# Mini Wallet Platform

A backend gaming platform with player registration, email verification, and a transaction-based wallet service.

## Prerequisites

- **Docker Desktop** (that's it — no .NET SDK or SQL Server on the host needed)

## Start everything

```bash
docker compose up --build
```

After the first build, subsequent starts skip the build step:

```bash
docker compose up
```

All services start with healthchecks and dependency ordering. First startup takes 2–3 minutes while SQL Server and Keycloak initialise.

## Service URLs

| Service | URL |
|---|---|
| Users API (Scalar UI) | http://localhost:5001/scalar |
| Wallet API (Scalar UI) | http://localhost:5002/scalar |
| Mailpit (email UI) | http://localhost:8025 |
| Keycloak admin console | http://localhost:8080 (admin / admin) |
| RabbitMQ management | http://localhost:15672 (guest / guest) |

## Sample credentials

| Role | Credential |
|---|---|
| Admin user | `admin@example.com` / `Admin123!` |
| Provider client | client id `provider-demo`, secret `provider-secret` |
| Player | register your own via `POST /v1/users` (see walkthrough below) |

## Token curl commands

**Provider token** (client credentials):

```bash
curl -s -X POST http://localhost:8080/realms/gaming/protocol/openid-connect/token \
  -d "grant_type=client_credentials" \
  -d "client_id=provider-demo" \
  -d "client_secret=provider-secret" \
  | jq -r .access_token
```

**Player token** (password grant — after registration and email verification):

```bash
curl -s -X POST http://localhost:8080/realms/gaming/protocol/openid-connect/token \
  -d "grant_type=password" \
  -d "client_id=wallet-web" \
  -d "username=your@email.com" \
  -d "password=YourPassword1!" \
  | jq -r .access_token
```

## Try it: full walkthrough

```
1. Register
   POST http://localhost:5001/v1/users
   { "email": "player@example.com", "password": "Player123!", "displayName": "Alice" }

2. Check email
   Open http://localhost:8025 — click the verification link in the email.

3. Verify
   POST http://localhost:5001/v1/users/verify
   { "token": "<token from the email>" }

4. Get a player token
   (use the curl command above with your credentials)

5. Check wallets
   GET http://localhost:5002/v1/players/me/wallets
   Authorization: Bearer <player token>

6. Top up
   POST http://localhost:5002/v1/wallet/topup
   Idempotency-Key: topup-001
   Authorization: Bearer <player token>
   { "currency": "USD", "amount": "100.00" }

7. Get a provider token
   (use the curl command above for provider-demo)

8. Place a BET
   POST http://localhost:5002/v1/transactions
   Authorization: Bearer <provider token>
   { "reference": "bet-001", "type": "BET", "playerId": "<player UUID>",
     "currency": "USD", "amount": "10.00" }

9. Record a WIN
   POST http://localhost:5002/v1/transactions
   Authorization: Bearer <provider token>
   { "reference": "win-001", "type": "WIN", "playerId": "<player UUID>",
     "currency": "USD", "amount": "25.00", "betReference": "bet-001" }

10. Check history and balance
    GET http://localhost:5002/v1/players/me/transactions?currency=USD
    GET http://localhost:5002/v1/players/me/wallets
    Authorization: Bearer <player token>
```

## Running the tests

**Wallet service** (103 tests):
```bash
dotnet test WalletService/WalletService.Tests
```

Covers: transaction processor (concurrency, idempotency, all error codes — `INSUFFICIENT_FUNDS`, `UNKNOWN_BET`, `BET_ALREADY_SETTLED`, `DUPLICATE_REFERENCE_MISMATCH`), API authorization (player vs other player, admin, provider vs player token, unverified email), and the MassTransit inbox consumer for wallet creation.

**Users service** (50 tests):
```bash
dotnet test UsersService/UsersService.Tests
```

Covers: registration flow, duplicate email handling, token verification (valid, expired, invalid), and outbox atomicity (registration is a single DB transaction — no half-committed state if the process crashes).

### Why these tests matter

**Concurrency test** (`Fifty_parallel_bets_of_10_on_balance_100_leave_exactly_ten_successes_and_balance_zero`): runs against a real SQL Server started by Testcontainers. Fifty parallel tasks fire the same player's wallet simultaneously. The guarded atomic `UPDATE Wallets SET Balance = Balance - @amount WHERE Balance >= @amount` means only 10 bets succeed; the balance ends at exactly zero. No distributed locks needed.

**Outbox atomicity test**: `PlayerRegistered` is written to the outbox inside the same DB transaction as the user row. Rolling back one rolls back both. The test proves that a crash between the two writes is impossible — either the full record appears or nothing does.
