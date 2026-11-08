#!/usr/bin/env bash
# End-to-end smoke test for the Mini Wallet Platform.
set -euo pipefail

USERS_BASE="http://localhost:5001"
WALLET_BASE="http://localhost:5002"
KEYCLOAK_BASE="http://localhost:8080"
MAILPIT_BASE="http://localhost:8025"

step() { printf '[%s] ' "$1"; }
ok()   { echo "OK"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

require_cmd() { command -v "$1" >/dev/null 2>&1 || fail "Required command not found: $1"; }
require_cmd curl
require_cmd jq

# ---- Step 1: Register a new user ----
step "1 Register user"
EMAIL="smoketest_$(date +%s)@example.com"
PASSWORD="Smoke1Test!"
DISPLAY="Smoke User"

curl -sf -X POST "$USERS_BASE/v1/users" \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\",\"displayName\":\"$DISPLAY\"}" \
  > /dev/null
ok

# ---- Step 2: Find verification email ----
step "2 Find verification email"
MSG_ID=""
DEADLINE=$(( $(date +%s) + 30 ))
while [ -z "$MSG_ID" ] && [ "$(date +%s)" -lt "$DEADLINE" ]; do
  sleep 2
  MSG_ID=$(curl -sf "$MAILPIT_BASE/api/v1/messages" \
    | jq -r --arg e "$EMAIL" '.messages[] | select(.To[].Address == $e) | .ID' \
    | head -1 || true)
done
[ -n "$MSG_ID" ] || fail "No verification email found for $EMAIL within 30s"
ok

# ---- Step 3: Extract token and verify ----
step "3 Verify email"
EMAIL_BODY=$(curl -sf "$MAILPIT_BASE/api/v1/message/$MSG_ID" | jq -r '.HTML // .Text')
# Extract token from ?token=XXXX or &token=XXXX
VERIFY_TOKEN=$(printf '%s' "$EMAIL_BODY" | grep -oP '(?<=[?&]token=)[A-Za-z0-9_\-]+' | head -1 || true)
[ -n "$VERIFY_TOKEN" ] || fail "Could not extract verify token from email"
curl -sf "$USERS_BASE/v1/users/verify?token=$VERIFY_TOKEN" > /dev/null
ok

# ---- Step 4: Get player token ----
step "4 Get player token"
EMAIL_ENC=$(python3 -c "import urllib.parse; print(urllib.parse.quote('$EMAIL'))" 2>/dev/null \
  || python -c "import urllib; print(urllib.quote('$EMAIL'))")
PASS_ENC=$(python3 -c "import urllib.parse; print(urllib.parse.quote('$PASSWORD'))" 2>/dev/null \
  || python -c "import urllib; print(urllib.quote('$PASSWORD'))")
PLAYER_TOKEN=$(curl -sf -X POST \
  "$KEYCLOAK_BASE/realms/gaming/protocol/openid-connect/token" \
  -d "grant_type=password&client_id=wallet-web&username=${EMAIL_ENC}&password=${PASS_ENC}" \
  | jq -r '.access_token')
[ -n "$PLAYER_TOKEN" ] && [ "$PLAYER_TOKEN" != "null" ] || fail "No player token"
ok

# ---- Step 5: GET wallets ----
step "5 Get wallets"
WALLETS=$(curl -sf "$WALLET_BASE/v1/players/me/wallets" \
  -H "Authorization: Bearer $PLAYER_TOKEN")
COUNT=$(echo "$WALLETS" | jq 'length')
[ "$COUNT" -eq 3 ] || fail "Expected 3 wallets, got $COUNT"
USD_BAL=$(echo "$WALLETS" | jq -r '.[] | select(.currency=="USD") | .balance')
[ "$USD_BAL" = "0.00" ] || [ "$USD_BAL" = "0" ] || fail "Expected USD balance 0, got $USD_BAL"
ok

# ---- Step 6: Top-up 100 USD ----
# Amount is a string per the API contract
step "6 Top-up 100 USD"
IDEM_KEY=$(cat /proc/sys/kernel/random/uuid 2>/dev/null || python3 -c "import uuid; print(uuid.uuid4())")
curl -sf -X POST "$WALLET_BASE/v1/wallet/topup" \
  -H "Authorization: Bearer $PLAYER_TOKEN" \
  -H "Idempotency-Key: $IDEM_KEY" \
  -H "Content-Type: application/json" \
  -d '{"currency":"USD","amount":"100"}' \
  > /dev/null
ok

# ---- Step 7: Get provider token ----
step "7 Get provider token"
PROVIDER_TOKEN=$(curl -sf -X POST \
  "$KEYCLOAK_BASE/realms/gaming/protocol/openid-connect/token" \
  -d "grant_type=client_credentials&client_id=provider-demo&client_secret=provider-secret" \
  | jq -r '.access_token')
[ -n "$PROVIDER_TOKEN" ] && [ "$PROVIDER_TOKEN" != "null" ] || fail "No provider token"
ok

# ---- Decode player sub from JWT ----
PLAYER_ID=$(printf '%s' "$PLAYER_TOKEN" | cut -d. -f2 | \
  python3 -c "
import sys, base64, json
p = sys.stdin.read().strip()
p += '=' * (-len(p) % 4)
print(json.loads(base64.b64decode(p))['sub'])
")

# ---- Step 8: BET 30 USD ----
step "8 BET 30 USD"
BET_REF="bet-$(date +%s)-$$"
BET_IDEM=$(cat /proc/sys/kernel/random/uuid 2>/dev/null || python3 -c "import uuid; print(uuid.uuid4())")
curl -sf -X POST "$WALLET_BASE/v1/transactions" \
  -H "Authorization: Bearer $PROVIDER_TOKEN" \
  -H "Idempotency-Key: $BET_IDEM" \
  -H "Content-Type: application/json" \
  -d "{\"reference\":\"$BET_REF\",\"type\":\"BET\",\"playerId\":\"$PLAYER_ID\",\"currency\":\"USD\",\"amount\":\"30\"}" \
  > /dev/null
ok

# ---- Step 9: WIN 30 USD ----
step "9 WIN 30 USD"
WIN_REF="win-$(date +%s)-$$"
WIN_IDEM=$(cat /proc/sys/kernel/random/uuid 2>/dev/null || python3 -c "import uuid; print(uuid.uuid4())")
curl -sf -X POST "$WALLET_BASE/v1/transactions" \
  -H "Authorization: Bearer $PROVIDER_TOKEN" \
  -H "Idempotency-Key: $WIN_IDEM" \
  -H "Content-Type: application/json" \
  -d "{\"reference\":\"$WIN_REF\",\"type\":\"WIN\",\"playerId\":\"$PLAYER_ID\",\"currency\":\"USD\",\"amount\":\"30\",\"betReference\":\"$BET_REF\"}" \
  > /dev/null
ok

# ---- Step 10: Verify transactions and final balance ----
step "10 Get transactions & verify balance"
TX_PAGE=$(curl -sf "$WALLET_BASE/v1/players/me/transactions" \
  -H "Authorization: Bearer $PLAYER_TOKEN")
TX_COUNT=$(echo "$TX_PAGE" | jq '.items | length')
[ "$TX_COUNT" -ge 3 ] || fail "Expected at least 3 transactions, got $TX_COUNT"

WALLETS2=$(curl -sf "$WALLET_BASE/v1/players/me/wallets" \
  -H "Authorization: Bearer $PLAYER_TOKEN")
FINAL_BAL=$(echo "$WALLETS2" | jq -r '.[] | select(.currency=="USD") | .balance')
[ "$FINAL_BAL" = "100.00" ] || [ "$FINAL_BAL" = "100" ] || fail "Expected USD balance 100.00, got $FINAL_BAL"
ok

echo ""
echo "All smoke tests passed!"
exit 0
