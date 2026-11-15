# Build the installer and publish it to this repo's GitHub Releases.
# Version defaults to YYYYMMDD-<7-char git hash> (UTC date).
param(
    [string]$Version = "",
    [switch]$SkipBuild,
    [switch]$AllowDirty,
    [string]$Repo = "stuart11n/s16n-hid-spoof",
    [string]$Tag = "latest",
    [string]$Title = "s16n-hid-spoof (latest)"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
$assetName = "s16n-hid-spoof-setup.exe"

function Invoke-Checked([string]$What, [scriptblock]$Block) {
    & $Block
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE" }
}

# Windows PowerShell turns native stderr into a terminating error under "Stop".
function Test-NativeSucceeds([scriptblock]$Block) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $Block *> $null; return ($LASTEXITCODE -eq 0) }
    finally { $ErrorActionPreference = $prev }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) not found. Install from https://cli.github.com/"
}
if (-not (Test-NativeSucceeds { gh auth status })) {
    throw "gh is not signed in. Run: gh auth login"
}

Push-Location $root
try {
    $dirty = git status --porcelain --untracked-files=no
    if ($dirty -and -not $AllowDirty) {
        throw "Working tree has uncommitted changes. Commit them or pass -AllowDirty."
    }
    $sha = (git rev-parse HEAD).Trim()
    $shortSha = (git rev-parse --short=7 HEAD).Trim()
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = "{0}-{1}" -f [DateTime]::UtcNow.ToString("yyyyMMdd"), $shortSha
        Write-Host "Version defaulted to $Version"
    }

    $onRemote = git branch -r --contains $sha 2>$null
    if (-not $onRemote) {
        Write-Warning "HEAD $shortSha is not on any remote branch; push it to $Repo so the release can be traced."
    }

    $packageArgs = @{ Version = $Version }
    if ($SkipBuild) { $packageArgs.SkipBuild = $true }
    & (Join-Path $PSScriptRoot "package.ps1") @packageArgs

    $versioned = Join-Path $dist "s16n-hid-spoof-setup-$Version.exe"
    if (-not (Test-Path $versioned)) { throw "Missing installer: $versioned" }
    $asset = Join-Path $dist $assetName
    Copy-Item $versioned $asset -Force

    $dirtyNote = if ($dirty) { " (with uncommitted changes)" } else { "" }
    $notes = @"
Windows x64 self-contained installer for s16n-hid-spoof $Version.

Includes tray UI, per-slot Enabled, elevated logon autostart task option, and loopback HTTP control plane.

Commit: $sha$dirtyNote
"@
    $notesFile = Join-Path $dist "release-notes.md"
    Set-Content -Path $notesFile -Value $notes -Encoding utf8

    if (Test-NativeSucceeds { gh release view $Tag -R $Repo }) {
        Write-Host "Updating release $Tag on $Repo ..."
        Invoke-Checked "gh release edit" {
            gh release edit $Tag -R $Repo --title $Title --notes-file $notesFile
        }
        Invoke-Checked "gh release upload" {
            gh release upload $Tag $asset -R $Repo --clobber
        }
    } else {
        Write-Host "Creating release $Tag on $Repo ..."
        Invoke-Checked "gh release create" {
            gh release create $Tag $asset -R $Repo --target main --title $Title --notes-file $notesFile --latest
        }
    }

    Write-Host "Published $assetName ($Version, $shortSha)"
    Write-Host "Download: https://github.com/$Repo/releases/download/$Tag/$assetName"
} finally {
    Pop-Location
}
