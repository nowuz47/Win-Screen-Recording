param(
    [string]$Version = '0.1.0-preview.2',
    [string]$PublishDirectory = "$PSScriptRoot\..\out\app",
    [string]$OutputDirectory = "$PSScriptRoot\..\out"
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$') { throw 'Invalid package version.' }
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
foreach ($name in @('Glide.App.exe', 'Glide.Capture.dll', 'Glide.App.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $name))) { throw "Missing published file: $name" }
}
if (-not (Get-ChildItem -LiteralPath $publish -Filter '*.pri')) { throw 'Missing WinUI resources.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = (Get-Command ISCC.exe -ErrorAction Stop).Source
}
& $compiler "/DAppVersion=$Version" "/DPublishDir=$publish" "/DPackageDir=$output" "$PSScriptRoot\..\installer\Glide.iss"
if ($LASTEXITCODE -ne 0) { throw "Installer compiler failed: $LASTEXITCODE" }
$setup = Join-Path $output "Glide-Setup-$Version-x64.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw 'Installer output missing.' }
Write-Output $setup
exit 0
