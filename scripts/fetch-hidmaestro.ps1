#Requires -Version 5.1
param(
    [string]$Version = "v2.1.0",
    [string]$OutDir = "",
    # SHA-256 of HIDMaestro.Core.dll inside the v2.1.0 release zip.
    [string]$ExpectedSha256 = "F13E32591404DB0F418AF9265B8E240F71257297CFAE5A41DCD696FD87E9B4D5"
)

$ErrorActionPreference = "Stop"
if (-not $OutDir) {
    $OutDir = Join-Path (Split-Path $PSScriptRoot -Parent) "lib"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$candidates = @(
    "https://github.com/hifihedgehog/HIDMaestro/releases/download/$Version/HIDMaestro-$Version.zip",
    "https://github.com/hifihedgehog/HIDMaestro/releases/download/$Version/HIDMaestro-$($Version.TrimStart('v')).zip"
)

$tmp = Join-Path $env:TEMP "hidmaestro-$Version.zip"
$downloaded = $false
foreach ($url in $candidates) {
    try {
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
        $downloaded = $true
        break
    } catch {
        Write-Warning "Failed: $url"
    }
}

if (-not $downloaded) {
    throw "Could not download HIDMaestro $Version release zip."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($tmp)
try {
    $entry = $zip.Entries | Where-Object { $_.FullName -eq "HIDMaestro.Core.dll" } | Select-Object -First 1
    if (-not $entry) {
        throw "HIDMaestro.Core.dll not found in release zip."
    }
    $dest = Join-Path $OutDir "HIDMaestro.Core.dll"
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)

    $hash = (Get-FileHash -Path $dest -Algorithm SHA256).Hash.ToUpperInvariant()
    $expected = $ExpectedSha256.ToUpperInvariant()
    if ($Version -eq "v2.1.0" -and $hash -ne $expected) {
        Remove-Item -Force $dest -ErrorAction SilentlyContinue
        throw "HIDMaestro.Core.dll SHA-256 mismatch.`nExpected: $expected`nActual:   $hash"
    }

    Write-Host "Wrote $dest"
    Write-Host "SHA-256 $hash"
} finally {
    $zip.Dispose()
}
