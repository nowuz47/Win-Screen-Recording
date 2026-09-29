param([ValidateSet('home','recording')][string]$Phase='home')
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GlideActivationWindow {
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
}
'@
$app=(Get-Content -LiteralPath "$env:LOCALAPPDATA\GlideDev\app-current.txt" -Raw).Trim()
$path=Join-Path $app 'Glide.App.exe'
$owners=@(Get-Process Glide.App | Where-Object {$_.Path -eq $path})
if($owners.Count -ne 1){throw "Expected one review candidate before duplicate launch; got $($owners.Count)."}
$primary=$owners[0];$primary.Refresh();$window=$primary.MainWindowHandle
if($window -eq [IntPtr]::Zero){throw 'Review candidate has no main window.'}
$beforeMinimized=[GlideActivationWindow]::IsIconic($window)
$beforeProjects=@(Get-ChildItem -LiteralPath "$env:LOCALAPPDATA\Glide\projects" -Directory | Select-Object -ExpandProperty Name | Sort-Object)
$duplicate=Start-Process -FilePath $path -PassThru
$exited=$duplicate.WaitForExit(8000)
if($exited){$duplicate.WaitForExit()}
$primary.Refresh()
$after=@(Get-Process Glide.App | Where-Object {$_.Path -eq $path})
[uint32]$foregroundProcess=0
$watch=[Diagnostics.Stopwatch]::StartNew()
do{
 [void][GlideActivationWindow]::GetWindowThreadProcessId([GlideActivationWindow]::GetForegroundWindow(),[ref]$foregroundProcess)
 if($foregroundProcess -eq $primary.Id){break}
 Start-Sleep -Milliseconds 100
}while($watch.ElapsedMilliseconds -lt 2000)
$afterProjects=@(Get-ChildItem -LiteralPath "$env:LOCALAPPDATA\Glide\projects" -Directory | Select-Object -ExpandProperty Name | Sort-Object)
$checks=@{duplicateExited=$exited;duplicateExitSuccess=($exited -and $duplicate.ExitCode -eq 0);samePrimary=($after.Count -eq 1 -and $after[0].Id -eq $primary.Id);restored=(-not [GlideActivationWindow]::IsIconic($window));primaryForeground=($foregroundProcess -eq $primary.Id);projectDirectoriesUnchanged=(($beforeProjects -join '|') -eq ($afterProjects -join '|'))}
$duplicateExitCode=$null
if($exited){$duplicateExitCode=$duplicate.ExitCode}
$destination="$PSScriptRoot\..\.artifacts\app-activation-$Phase-$([Guid]::NewGuid().ToString('N')).json"
@{timestamp=[DateTimeOffset]::UtcNow.ToString('O');phase=$Phase;app=$app;primaryId=$primary.Id;duplicateId=$duplicate.Id;duplicateExitCode=$duplicateExitCode;beforeMinimized=$beforeMinimized;checks=$checks;passed=(-not $checks.ContainsValue($false));manifest=@(Get-FileHash -Algorithm SHA256 -LiteralPath "$app\Glide.App.dll","$app\Glide.Capture.dll" | Select-Object Path,Hash);scope='Actual candidate duplicate launch, observed foreground/restoration and process identity. Recording continuity is checked from collected media separately.'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $destination -Encoding utf8
$checks | Format-Table
if($checks.ContainsValue($false)){exit 1}
