#Requires -Version 5.1
<#
.SYNOPSIS
    End-to-end smoke test for the Mini Wallet Platform.
    Runs all 10 steps against a running docker compose stack.
#>

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$UsersBase    = "http://localhost:5001"
$WalletBase   = "http://localhost:5002"
$KeycloakBase = "http://localhost:8080"
$MailpitBase  = "http://localhost:8025"

function Step([string]$label, [scriptblock]$action) {
    Write-Host "[$label] " -NoNewline
    & $action
    Write-Host "OK"
}

function Invoke-Api {
    param(
        [string]$Method = "GET",
        [string]$Uri,
        [hashtable]$Headers = @{},
        [object]$Body = $null,
        [string]$ContentType = "application/json"
    )
    $params = @{
        Method      = $Method
        Uri         = $Uri
        Headers     = $Headers
        ErrorAction = "Stop"
    }
    if ($null -ne $Body) {
        $params["Body"]        = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 10 }
        $params["ContentType"] = $ContentType
    }
    return Invoke-RestMethod @params
}

# ---- Step 1: Register a new user ----
$email    = "smoketest_$(Get-Random)@example.com"
$password = "Smoke1Test!"
$username = "Smoke User"

Step "1 Register user" {
    $body = @{ email = $email; password = $password; displayName = $username }
    Invoke-Api -Method POST -Uri "$UsersBase/v1/users" -Body $body | Out-Null
}

# ---- Step 2: Read verification email from Mailpit ----
Step "2 Find verification email" {
    $deadline = (Get-Date).AddSeconds(30)
    $script:verifyMsg = $null
    do {
        Start-Sleep -Seconds 2
        $msgs = Invoke-Api -Uri "$MailpitBase/api/v1/messages"
        $script:verifyMsg = $msgs.messages |
            Where-Object { $_.To | Where-Object { $_.Address -eq $email } } |
            Select-Object -First 1
    } while ($null -eq $script:verifyMsg -and (Get-Date) -lt $deadline)
    if ($null -eq $script:verifyMsg) { throw "No verification email found for $email within 30 s" }
}

# ---- Step 3: Extract token and verify ----
Step "3 Verify email" {
    $msgId  = $script:verifyMsg.ID
    $detail = Invoke-Api -Uri "$MailpitBase/api/v1/message/$msgId"
    $html   = $detail.HTML
    if (-not $html) { $html = $detail.Text }
    if ($html -match '[?&]token=([A-Za-z0-9_\-]+)') {
        $script:verifyToken = $Matches[1]
    } else {
        throw "Could not extract verify token from email body"
    }
    Invoke-Api -Uri "$UsersBase/v1/users/verify?token=$($script:verifyToken)" | Out-Null
}

# ---- Step 4: Get player token (password grant) ----
Step "4 Get player token" {
    $enc   = [Uri]::EscapeDataString
    $body  = "grant_type=password&client_id=wallet-web&username=$($enc.Invoke($email))&password=$($enc.Invoke($password))"
    $result = Invoke-Api -Method POST `
        -Uri "$KeycloakBase/realms/gaming/protocol/openid-connect/token" `
        -Body $body -ContentType "application/x-www-form-urlencoded"
    $script:playerToken = $result.access_token
    if (-not $script:playerToken) { throw "No access_token in response" }
}

# ---- Step 5: GET wallets (expect 3 zero-balance wallets) ----
Step "5 Get wallets" {
    $headers = @{ Authorization = "Bearer $($script:playerToken)" }
    $wallets = Invoke-Api -Uri "$WalletBase/v1/players/me/wallets" -Headers $headers
    # Response is a bare array of { currency, balance }
    if ($wallets.Count -ne 3) { throw "Expected 3 wallets, got $($wallets.Count)" }
    $usd = $wallets | Where-Object { $_.currency -eq "USD" }
    if (-not $usd) { throw "No USD wallet found" }
    if ([decimal]$usd.balance -ne 0) { throw "Expected USD balance 0, got $($usd.balance)" }
}

# ---- Step 6: Top-up 100 USD ----
# Amount must be a string per the API contract
Step "6 Top-up 100 USD" {
    $headers = @{
        Authorization     = "Bearer $($script:playerToken)"
        "Idempotency-Key" = [System.Guid]::NewGuid().ToString()
    }
    $body = @{ currency = "USD"; amount = "100" }
    Invoke-Api -Method POST -Uri "$WalletBase/v1/wallet/topup" -Headers $headers -Body $body | Out-Null
}

# ---- Step 7: Get provider token (client_credentials) ----
Step "7 Get provider token" {
    $body = "grant_type=client_credentials&client_id=provider-demo&client_secret=provider-secret"
    $result = Invoke-Api -Method POST `
        -Uri "$KeycloakBase/realms/gaming/protocol/openid-connect/token" `
        -Body $body -ContentType "application/x-www-form-urlencoded"
    $script:providerToken = $result.access_token
    if (-not $script:providerToken) { throw "No access_token in provider response" }
}

# ---- Decode player sub from JWT ----
function Get-JwtSub([string]$token) {
    $payload = $token.Split('.')[1]
    # Pad to multiple of 4
    $mod = $payload.Length % 4
    if ($mod -ne 0) { $payload += '=' * (4 - $mod) }
    $decoded = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload))
    return ($decoded | ConvertFrom-Json).sub
}

$script:playerId = Get-JwtSub $script:playerToken

# ---- Step 8: BET 30 USD ----
# Reference is a unique provider-scoped string; Amount is a string
$script:betRef = "bet-$(Get-Date -Format 'yyyyMMddHHmmss')-$(Get-Random)"
Step "8 BET 30 USD" {
    $headers = @{
        Authorization     = "Bearer $($script:providerToken)"
        "Idempotency-Key" = [System.Guid]::NewGuid().ToString()
    }
    $body = @{
        reference = $script:betRef
        type      = "BET"
        playerId  = $script:playerId
        currency  = "USD"
        amount    = "30"
    }
    $result = Invoke-Api -Method POST -Uri "$WalletBase/v1/transactions" -Headers $headers -Body $body
    if ($null -eq $result) { throw "No response from BET" }
}

# ---- Step 9: WIN 30 USD ----
Step "9 WIN 30 USD" {
    $headers = @{
        Authorization     = "Bearer $($script:providerToken)"
        "Idempotency-Key" = [System.Guid]::NewGuid().ToString()
    }
    $body = @{
        reference    = "win-$(Get-Date -Format 'yyyyMMddHHmmss')-$(Get-Random)"
        type         = "WIN"
        playerId     = $script:playerId
        currency     = "USD"
        amount       = "30"
        betReference = $script:betRef
    }
    $result = Invoke-Api -Method POST -Uri "$WalletBase/v1/transactions" -Headers $headers -Body $body
    if ($null -eq $result) { throw "No response from WIN" }
}

# ---- Step 10: GET transactions; verify count >= 3 and balance = 100.00 ----
Step "10 Get transactions & verify balance" {
    $headers = @{ Authorization = "Bearer $($script:playerToken)" }

    # Transactions page: { items: [...], nextCursor: ... }
    $txPage = Invoke-Api -Uri "$WalletBase/v1/players/me/transactions" -Headers $headers
    $count  = $txPage.items.Count
    if ($count -lt 3) { throw "Expected at least 3 transactions, got $count" }

    # Wallets array
    $wallets = Invoke-Api -Uri "$WalletBase/v1/players/me/wallets" -Headers $headers
    $usd = $wallets | Where-Object { $_.currency -eq "USD" }
    if (-not $usd) { throw "No USD wallet found after transactions" }
    $bal = [decimal]$usd.balance
    if ($bal -ne [decimal]100.00) { throw "Expected USD balance 100.00, got $bal" }
}

Write-Host ""
Write-Host "All smoke tests passed!" -ForegroundColor Green
exit 0
