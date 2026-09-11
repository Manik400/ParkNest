# Points the API's wallet top-ups at a real payment gateway, for local development.
#
# Stores everything in `dotnet user-secrets` (in your user profile, never in the repo). Secrets are
# read with hidden input, so they never land in shell history or on screen. Restart the API after.
#
#   powershell -ExecutionPolicy Bypass -File scripts/connect-payments.ps1          # connect a provider
#   powershell -ExecutionPolicy Bypass -File scripts/connect-payments.ps1 -Reset   # back to the Sandbox
#
# Which provider, and what each costs: docs/payment-gateway-setup-fully-free-rnd.md

param([switch]$Reset)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\ParkNest.Api'

$supported = @('Razorpay')

$keys = @(
    'Payments:Provider', 'Payments:Mode', 'Payments:PublicBaseUrl',
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

$provider = (Read-Host "Provider ($($supported -join ', '))").Trim()
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
