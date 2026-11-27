# Builds the two downloads for a release:
#   dist/release/Nyx-windows-x64.exe   the laptop app (one self-contained exe, no .NET install needed)
#   dist/release/Nyx-phone.apk         the phone app, signed with your release key
#
# Run from PowerShell in this folder:   powershell -ExecutionPolicy Bypass -File .\build-release.ps1
# The phone app needs android/keystore.properties (see android/keystore.properties.example).
# Without it you get an unsigned APK, which phones refuse to install.

param([switch]$SkipTests)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root "dist\release"
$stage = Join-Path $root "dist\stage\Nyx"
Remove-Item $out, (Join-Path $root "dist\stage") -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out, $stage | Out-Null

if (-not $SkipTests) {
    Write-Host "== Laptop tests"
    dotnet test (Join-Path $root "tests\Nyx.Tests") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Laptop tests failed." }
}

Write-Host "== Laptop app"
dotnet publish (Join-Path $root "desktop\PhoneBridge.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $stage
if ($LASTEXITCODE -ne 0) { throw "Laptop build failed." }
# one file to download and double-click; settings live in %LOCALAPPDATA%\Nyx, so it runs from anywhere
Copy-Item (Join-Path $stage "Nyx.exe") (Join-Path $out "Nyx-windows-x64.exe") -Force

Write-Host "== Phone app"
Push-Location (Join-Path $root "android")
try {
    # find the Android SDK the usual way if the project doesn't say where it is
    if (-not (Test-Path "local.properties") -and -not $env:ANDROID_HOME) {
        $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk"
        if (Test-Path $sdk) { $env:ANDROID_HOME = $sdk } else { throw "Android SDK not found. Set ANDROID_HOME." }
    }
    $tasks = "lintRelease assembleRelease"
    if (-not $SkipTests) { $tasks = "testDebugUnitTest " + $tasks }
    # through cmd: Gradle writes progress to stderr, which PowerShell would treat as an error
    & cmd /c ".\gradlew.bat $tasks --console=plain 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "Phone build failed." }
} finally { Pop-Location }
$apkDir = Join-Path $root "android\app\build\outputs\apk\release"
$signed = Join-Path $apkDir "app-release.apk"
if (Test-Path $signed) {
    Copy-Item $signed (Join-Path $out "Nyx-phone.apk")
} else {
    Copy-Item (Join-Path $apkDir "app-release-unsigned.apk") (Join-Path $out "Nyx-phone-UNSIGNED.apk")
    Write-Warning "No android/keystore.properties, so the APK is unsigned and phones won't install it. See README > Releasing."
}

Write-Host ""
Write-Host "Done. Upload these to a GitHub release:"
Get-ChildItem $out | ForEach-Object { "  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB) }
