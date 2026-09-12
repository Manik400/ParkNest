# Points the API's wallet top-ups at a real payment gateway, for local development.
#
# Stores everything in `dotnet user-secrets` (in your user profile, never in the repo). Secrets are
# read with hidden input, so they never land in shell history or on screen. Restart the API after.
#
#   powershell -ExecutionPolicy Bypass -File scripts/connect-payments.ps1          # connect a provider
#   powershell -ExecutionPolicy Bypass -File scripts/connect-payments.ps1 -Reset   # back to the Sandbox
#
# Which provider, and what each costs: docs/payment-gateway-setup-fully-free-rnd.md
#   Cashfree  - recommended. No setup or annual fee, test keys before KYC, 0% offer for new merchants.
#   Razorpay  - fallback. Rs 199 + GST KYC fee, then 0% for 90 days.

param([switch]$Reset)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\ParkNest.Api'

$supported = @('Cashfree', 'Razorpay')

$keys = @(
    'Payments:Provider', 'Payments:Mode', 'Payments:PublicBaseUrl',
    'Payments:Cashfree:ClientId', 'Payments:Cashfree:ClientSecret', 'Payments:Cashfree:PaymentMethods',
    'Payments:Razorpay:KeyId', 'Payments:Razorpay:KeySecret', 'Payments:Razorpay:WebhookSecret'
)

function Set-Secret([string]$key, [string]$value) {
    dotnet user-secrets set $key $value --project $project | Out-Null
}

function Read-Hidden([string]$prompt) {
    $secure = Read-Host $prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function New-WebhookSecret {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return ([Convert]::ToBase64String($bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

if ($Reset) {
    foreach ($key in $keys) { dotnet user-secrets remove $key --project $project | Out-Null }
    Write-Host 'Payment secrets removed. The API is back on the built-in Sandbox gateway after a restart.' -ForegroundColor Green
    return
}

$provider = (Read-Host "Provider ($($supported -join ', ')) [Cashfree]").Trim()
if (-not $provider) { $provider = 'Cashfree' }
$provider = $supported | Where-Object { $_ -ieq $provider } | Select-Object -First 1
if (-not $provider) { throw "Supported providers: $($supported -join ', ')." }

$mode = (Read-Host 'Mode - Test or Live [Test]').Trim()
if (-not $mode) { $mode = 'Test' }
if ($mode -notin @('Test', 'Live')) { throw 'Mode must be Test or Live.' }

Write-Host ''
Write-Host 'Public base URL of the API: where the provider sends the browser back, and calls the webhook.'
Write-Host '  https://localhost:7139 works for the redirect on this machine. For webhooks too, run'
Write-Host '  "cloudflared tunnel --url https://localhost:7139" and paste the https://....trycloudflare.com URL.'
$base = (Read-Host 'Public base URL [https://localhost:7139]').Trim().TrimEnd('/')
if (-not $base) { $base = 'https://localhost:7139' }
if ($base -notmatch '^https?://') { throw "'$base' must start with http:// or https://." }

switch ($provider) {
    'Cashfree' {
        Write-Host ''
        Write-Host 'Cashfree dashboard (https://merchant.cashfree.com) > Developers > API keys.' -ForegroundColor Cyan
        Write-Host '  Test keys exist as soon as the account does; Live keys appear once KYC is approved.'
        $clientId = (Read-Host 'Cashfree App ID (x-client-id)').Trim()
        if (-not $clientId) { throw 'The App ID is required.' }
        if ($mode -eq 'Live' -and $clientId -like 'TEST*') { throw 'That is a sandbox App ID; run again with Mode=Test if you mean it.' }

        $clientSecret = Read-Hidden 'Cashfree secret key (hidden)'
        if (-not $clientSecret) { throw 'The secret key is required.' }
        if ($mode -eq 'Test' -and $clientSecret -like 'cfsk_ma_prod_*') { throw 'That is a production secret; run again with Mode=Live if you mean it.' }
        if ($mode -eq 'Live' -and $clientSecret -like 'cfsk_ma_test_*') { throw 'That is a sandbox secret; Mode=Live needs a cfsk_ma_prod_ key.' }

        $methods = (Read-Host 'Payment methods to offer, e.g. upi (empty = everything enabled on the account)').Trim()

        Set-Secret 'Payments:Cashfree:ClientId' $clientId
        Set-Secret 'Payments:Cashfree:ClientSecret' $clientSecret
        $clientSecret = $null
        if ($methods) { Set-Secret 'Payments:Cashfree:PaymentMethods' $methods }
        else { dotnet user-secrets remove 'Payments:Cashfree:PaymentMethods' --project $project | Out-Null }

        Write-Host ''
        Write-Host 'In the Cashfree dashboard (Developers > Webhooks > Payment Gateway), add a webhook:' -ForegroundColor Cyan
        Write-Host "  URL:     $base/api/payments/webhook   (must be https; a tunnel URL for local development)"
        Write-Host '  Events:  Payment success, Payment failed'
        Write-Host '  Version: 2026-01-01'
        Write-Host '  No secret to enter: Cashfree signs webhooks with the secret key above.'
        Write-Host 'Without a reachable webhook the API still credits: it asks Cashfree itself when the browser returns.'
    }
    'Razorpay' {
        $keyId = (Read-Host 'Razorpay Key Id (rzp_test_... or rzp_live_...)').Trim()
        if ($mode -eq 'Test' -and $keyId -like 'rzp_live_*') { throw 'That is a live key; run again with Mode=Live if you mean it.' }
        if ($mode -eq 'Live' -and $keyId -like 'rzp_test_*') { throw 'That is a test key; Mode=Live needs a rzp_live_ key.' }

        $keySecret = Read-Hidden 'Razorpay Key Secret (hidden)'
        $webhookSecret = Read-Hidden 'Webhook secret you set on the webhook in the Razorpay dashboard (hidden; leave empty to generate one)'
        $generated = -not $webhookSecret
        if ($generated) { $webhookSecret = New-WebhookSecret }

        Set-Secret 'Payments:Razorpay:KeyId' $keyId
        Set-Secret 'Payments:Razorpay:KeySecret' $keySecret
        Set-Secret 'Payments:Razorpay:WebhookSecret' $webhookSecret
        $keySecret = $null

        Write-Host ''
        Write-Host 'In the Razorpay dashboard (Settings > Webhooks), add a webhook:' -ForegroundColor Cyan
        Write-Host "  URL:    $base/api/payments/webhook"
        Write-Host '  Events: payment.captured, payment.failed'
        if ($generated) { Write-Host "  Secret: $webhookSecret" }
        Write-Host '  Also turn on automatic capture (Settings > Payment Capture).'
        $webhookSecret = $null
    }
}

Set-Secret 'Payments:Provider' $provider
Set-Secret 'Payments:Mode' $mode
Set-Secret 'Payments:PublicBaseUrl' $base

Write-Host ''
Write-Host "Saved: $provider ($mode). Restart the API to use it." -ForegroundColor Green
Write-Host 'Back to the Sandbox any time: powershell -ExecutionPolicy Bypass -File scripts/connect-payments.ps1 -Reset'
