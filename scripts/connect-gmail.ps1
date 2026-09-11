# Connects the API's email sign-in to a Gmail account for local development.
#
# Stores the credentials in `dotnet user-secrets` (in your user profile, never in the repo). The
# App Password is read with hidden input, so it never lands in shell history or on screen.
# Restart the API afterwards: it reads these settings only at startup.

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\ParkNest.Api'

$email = (Read-Host 'Gmail address to send sign-in codes from').Trim()
if ($email -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { throw "'$email' does not look like an email address." }

$secure = Read-Host 'Gmail App Password (16 letters; input is hidden)' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    # Google displays it in groups of four; the spaces are not part of it.
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) -replace '\s', ''
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}
if ($password.Length -ne 16) { Write-Warning "App Passwords are 16 letters; this one is $($password.Length). Saving anyway." }

dotnet user-secrets set 'Email:Provider' 'Smtp' --project $project | Out-Null
dotnet user-secrets set 'Email:Username' $email --project $project | Out-Null
dotnet user-secrets set 'Email:Password' $password --project $project | Out-Null
$password = $null

$admin = Read-Host "Make $email an admin the first time it signs in? (y/N)"
if ($admin -match '^(y|yes)$') {
    dotnet user-secrets set 'Auth:AdminEmails:0' $email --project $project | Out-Null
}

Write-Host "Saved. Restart the API and sign in with any email address; the code will come from $email." -ForegroundColor Green
