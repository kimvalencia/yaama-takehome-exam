# Design

## Architecture

Two .NET 10 services with separate concerns:

- **Users service** owns identity: registration, email verification, Keycloak provisioning.
- **Wallet service** owns money: balances, transaction history, provider transactions, player top-up.

```
Client / Provider --> nginx (load balancer) --> wallet-api (2 replicas) --> WalletDb (SQL Server)
Client             --> users-api             --> UsersDb (SQL Server), Keycloak, Mailpit
users-api --(EF outbox)--> RabbitMQ --> wallet-api inbox consumer (creates wallets)
All services validate JWTs issued by Keycloak (realm: gaming).
```

The two services have different failure modes — Users can be down while Wallet continues processing provider transactions, and vice versa. Keeping them separate means a Users outage does not block providers. nginx distributes load across the two Wallet replicas without each replica needing to know about the other.

## Key decisions

**MassTransit EF outbox/inbox.** Wallet creation is triggered by registration. A synchronous call from Users to Wallet at registration time would fail registration if Wallet is down. Instead, `PlayerRegistered` is written to the outbox table inside the same DB transaction as the user row (atomic: either both are committed or neither is). MassTransit publishes the message when SQL Server is reachable and RabbitMQ is up. On the Wallet side, the inbox makes delivery idempotent: a re-delivered message is recognised and discarded.

**Guarded atomic UPDATE.** Each debit runs `UPDATE Wallets SET Balance = Balance - @amount WHERE Id = @id AND Balance >= @amount`. If zero rows are updated, the transaction is rolled back and the caller gets 422 `INSUFFICIENT_FUNDS`. Because the database enforces the invariant atomically, two concurrent debits on the same wallet (across different replicas) cannot both succeed when only one has sufficient funds. No application-level locks are needed.

**Keycloak disabled-until-verified.** The player account is created in Keycloak as *disabled* at registration. Verification enables it. This means the identity layer enforces the email gate — a player cannot obtain a JWT until verified, regardless of which code path is called. As defence in depth, the top-up endpoint also rejects tokens where `email_verified=false`.

**Top-up as DEPOSIT.** Top-up is handled by the same transaction processor as BET and WIN. This means top-ups share the same idempotency guarantee (the `Idempotency-Key` header maps to the reference), the same audit trail, and the same `BalanceAfter` accounting. The provider id is scoped per player (`topup:{playerId}`) so the idempotency key namespace is isolated between players.

## Failure scenarios

| Scenario | Handling |
|---|---|
| Wallet down during registration | Outbox keeps the event; RabbitMQ delivers when Wallet returns. Registration itself succeeds. |
| Users down | No registration is accepted; clients retry. Nothing is half-done thanks to the single DB transaction. |
| RabbitMQ down | Outbox accumulates; the publisher retries. |
| Duplicate/redelivered message | Unique indexes and the inbox make wallet creation idempotent. |
| Provider times out and retries | Same `reference` returns the original result; no double debit. |
| Concurrent bets, same player, different instances | Guarded atomic update; the second bet is rejected if funds are short. |
| Crash between Keycloak user creation and DB commit | Retry reuses the Keycloak user (only if disabled and unverified); no duplicate account. |
| SQL Server not ready at boot | Retry on startup plus compose healthchecks. |
| `PlayerRegistered` published before Wallet queue exists | Users declares the queue topology at startup; message is queued immediately. |
| Message retry exhausted (10 attempts, ~25 min) | Message is parked in `<queuename>_error`; operator moves it back via RabbitMQ management UI or `rabbitmqadmin move messages`. |

## Known simplifications

- Simulated top-up and email (Mailpit, no real SMTP or payment gateway).
- One SQL Server container serves both databases (`UsersDb` and `WalletDb`); one outage takes both down.
- No rate limiting, auditing, or secrets management (passwords in `.env`).
- Verification token stored raw in the outbox table body — acceptable for a demo.
- Reference uniqueness follows the database collation (case-insensitive by default) — not a documented contract.
- nginx replaces a cloud load balancer.
- Keycloak bootstrapped with `admin`/`admin` — dev only, change before production.
- `firstName = lastName = displayName` workaround because Keycloak's person-name validator rejects some characters.
- Account enumeration is possible via a 409 on duplicate registration.

## What I'd change for production

- Replace simulated top-up with a real payment gateway (Stripe, Adyen).
- Secrets management (Vault, AWS Secrets Manager) — no plaintext credentials in `.env`.
- Rate limiting per player and per provider, plus an observability stack (Prometheus, Grafana, structured logs).
- SQL Server high-availability or a managed equivalent (Azure SQL, Aurora); separate instances per service.
- Automated dead-letter queue processor instead of manual `rabbitmqadmin` intervention.
- HTTPS / TLS termination at nginx with a real certificate.
