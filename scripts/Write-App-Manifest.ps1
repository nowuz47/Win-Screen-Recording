param([string]$AppDirectory)
$ErrorActionPreference = 'Stop'
if (-not $AppDirectory) {
    $AppDirectory = (Get-Content -LiteralPath "$env:LOCALAPPDATA\GlideDev\app-current.txt" -Raw).Trim()
}
$name = Split-Path -Leaf $AppDirectory
if ($name -notmatch '^app-x64-build-\d+-\d+$') { throw 'Expected a versioned development app build.' }
$files = 'Glide.App.exe','Glide.App.dll','Glide.Core.dll','Glide.Media.dll','Glide.Capture.dll'
$hashes = $files | ForEach-Object { Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $AppDirectory $_) }
$evidence = Join-Path $PSScriptRoot '..\.artifacts\windows-app-manifests'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$destination = Join-Path $evidence "$name.json"
$payload = $hashes | Select-Object Path,Hash | ConvertTo-Json
if (Test-Path -LiteralPath $destination) {
    if ((Get-Content -LiteralPath $destination -Raw).Trim() -ne $payload.Trim()) { throw 'An existing app build manifest differs.' }
    return
}
$payload | Set-Content -LiteralPath $destination -Encoding utf8
