# Publish framework-dependent Release and build the Inno Setup installer.
# The installer downloads .NET 10 Desktop + ASP.NET runtimes when missing.
param(
    [string]$Version = "",
    [switch]$SkipBuild,
    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\S16nHidSpoof\S16nHidSpoof.csproj"
$publish = Join-Path $root "dist\publish"
$stage = Join-Path $root "dist\stage"
$dist = Join-Path $root "dist"
$iss = Join-Path $root "installer\s16n-hid-spoof.iss"
$libDll = Join-Path $root "lib\HIDMaestro.Core.dll"

if (-not $Version) {
    $sha = (git -C $root rev-parse --short HEAD).Trim()
    $Version = (Get-Date).ToUniversalTime().ToString("yyyyMMdd") + "-" + $sha
}

function Require-File([string]$Path) {
    if (-not (Test-Path $Path)) { throw "Missing required file: $Path" }
}

if (-not (Test-Path $libDll)) {
    Write-Host "Fetching HIDMaestro.Core.dll…"
    & (Join-Path $PSScriptRoot "fetch-hidmaestro.ps1")
}
Require-File $libDll

if (-not $SkipBuild) {
    Write-Host "Publishing $Version…"
    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
    # Fail clearly if a running instance locks bin\Release.
    $locked = Get-Process -Name "s16n-hid-spoof" -ErrorAction SilentlyContinue
    if ($locked) {
        throw "Stop running s16n-hid-spoof.exe (PID $($locked.Id -join ',')) before packaging — it locks the build output."
    }
    # Keep Assembly Version numeric (csproj 1.0.0). Stamp UI/build id via InformationalVersion only
    # (YYYYMMDD-git) — passing that string as Version breaks CS7034 on publish.
    dotnet publish $proj -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=false `
        -p:InformationalVersion=$Version `
        -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

Require-File (Join-Path $publish "s16n-hid-spoof.exe")
Require-File (Join-Path $publish "appsettings.json")
$assetsSrc = Join-Path $root "src\S16nHidSpoof\Assets"
Require-File (Join-Path $assetsSrc "app.ico")

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Copy-Item -Recurse -Force (Join-Path $publish "*") $stage
# Drop SDK/web publish fluff from the installer payload.
Remove-Item -Force (Join-Path $stage "*.pdb") -ErrorAction SilentlyContinue
Remove-Item -Force (Join-Path $stage "web.config") -ErrorAction SilentlyContinue
Remove-Item -Force (Join-Path $stage "*.staticwebassets.endpoints.json") -ErrorAction SilentlyContinue
# Single-file publish may omit or nest assets; always stage loose Assets from source.
$assetsDest = Join-Path $stage "Assets"
if (Test-Path $assetsDest) { Remove-Item -Recurse -Force $assetsDest }
New-Item -ItemType Directory -Force -Path $assetsDest | Out-Null
Copy-Item -Force (Join-Path $assetsSrc "*") $assetsDest

# Helper scripts for the optional elevated logon task (reliable quoting).
@"
@echo off
setlocal
set EXE=%~dp0s16n-hid-spoof.exe
schtasks /Create /F /TN "s16n-hid-spoof" /TR "\"%EXE%\" --tray" /SC ONLOGON /RL HIGHEST
exit /b %ERRORLEVEL%
"@ | Set-Content -Encoding ASCII (Join-Path $stage "register-autostart.cmd")

@"
@echo off
schtasks /Delete /TN "s16n-hid-spoof" /F
exit /b 0
"@ | Set-Content -Encoding ASCII (Join-Path $stage "unregister-autostart.cmd")

Write-Host "Staged -> $stage"

if (-not $IsccPath) {
    $candidates = @(
        "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $IsccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
    throw "ISCC.exe not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup)."
}

$stageAbs = (Resolve-Path $stage).Path
$distAbs = (Resolve-Path $dist).Path
Write-Host "Compiling installer with $IsccPath…"
& $IsccPath `
    "/DStageDir=$stageAbs" `
    "/DOutDir=$distAbs" `
    "/DMyAppVersion=$Version" `
    $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Get-ChildItem $dist -Filter "s16n-hid-spoof-setup-*.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $setup) { throw "Installer exe not found in $dist" }

Write-Host "Installer -> $($setup.FullName)"
Get-Item $setup.FullName | Select-Object FullName, Length, LastWriteTime
