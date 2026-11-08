# Mini Wallet Platform: Design

Status: DRAFT for approval. Items marked **(my call)** are decisions the AI made on the owner's behalf. Everything else was stated by the owner.

## 1. Intent

Build the backend for a small gaming platform as two .NET services (Users, Wallet), as specified in `candidate-brief_8155.md`. Scope is the brief's "What you'll build" section plus one owner addition: **player top-up**. Optional extras (SignalR etc.) are out of scope. Correctness and clarity beat feature count.

What the owner cares about most on the player side: players find their **remaining balance** and **transaction history** easily, and can **top up easily**.

## 2. Goals, non-goals, anti-goals

**Goals**
- Registration with email verification; wallets (USD, EUR, BTC, balance 0) appear automatically after registration.
- One provider transaction endpoint supporting BET and WIN, designed so new types (REFUND, BONUS, JACKPOT...) are added without reworking the endpoint.
- Players view their own balances and history; admins view anyone's.
- Providers authenticate as systems, not as players.
- Correct under concurrent requests across several Wallet instances; nothing lost or duplicated when either service is down.
- `docker compose up` starts everything.

**Non-goals**
- Real payments (top-up is simulated), SignalR, rate limiting, observability stack, production hardening beyond what `DESIGN.md` lists as "what I'd change".

**Anti-goals** (a failure even if it technically works)
- A balance that can go negative or be double-charged under retries/concurrency.
- A half-finished feature instead of a documented cut.
- Players able to call the provider endpoint, or read another player's wallet.

## 3. Decisions made by the owner

- Database is **Microsoft SQL Server**, run as a **container inside docker compose** (not the owner's local instance), so one command starts everything. This overrides an earlier plan to use the host's local SQL Server.
- Top-up is a **simulated endpoint** the player calls to add funds.
- A player **cannot log in or top up until the email is verified**.
- All other technical choices were delegated to the AI (below).

## 4. Architecture (my call unless stated)

```
Client / Provider --> nginx (LB) --> wallet-api x2 --> SQL Server (WalletDb)
Client --> users-api --> SQL Server (UsersDb), Keycloak admin API, Mailpit (SMTP)
users-api --(outbox)--> RabbitMQ --> wallet-api (consumer, creates wallets)
All APIs validate JWTs issued by Keycloak.
```

Compose services: `sqlserver`, `rabbitmq`, `keycloak` (with imported realm), `mailpit`, `users-api`, `wallet-api` (2 replicas), `nginx`. Healthchecks and startup retries so services wait for SQL Server/RabbitMQ. Databases are created/migrated on startup (EF Core migrations). Each service owns its own database.

Stack: .NET 10 (the existing scaffolds target net10.0; the brief allows ".NET 8 or later"), ASP.NET Core controllers, EF Core, Scalar + OpenAPI (already installed), xUnit, MassTransit 8.x with its EF outbox/inbox over RabbitMQ (the builder may swap for hand-written outbox if MassTransit proves heavy).

## 5. Identity and access

- Keycloak realm `gaming` imported at startup, with sample users/clients documented in the README.
- Roles: `player`, `admin`. Providers are Keycloak **confidential clients using client credentials**, with role `game-provider`; their client id is the `providerId`. Players never receive provider credentials.
- Users service creates the player in Keycloak at registration as **disabled** with `emailVerified=false`. Verification enables the account. This is how "blocked from logging in until verified" is enforced. As defence in depth, the top-up endpoint also rejects tokens without `email_verified=true`.
- Players access only their own data (`me`); requesting another player's id returns 403. Admins can access any player.

## 6. Users service

- `POST /v1/users` (register: email, password, display name). Validates, creates the Keycloak user (reusing an existing one for the same email if a previous attempt crashed halfway), then in **one DB transaction** saves the user, a hashed expiring verification token, and a `PlayerRegistered` outbox message. Sends the verification email via Mailpit (inspect at its web UI).
- `POST /v1/users/verify` (token): enables the Keycloak account, marks the user verified. Idempotent; expired/invalid tokens return a problem response.
- Wallet creation is triggered by registration (as the brief words it), not verification. Wallets of unverified players exist but cannot be used (no token, and the top-up check).

## 7. Wallet service

### Data
- `Wallets(Id, PlayerId, Currency, Balance decimal(28,8), RowVersion)`: unique `(PlayerId, Currency)`.
- `Transactions(Id, WalletId, ProviderId, Reference, Type, Amount, BalanceAfter, RelatedTransactionId?, CreatedAt)`: unique `(ProviderId, Reference)`. Top-ups use provider id `player-topup` and the client's idempotency key as the reference.
- Amounts are positive decimals; direction comes from the type. Currency precision is enforced (USD/EUR 2 decimals, BTC 8).

### Correct balance changes
Each transaction runs in one DB transaction: insert the transaction row, then a guarded update, e.g. `UPDATE Wallets SET Balance = Balance - @amount WHERE Id = @id AND Balance >= @amount`. Zero rows updated means insufficient funds, so nothing is committed and a 422 is returned. The database applies the change atomically, so concurrent requests across instances cannot overdraw. No in-memory locks.

### Idempotency
`reference` is the provider's unique id for the transaction. The unique index makes retries safe: same reference + same payload returns the original result (HTTP 200, flagged as a replay); same reference + different payload returns 409. If two identical requests race, the loser hits the unique index and returns the winner's result.

### Extensibility
Each transaction type is a handler registered by type (direction, validation rules, related-transaction rules). BET (debit) and WIN (credit, must reference an existing BET of the same player and currency) ship now. DEPOSIT (credit) backs top-up. REFUND/BONUS/JACKPOT are added by adding a handler. Types arrive as strings in the contract, so adding one does not change the schema.

### Rules for WIN (my call)
`betReference` must name an existing BET of the same provider, player and currency; a BET can be settled by at most one WIN. Otherwise 422 with a specific error code.

### Wallet creation
The consumer creates the three wallets for a `PlayerRegistered` message. A unique `(PlayerId, Currency)` index plus the inbox make redelivery harmless.

## 8. Wallet API contract (v1, my call)

All paths are prefixed `/v1`. Errors use RFC 7807 `application/problem+json` with a stable machine-readable `code` (`INSUFFICIENT_FUNDS`, `UNKNOWN_PLAYER`, `UNKNOWN_BET`, `BET_ALREADY_SETTLED`, `DUPLICATE_REFERENCE_MISMATCH`, `EMAIL_NOT_VERIFIED`, `VALIDATION_FAILED`). Money is sent as JSON strings (e.g. `"12.50"`) to avoid float issues.

| Caller | Endpoint | Purpose |
|---|---|---|
| Provider | `POST /v1/transactions` | The single transaction endpoint. Body: `reference`, `type`, `playerId`, `currency`, `amount`, `betReference` (WIN only). Returns the transaction and the new balance. 201 created / 200 replay / 404 unknown player / 409 / 422 / 401 / 403. |
| Player | `GET /v1/players/me/wallets` | All balances. |
| Player | `GET /v1/players/me/transactions` | History, newest first, cursor pagination (`limit`, `cursor`), optional `currency` and `type` filters. |
| Player | `POST /v1/wallet/topup` | Simulated top-up. Body: `currency`, `amount`; required `Idempotency-Key` header. Requires verified email. |
| Admin | `GET /v1/players/{playerId}/wallets`, `.../transactions` | Same views for any player. |

OpenAPI documents each endpoint, the auth scheme (OAuth2 client-credentials for providers, password/authorization-code for players via Keycloak), examples and error codes. Scalar UI is the interactive explorer, with the "how to authenticate" steps in the API description. Versioning is by URL prefix.

## 9. Failure scenarios (to be written up in `DESIGN.md`)

| Scenario | Handling |
|---|---|
| Wallet down during registration | Outbox keeps the event; RabbitMQ delivers when Wallet returns. Registration itself succeeds. |
| Users down | No registration is accepted; clients retry. Nothing is half-done thanks to the single DB transaction. |
| RabbitMQ down | Outbox accumulates; the publisher retries. |
| Duplicate/redelivered message | Unique indexes and the inbox make wallet creation idempotent. |
| Provider times out and retries | Same `reference` returns the original result; no double debit. |
| Concurrent bets, same player, different instances | Guarded atomic update; the second bet is rejected if funds are short. |
| Crash between Keycloak user creation and DB commit | Retry reuses the Keycloak user; no duplicate account. |
| SQL Server not ready at boot | Retry on startup plus compose healthchecks. |

## 10. Testing (to justify in README)

- Concurrency test against a real SQL Server (Testcontainers): many parallel bets cannot overdraw; parallel duplicate references produce one transaction.
- Handler unit tests for BET/WIN rules and money precision.
- API tests for authorization: player vs other player, admin, provider vs player token, unverified email.
- Outbox/consumer idempotency test: replayed `PlayerRegistered` creates exactly three wallets.

## 11. Left to the builder

Project layout and naming, EF migration details, exact Keycloak realm JSON, nginx config, sample data, README wording.

## 12. Known simplifications (go in `DESIGN.md`)

Simulated top-up and email; one SQL Server instance for both databases (separate DBs); no rate limiting, auditing or secrets management; nginx instead of a cloud load balancer; no balance reconciliation job; no SignalR.
