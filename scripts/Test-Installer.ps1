param(
    [string]$Installer = "$PSScriptRoot\..\out\Glide-Setup-0.1.0-preview.3-x64.exe",
    [string]$PublishDirectory = "$PSScriptRoot\..\out\app"
)
# Run on a disposable Windows CI runner. Does not launch the app or simulate UI.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run only on the disposable GitHub Actions Windows runner.' }
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9BC1F2E4-B013-4B85-93BF-1DECD7F54952}_is1'
if (Test-Path $registration) { throw 'An existing Glide installation must not be modified by this test.' }
$setup = (Resolve-Path -LiteralPath $Installer).Path
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$work = Join-Path $env:TEMP ('Glide-installer-test-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $work 'Installed App'
$evidence = "$PSScriptRoot\..\.artifacts\installer"
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$profile = Join-Path $env:LOCALAPPDATA 'Glide'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
$sentinel = Join-Path $profile ('installer-test-' + [Guid]::NewGuid().ToString('N') + '.txt')
Set-Content -LiteralPath $sentinel -Value 'Preserve existing user data'
$results = @()
$uninstaller = Join-Path $destination 'unins000.exe'
function Run-Installer([string]$Executable, [string]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit(120000)) { throw 'Installer exceeded the two minute test deadline.' }
    if ($process.ExitCode -ne 0) { throw "Installer exit code: $($process.ExitCode)" }
}
try {
    foreach ($phase in @('install', 'reinstall')) {
        $log = Join-Path $evidence "$phase.log"
        Run-Installer $setup "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$destination`" /LOG=`"$log`""
        foreach ($file in Get-ChildItem -LiteralPath $publish -Recurse -File) {
            $relative = $file.FullName.Substring($publish.Length).TrimStart('\')
            $installed = Join-Path $destination $relative
            if (-not (Test-Path -LiteralPath $installed)) { throw "Missing installed payload: $relative" }
            if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) { throw "Installed payload mismatch: $relative" }
        }
        $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Glide\Glide.lnk'
        if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Start Menu shortcut missing.' }
        $registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9BC1F2E4-B013-4B85-93BF-1DECD7F54952}_is1'
        if (-not (Test-Path $registration)) { throw 'Per-user uninstall registration missing.' }
        $results += @{ name=$phase; passed=$true; payloadHashMatch=$true }
    }
    Run-Installer $uninstaller "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$evidence\uninstall.log`""
    if (Test-Path -LiteralPath (Join-Path $destination 'Glide.App.exe')) { throw 'App remains after uninstall.' }
    if (Test-Path $registration) { throw 'Uninstall registration remains.' }
    if (Test-Path -LiteralPath $shortcut) { throw 'Start Menu shortcut remains.' }
    if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall deleted user data.' }
    $results += @{ name='uninstall preserves user data'; passed=$true }
} catch {
    $results += @{ name='installer smoke test'; passed=$false; error=$_.Exception.Message }
    throw
} finally {
    @{ timestamp=[DateTimeOffset]::UtcNow.ToString('O'); scope='Silent install, same-version reinstall and uninstall; not interactive wizard or app UX validation'; results=$results } |
        ConvertTo-Json -Depth 5 | Set-Content "$evidence\result.json" -Encoding utf8
    if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel }
}
Write-Output 'PASS install, reinstall, payload hashes, Start Menu, HKCU registration, uninstall and user-data preservation'
exit 0
