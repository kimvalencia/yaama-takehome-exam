# AI Usage

## Tools

- **Claude Code** (VS Code extension, with the `superpowers` plugin): used throughout the project for brainstorming the initial design, writing the design spec (`docs/superpowers/specs/`), writing the implementation plan, and executing the plan via subagent-driven development. Each of the seven tasks was implemented by a fresh subagent, reviewed by a reviewer subagent, and a final whole-branch code review was run at the end.

## Prompts

### Prompt 1: kick-off and design discussion

Invoked with the `superpowers:brainstorming` skill so the design is discussed before any code is written.

> /superpowers:brainstorming read this file and lets discuss how to achieve this. include this in AI_USAGE.md as my first prompt

(The "file" is `candidate-brief_8155.md`, the take-home brief.)

**Why I started this way:** the brief leaves most decisions open (database, messaging, auth provider, API shape). I wanted to agree on the approach and trade-offs with the AI before generating code, instead of letting it pick defaults.

### Prompt 2: Directing a key design ruling

> "with the docker compose up. create a new docker image to cover this instead of connecting to my local db. A player should be blocked from logging in and topping up until they've verified their email"

**Why I used it this way:** this reversed the AI's default plan (connecting to my local SQL Server via `host.docker.internal`) and added a hard requirement that was not in the original brief. By stating it plainly mid-design, the AI incorporated both constraints before writing a single line of implementation code.

### Prompt 3: Scoping the API path

> "for the endpoint of topup, use /v1/wallet/topup"

**Why I used it this way:** the AI had proposed `/v1/players/me/topup`; I wanted the resource named `wallet` to match the mental model I had in mind. Naming the exact path prevented the AI from drifting toward a different convention and kept the API shape consistent with my intent.

### Prompt 4: Delegating execution

After reviewing the written design spec the user typed:

> "approve"

followed by:

> "subagent-driven"

**Why I used it this way:** approving the design before execution means I reviewed the AI's output at a natural checkpoint. "subagent-driven" is the keyword that tells the harness to run each task as an independent agent — keeping each task's context clean and making failures recoverable without re-running everything.

## Overrides

- **Database location:** the AI proposed (and I first accepted) connecting the containers to my local SQL Server via `host.docker.internal`. I reversed that: SQL Server runs as a container in the compose file instead. A reviewer would otherwise need their own SQL Server just to run `docker compose up`, which breaks the brief's "one command to run it".

- **Commit attribution model:** the AI harness set commit trailers to `Claude Opus 5.5`; the session instruction required `Claude Sonnet 5.5`. Several commits were amended after the fact to match the required attribution. The correct line is `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- **Top-up provider ID scoping:** the initial implementation used a global `player-topup` provider ID for top-up transactions, meaning any player could look up another player's top-up reference. It was changed to `topup:{playerId}` to scope idempotency per player.
- **Keycloak identity reuse:** the initial fix for "user creation crashed halfway" reused any existing Keycloak identity for the same email, including the seeded admin account. It was tightened to only reuse identities that are both disabled AND unverified.

## Trade-offs

- MassTransit's EF outbox adds latency to registration (the email and wallet creation happen asynchronously) but makes the system resilient to service outages. The alternative (synchronous wallet creation) would fail registration if Wallet is down.
- Keycloak as the identity provider adds operational complexity but means session management, token refresh, and future SSO/MFA are handled by a proven system rather than hand-rolled.
- Two SQL Server databases (`UsersDb` and `WalletDb`) in one container means one container outage takes both services down. The benefit is simpler local dev; in production they would be separate.
- The guarded atomic UPDATE avoids distributed locks but means the balance can only be read consistently inside a transaction. The API reads balance from the transaction row's `BalanceAfter` field to avoid a second query.

## Verification

- xUnit test suite: 103 wallet tests + 50 users tests, all passing. Run with `dotnet test`.
- Concurrency test (`Fifty_parallel_bets_of_10_on_balance_100_leave_exactly_ten_successes_and_balance_zero`) against a real Testcontainers SQL Server confirms the atomic UPDATE prevents overdrafts.
- Smoke script (`scripts/smoke.ps1` / `scripts/smoke.sh`) runs the full user journey against the live compose stack and exits 0 when all 10 steps pass.
- The smoke script is also the first test of JWT validation against live Keycloak (not mocked).
