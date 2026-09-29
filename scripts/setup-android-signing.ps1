# One-time: creates the Android release signing key and stores it as GitHub secrets, so every APK
# the "Android APK" workflow publishes installs as an update over the previous one.
#
# Run from the repository root (needs the GitHub CLI signed in: gh auth status):
#   powershell -ExecutionPolicy Bypass -File scripts\setup-android-signing.ps1
#
# It also writes clients/mobile/android/key.properties + app/release.jks for local release builds.
# Both are git-ignored. KEEP A BACKUP of release.jks and the password printed at the end: lose them
# and installed apps can never be updated, only uninstalled and reinstalled.

$ErrorActionPreference = 'Stop'

$android = Join-Path $PSScriptRoot '..\clients\mobile\android' | Resolve-Path
$jks = Join-Path $android 'app\release.jks'
$alias = 'parknest'

if (Test-Path $jks) {
    throw "A key already exists at $jks. Delete it first only if you are sure - updates depend on it."
}

$keytool = Get-Command keytool -ErrorAction SilentlyContinue
if (-not $keytool) {
    $candidates = @(
        "$env:ProgramFiles\Android\Android Studio\jbr\bin\keytool.exe",
        "$env:ProgramFiles\Android\Android Studio\jre\bin\keytool.exe"
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) { throw 'keytool not found. Install Android Studio or a JDK.' }
    $keytool = $found
} else {
    $keytool = $keytool.Source
}

# Letters and digits only, so it survives key.properties and shell quoting untouched.
$chars = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
$password = -join (1..32 | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })

& $keytool -genkeypair -v -keystore $jks -alias $alias -keyalg RSA -keysize 2048 -validity 10000 `
    -storepass $password -keypass $password -dname 'CN=ParkNest, O=ParkNest, C=IN' | Out-Null

@"
storeFile=release.jks
storePassword=$password
keyAlias=$alias
keyPassword=$password
"@ | Set-Content -Path (Join-Path $android 'key.properties') -Encoding ascii

[Convert]::ToBase64String([IO.File]::ReadAllBytes($jks)) | gh secret set ANDROID_KEYSTORE_BASE64
$password | gh secret set ANDROID_KEYSTORE_PASSWORD
$alias | gh secret set ANDROID_KEY_ALIAS

Write-Output "Signing key created: $jks"
Write-Output 'GitHub secrets set: ANDROID_KEYSTORE_BASE64, ANDROID_KEYSTORE_PASSWORD, ANDROID_KEY_ALIAS'
Write-Output "Password (store it in your password manager with a copy of release.jks): $password"
