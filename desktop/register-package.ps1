# Builds Nyx and registers it with a package identity, which Windows
# requires before an app may route call audio over Bluetooth hands-free.
#
# Before running: Settings > System > For developers > Developer Mode = On.
# Run from PowerShell:   powershell -ExecutionPolicy Bypass -File .\register-package.ps1
# Then start Nyx from the Start menu (not with dotnet run).

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $root "..\dist\Nyx"

# a running Nyx locks its own files
Get-Process Nyx -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

dotnet publish (Join-Path $root "PhoneBridge.csproj") -c Release -r win-x64 --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# Windows only updates an installed package in place when the version goes up, so each
# build gets a fresh one: 0.3.<yyMM>.<minute of the month>
$now = Get-Date
$build = ($now.Year - 2000) * 100 + $now.Month
$rev = ($now.Day - 1) * 1440 + $now.Hour * 60 + $now.Minute
$manifest = Get-Content (Join-Path $root "package\AppxManifest.xml") -Raw
$manifest = $manifest -replace 'Version="(\d+)\.(\d+)\.\d+\.\d+"', ('Version="$1.$2.' + $build + '.' + $rev + '"')
Set-Content (Join-Path $out "AppxManifest.xml") $manifest -Encoding UTF8
New-Item -ItemType Directory -Force (Join-Path $out "Assets") | Out-Null
Copy-Item (Join-Path $root "package\Assets\*") (Join-Path $out "Assets") -Force

# update in place: uninstalling first would make Windows delete the app's data
Add-AppxPackage -Register (Join-Path $out "AppxManifest.xml") -ForceApplicationShutdown -ForceUpdateFromAnyVersion

Write-Host ""
Write-Host "Registered. Open Nyx from the Start menu, connect your phone,"
Write-Host "then press 'Use this laptop's mic and speakers' on the Calls page."
