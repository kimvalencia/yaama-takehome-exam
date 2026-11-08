# Mini Wallet Platform Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two .NET services (Users, Wallet) behind `docker compose up`: registration with email verification, automatic USD/EUR/BTC wallets, a provider transaction endpoint (BET/WIN), player balances/history/top-up, and admin access.

**Architecture:** Wallet is the core: every balance change is one DB transaction containing a guarded `UPDATE` plus a unique-indexed transaction row, so concurrency and retries are safe without locks. Users publishes `PlayerRegistered` through a MassTransit EF outbox over RabbitMQ; Wallet consumes it idempotently. Keycloak issues all JWTs; SQL Server, RabbitMQ, Mailpit, nginx (2 Wallet replicas) all run in compose.

**Tech Stack:** .NET 10 (existing scaffolds), ASP.NET Core controllers, EF Core (SqlServer), MassTransit 8.x + RabbitMQ, Keycloak, Mailpit, nginx, Scalar/OpenAPI, xUnit + Testcontainers.MsSql.

**Spec:** `docs/superpowers/specs/2026-11-09-mini-wallet-platform-design.md`

## Global Constraints

- C# on .NET 8 or later (scaffolds are `net10.0`); ASP.NET Core Web API with controllers; OpenAPI + Scalar UI.
- Database is SQL Server in a compose container (never the host's); one database per service (`UsersDb`, `WalletDb`); migrations applied on startup with retry.
- All JWTs come from Keycloak realm `gaming`. Roles: `player`, `admin`, `game-provider` (client credentials; `azp` claim = providerId). Player id = token `sub`.
- Unverified players cannot log in (Keycloak user created disabled) and `POST /v1/wallet/topup` also rejects tokens lacking `email_verified=true`.
- API prefix `/v1`; errors are RFC 7807 `application/problem+json` with a stable `code`: `INSUFFICIENT_FUNDS`, `UNKNOWN_PLAYER`, `UNKNOWN_BET`, `BET_ALREADY_SETTLED`, `DUPLICATE_REFERENCE_MISMATCH`, `EMAIL_NOT_VERIFIED`, `VALIDATION_FAILED`.
- Money is a JSON string (`"12.50"`), stored `decimal(28,8)`; USD/EUR allow 2 decimals, BTC 8; amounts must be > 0.
- Idempotency: unique `(ProviderId, Reference)`. Same reference + same payload → 200 replay; different payload → 409.
- Wallet endpoints: `POST /v1/transactions` (provider), `GET /v1/players/me/wallets`, `GET /v1/players/me/transactions` (cursor paging, filters `currency`, `type`), `POST /v1/wallet/topup` (header `Idempotency-Key` required), admin `GET /v1/players/{playerId}/wallets` and `.../transactions`. Another player's id → 403.
- Users endpoints: `POST /v1/users` (register), `POST /v1/users/verify`.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Out of scope: SignalR, real payments, rate limiting, observability.

## Review Focus

1. Amount `"0"`, `"-5"`, `"abc"`, `"1.005"` (USD) or 9 BTC decimals → 400 `VALIDATION_FAILED`, never a 500. (Task 3)
2. Currency/type strings in the wrong case or unknown (`"usd"`, `"bet"`, `"REFUND"`, `"DOGE"`) → 400 with a clear message; decide case-insensitive for currency and type, normalised to upper-case. (Task 3)
3. History paging with a garbage `cursor`, `limit=0` or `limit=100000` → 400 for garbage; limit clamped to 1..100 (default 20). (Task 3)
4. Registering the same email twice (including different casing) → 409, no second Keycloak user. (Task 4)
5. Verifying with an expired, unknown or already-used token → 400, and re-verifying a verified account stays successful. (Task 4)

---

### Task 1: Repo clean-up and infrastructure compose

**Files:**
- Modify: `.gitignore` (add `**/bin/`, `**/obj/`, `**/.vs/`, `.env`); run `git rm -r --cached` on tracked `obj/` and `.vs/` folders
- Delete: `UsersService/UsersService/{Controllers/WeatherForecastController.cs,WeatherForecast.cs}`, same two files under `WalletService/WalletService/`
- Create: `docker-compose.yml`, `.env.example`, `infra/keycloak/gaming-realm.json`

**Interfaces:**
- Produces: compose services `sqlserver` (sa password from `.env`), `rabbitmq`, `keycloak` (imports realm), `mailpit` (SMTP 1025, UI 8025). Realm: user `admin@example.com` (role `admin`, verified), public client `wallet-web` (direct-access grants on, for password-grant sample tokens), confidential client `provider-demo` (secret `provider-secret`, service account with role `game-provider`), confidential client `users-api` (service account with realm-management `manage-users`), and a protocol mapper adding a flat `roles` claim (realm roles) to access tokens.

- [ ] **Step 1:** Untrack build output, update `.gitignore`, delete the WeatherForecast files, confirm both projects still `dotnet build`.
- [ ] **Step 2:** Write `docker-compose.yml` with the four infra services, healthchecks, and `.env.example` (`MSSQL_SA_PASSWORD`).
- [ ] **Step 3:** Write the realm JSON as described above.
- [ ] **Step 4: Verify:** `docker compose up -d sqlserver rabbitmq keycloak mailpit`, then
  `curl -d "grant_type=client_credentials&client_id=provider-demo&client_secret=provider-secret" http://localhost:8080/realms/gaming/protocol/openid-connect/token` returns an `access_token` whose payload contains `"roles":["game-provider"]` (decode it). Expected: PASS.
- [ ] **Step 5: Commit** `chore: clean repo and add infrastructure compose with Keycloak realm`

### Task 2: Wallet domain, persistence and the transaction processor

**Files:**
- Create: `WalletService/WalletService/Domain/{Currency.cs,Wallet.cs,WalletTransaction.cs,ErrorCodes.cs}`, `Data/WalletDbContext.cs` (+ migration `InitialCreate`), `Transactions/{ITransactionHandler.cs,BetHandler.cs,WinHandler.cs,DepositHandler.cs,TransactionProcessor.cs,TransactionCommand.cs}`
- Create: `WalletService/WalletService.Tests/WalletService.Tests.csproj` (xUnit, Testcontainers.MsSql, project reference), `SqlServerFixture.cs`, `TransactionProcessorTests.cs`
- Modify: `WalletService.csproj` (EF Core SqlServer + Design)

**Interfaces:**
- Produces:
  - `enum Currency { USD, EUR, BTC }`; `static class CurrencyRules { static int Decimals(Currency); static bool TryParse(string? s, out Currency c) /* case-insensitive */; }`
  - `class Wallet { Guid Id; Guid PlayerId; Currency Currency; decimal Balance; byte[] RowVersion; }`
  - `class WalletTransaction { Guid Id; long Seq /*identity*/; Guid WalletId; string ProviderId; string Reference; string Type; decimal Amount; decimal BalanceAfter; Guid? RelatedTransactionId; DateTimeOffset CreatedAt; }`
  - `record TransactionCommand(string ProviderId, string Reference, string Type, Guid PlayerId, Currency Currency, decimal Amount, string? BetReference)`
  - `enum ProcessStatus { Created, Replayed, Rejected }`; `record ProcessResult(ProcessStatus Status, WalletTransaction? Transaction, string? ErrorCode)`
  - `enum Direction { Debit, Credit }`; `interface ITransactionHandler { string Type { get; } Direction Direction { get; } Task<(string? ErrorCode, Guid? RelatedTransactionId)> ValidateAsync(TransactionCommand c, WalletDbContext db, CancellationToken ct); }`
  - `class TransactionProcessor(WalletDbContext db, IEnumerable<ITransactionHandler> handlers) { Task<ProcessResult> ProcessAsync(TransactionCommand c, CancellationToken ct); }` Unknown `Type` → `Rejected` with `VALIDATION_FAILED`.
  - Wallets table: unique `(PlayerId, Currency)`; transactions: unique `(ProviderId, Reference)`.

- [ ] **Step 1: Write failing tests** (xUnit, real SQL Server via fixture; seed one wallet per test):
  `Bet_debits_balance_and_records_balance_after`; `Bet_over_balance_is_rejected_with_INSUFFICIENT_FUNDS_and_balance_unchanged`; `Same_reference_same_payload_replays_without_second_debit`; `Same_reference_different_amount_is_rejected_DUPLICATE_REFERENCE_MISMATCH`; `Win_requires_existing_bet_of_same_player_and_currency` (`UNKNOWN_BET`); `Second_win_for_same_bet_is_rejected_BET_ALREADY_SETTLED`; `Unknown_wallet_is_rejected_UNKNOWN_PLAYER`; `Deposit_credits_balance`.
  Concurrency (the key test): `Fifty_parallel_bets_of_10_on_balance_100_leave_exactly_ten_successes_and_balance_zero` (each task uses its own `WalletDbContext`/processor on the same DB); `Twenty_parallel_identical_requests_create_one_transaction_and_one_debit`.
- [ ] **Step 2:** Run `dotnet test WalletService/WalletService.Tests` → FAIL (types missing).
- [ ] **Step 3:** Implement the domain, DbContext (precision `decimal(28,8)`, indexes above) and migration.
- [ ] **Step 4:** Implement `TransactionProcessor.ProcessAsync`: fast-path lookup by `(ProviderId, Reference)` → replay/mismatch; otherwise begin a DB transaction, run handler validation, apply the guarded update via `ExecuteUpdateAsync` (`WHERE Id=@id AND (Debit ⇒ Balance >= @amount)`; zero rows ⇒ `INSUFFICIENT_FUNDS`), read the new balance in the same transaction, insert the transaction row. On unique-index violation (SQL error 2601/2627) roll back and re-run the lookup path. Implement the three handlers per the spec rules (BET debit; WIN credit with bet-lookup rules; DEPOSIT credit).
- [ ] **Step 5:** Run tests → all PASS.
- [ ] **Step 6: Commit** `feat(wallet): transaction processor with idempotency and atomic balance updates`

### Task 3: Wallet API, auth and OpenAPI contract

**Files:**
- Create: `WalletService/WalletService/Api/{TransactionsController.cs,PlayerWalletController.cs,TopupController.cs,AdminWalletController.cs,Dtos.cs,ProblemResults.cs}`, `Auth/{AuthExtensions.cs,Policies.cs}`, `Api/OpenApiSetup.cs`
- Modify: `Program.cs` (JWT, policies, problem details, DI, OpenAPI+Scalar in all environments, DB migrate-with-retry on start), `appsettings.json` (`Auth:Authority`, `Auth:MetadataAddress`, `Auth:ValidIssuer`)
- Test: `WalletService.Tests/Api/{TransactionsApiTests.cs,PlayerApiTests.cs,AuthorizationTests.cs}` using `WebApplicationFactory` with the SQL fixture and a test auth handler that injects claims (`sub`, `roles`, `azp`, `email_verified`)

**Interfaces:**
- Consumes: `TransactionProcessor`, `CurrencyRules`, `ErrorCodes` from Task 2.
- Produces: policies `Provider` (role `game-provider`), `Player` (role `player`), `Admin` (role `admin`), `VerifiedPlayer` (player + `email_verified=true`). DTOs: `TransactionRequest(string Reference, string Type, Guid PlayerId, string Currency, string Amount, string? BetReference)`, `TransactionResponse(Guid Id, string Reference, string Type, string Currency, string Amount, string BalanceAfter, string? BetReference, DateTimeOffset CreatedAt, bool Replayed)`, `WalletResponse(string Currency, string Balance)`, `TransactionPage(IReadOnlyList<TransactionResponse> Items, string? NextCursor)` (cursor = base64 of last `Seq`), `TopupRequest(string Currency, string Amount)`.
- Auth wiring: validate against `MetadataAddress` (`http://keycloak:8080/...`) but accept issuer `http://localhost:8080/realms/gaming` (the host-visible issuer), `RoleClaimType = "roles"`.

- [ ] **Step 1: Write failing tests:** provider POST returns 201 then 200 `Replayed=true`; 422 `INSUFFICIENT_FUNDS` problem body has `code`; player token on `POST /v1/transactions` → 403; provider token on player routes → 403; `GET /v1/players/me/wallets` returns only the caller's three wallets; other player's id on admin route → 403 for player, 200 for admin; top-up credits and appears in history as `DEPOSIT`; top-up without `Idempotency-Key` → 400; top-up with `email_verified=false` → 403 `EMAIL_NOT_VERIFIED`; history newest-first, pages via `NextCursor`, filters by `currency`/`type`. Plus the Review Focus tests 1–3 (invalid amounts, wrong-case/unknown currency and type, bad cursor/limit).
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement controllers over the interfaces above; map `ProcessResult` to 201/200/404/409/422 and validation to 400 via `ProblemDetails` with an extension `code`; top-up uses provider id `player-topup` and the `Idempotency-Key` as reference.
- [ ] **Step 4:** `OpenApiSetup`: describe OAuth2 (client credentials for providers, password flow for players, token URL `http://localhost:8080/realms/gaming/protocol/openid-connect/token`), per-endpoint examples and error codes, and an API description section "How to authenticate" with the sample `curl` for provider and player tokens.
- [ ] **Step 5:** Run tests → PASS. Manually open `/scalar` and confirm the auth section and examples render.
- [ ] **Step 6: Commit** `feat(wallet): v1 API with provider, player, admin and top-up endpoints`

### Task 4: Users service: registration and email verification

**Files:**
- Create: `UsersService/UsersService/Domain/{User.cs,VerificationToken.cs}`, `Data/UsersDbContext.cs` (+ migration), `Identity/{IIdentityProvider.cs,KeycloakIdentityProvider.cs}`, `Email/{IEmailSender.cs,SmtpEmailSender.cs}`, `Registration/RegistrationService.cs`, `Api/UsersController.cs`
- Create: `UsersService/UsersService.Tests/` (xUnit, SQL fixture, fakes for `IIdentityProvider`/`IEmailSender`), `RegistrationServiceTests.cs`
- Modify: `UsersService.csproj`, `Program.cs` (DI, migrate-with-retry, OpenAPI+Scalar, problem details)

**Interfaces:**
- Produces:
  - `User { Guid Id /* = Keycloak user id */; string Email /* lower-cased */; string DisplayName; DateTimeOffset? EmailVerifiedAt; DateTimeOffset CreatedAt }`; `VerificationToken { Guid Id; Guid UserId; string TokenHash /* SHA-256 */; DateTimeOffset ExpiresAt; DateTimeOffset? UsedAt }` (24 h expiry); unique index on `User.Email`.
  - `interface IIdentityProvider { Task<Guid> CreateDisabledUserAsync(string email, string password, string displayName, CancellationToken ct) /* returns existing id if a previous attempt created it */; Task EnableUserAsync(Guid userId, CancellationToken ct); }`
  - `interface IEmailSender { Task SendVerificationAsync(string email, string verifyUrl, CancellationToken ct); }`
  - `RegistrationService`: `Task<RegisterResult> RegisterAsync(RegisterRequest r, CancellationToken ct)` (`RegisterRequest(string Email, string Password, string DisplayName)`; result carries `UserId` or `ErrorCode`), `Task<VerifyResult> VerifyAsync(string token, CancellationToken ct)`.
  - Registration in one DB transaction saves user + token hash + (Task 5) outbox messages; Keycloak user is created first. `POST /v1/users` → 202 `{userId}`; duplicate email → 409; weak/invalid input → 400. `POST /v1/users/verify {token}` → 204.

- [ ] **Step 1: Write failing tests:** `Register_creates_user_and_disabled_identity`; `Register_same_email_twice_returns_conflict_and_creates_one_identity`; `Register_email_matching_is_case_insensitive`; `Register_reuses_identity_when_previous_attempt_left_one_behind`; `Verify_enables_identity_and_marks_user_verified`; `Verify_twice_still_succeeds`; `Verify_expired_or_unknown_or_used_token_fails`.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement. `KeycloakIdentityProvider` uses the `users-api` client credentials against the Keycloak admin REST API (`/admin/realms/gaming/users`), created with `enabled=false`, `emailVerified=false`, password credential non-temporary; `EnableUserAsync` sets both flags true. Random 32-byte URL-safe token; only its hash is stored.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5: Commit** `feat(users): registration and email verification`

### Task 5: Messaging: outbox in Users, consumer in Wallet

**Files:**
- Create: `UsersService/UsersService/Contracts/PlayerRegistered.cs` and `Contracts/VerificationEmailRequested.cs`; `Messaging/SendVerificationEmailConsumer.cs`
- Create: `WalletService/WalletService/Contracts/PlayerRegistered.cs` (identical, `namespace Contracts;` in both services; deliberately duplicated so each service builds from its own folder), `Messaging/PlayerRegisteredConsumer.cs`
- Modify: both `Program.cs` and `*.csproj` (MassTransit 8.x, RabbitMQ transport, EF outbox/inbox on each DbContext, new migrations); `RegistrationService` to publish through the outbox in its transaction
- Test: `WalletService.Tests/PlayerRegisteredConsumerTests.cs`, `UsersService.Tests/OutboxTests.cs`

**Interfaces:**
- Produces: `record PlayerRegistered(Guid PlayerId, DateTimeOffset RegisteredAt)`; `record VerificationEmailRequested(Guid UserId, string Email, string VerifyUrl)` (consumed inside Users so a failed SMTP send is retried rather than lost). Wallet consumer creates `USD`, `EUR`, `BTC` wallets at 0 for the player, skipping ones that exist.

- [ ] **Step 1: Write failing tests:** consuming `PlayerRegistered` creates exactly three zero-balance wallets; consuming the same message twice still leaves three; registering a user writes one `PlayerRegistered` and one `VerificationEmailRequested` outbox row in the same transaction as the user; a failed registration transaction leaves no outbox rows.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Wire MassTransit (retry with exponential backoff, durable queues) and implement the consumers.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Run `docker compose up -d` infra, start both APIs locally, register a user, confirm the Mailpit UI (`http://localhost:8025`) shows the email, open the verify link, confirm three wallets exist and that stopping Wallet before registering then starting it afterwards still yields the wallets.
- [ ] **Step 6: Commit** `feat: outbox/inbox messaging for wallet creation and verification email`

### Task 6: Full compose, load balancer and startup resilience

**Files:**
- Modify: both `Dockerfile`s (drop VS-specific `USER $APP_UID` issues only if they break the run; add `curl`-free health endpoint), `docker-compose.yml`
- Create: `infra/nginx/nginx.conf`, `WalletService/WalletService/Api/HealthController.cs` (`GET /health`), same in Users
- Test: `scripts/smoke.ps1` and `scripts/smoke.sh`

**Interfaces:**
- Produces: services `users-api` (port 5001), `wallet-api` (`deploy.replicas: 2`, no published port), `nginx` (port 5002 → wallet-api round-robin); `depends_on` with healthchecks; services retry DB/RabbitMQ connection at boot. Keycloak/RabbitMQ/Mailpit/SQL ports published for demo.

- [ ] **Step 1:** Implement the compose additions, nginx upstream and health endpoints.
- [ ] **Step 2:** Write the smoke script: register → read email from Mailpit API (`/api/v1/messages`) → verify → password-grant token → `GET /v1/players/me/wallets` shows three zero wallets → top-up 100 USD → provider token → BET 30 → WIN → history lists 3 entries and balance is the expected value.
- [ ] **Step 3: Verify:** from a clean state, `docker compose up --build` (single command) and run the smoke script. Expected: every step prints OK and the script exits 0. Also run it with `docker compose stop wallet-api` during registration and again after restart: wallets still appear exactly once.
- [ ] **Step 4: Commit** `feat: full docker compose with two wallet replicas behind nginx`

### Task 7: Documentation

**Files:**
- Create: `README.md`, `DESIGN.md` (max 2 pages)
- Modify: `AI_USAGE.md`

- [ ] **Step 1:** `README.md`: prerequisites (Docker), the single command, URLs (Scalar for each service, Mailpit, Keycloak), sample credentials (admin user, how to register a player, `provider-demo`/`provider-secret`), copy-paste token `curl`s, a try-it walkthrough, test instructions, and why those tests were chosen.
- [ ] **Step 2:** `DESIGN.md`: architecture and reasons, the failure-scenario table from the spec §9, and the simplification/production list from spec §12.
- [ ] **Step 3:** `AI_USAGE.md`: fill Tools, 3–5 representative prompts (Prompt 1 already present), overrides (database location, plus any others that occurred), trade-offs, verification (tests, concurrency test, smoke script).
- [ ] **Step 4:** Walk the README from a clean clone on a fresh `docker compose up` to confirm it is accurate.
- [ ] **Step 5: Commit** `docs: README, DESIGN and AI_USAGE`

---

## Self-Review

- **Spec coverage:** registration/verify (T4), wallets on registration (T5), BET/WIN/extensibility and idempotency (T2), balances/history/top-up/admin/provider auth (T3), concurrency and outage handling (T2, T5, T6), one-command run (T1, T6), docs and AI usage (T7). Spec §10 tests map to T2 (concurrency), T3 (authorization), T5 (idempotent consumer).
- **Type consistency:** `TransactionCommand`, `ProcessResult`, `ErrorCodes`, policy names and DTOs are defined once in Tasks 2–3 and reused unchanged.
- **Known risk:** Keycloak issuer mismatch between `localhost` (tokens) and `keycloak` (container-internal metadata) is handled explicitly in Task 3's auth wiring.
