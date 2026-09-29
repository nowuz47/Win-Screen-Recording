param([Parameter(Mandatory=$true)][DateTimeOffset]$SinceUtc)
$ErrorActionPreference='Stop'
$cutoff=$SinceUtc.UtcDateTime
$projects=@(Get-ChildItem -LiteralPath "$env:LOCALAPPDATA\Glide\projects" -Directory | Where-Object { $capture=Join-Path $_.FullName 'capture\capture-info.json'; (Test-Path -LiteralPath $capture) -and (Get-Item -LiteralPath $capture).LastWriteTimeUtc -ge $cutoff })
if($projects.Count -ne 1){throw "Expected exactly one capture fixture in this review session; found $($projects.Count)."}
$destination="$PSScriptRoot\..\.artifacts\capture-cache-ui\run-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $destination | Out-Null
Copy-Item -LiteralPath $projects[0].FullName -Destination "$destination\project" -Recurse
$app=(Get-Content -LiteralPath "$env:LOCALAPPDATA\GlideDev\app-current.txt" -Raw).Trim()
Get-FileHash -Algorithm SHA256 -LiteralPath "$app\Glide.App.exe","$app\Glide.App.dll","$app\Glide.Core.dll","$app\Glide.Media.dll","$app\Glide.Capture.dll" | Select-Object Path,Hash | ConvertTo-Json | Set-Content -LiteralPath "$destination\build-manifest.json" -Encoding utf8
Get-Process Glide* | Select-Object Id,ProcessName,Path,MainWindowTitle,StartTime | ConvertTo-Json | Set-Content -LiteralPath "$destination\running-glide-processes.json" -Encoding utf8
$log="$env:LOCALAPPDATA\Glide\diagnostics\toolbar-actions.log"
if(Test-Path -LiteralPath $log){Get-Content -LiteralPath $log -Encoding utf8 | Where-Object { $_ -match '^([^ ]+) ' -and [DateTimeOffset]::Parse($Matches[1]).UtcDateTime -ge $cutoff } | Set-Content -LiteralPath "$destination\toolbar-actions.log" -Encoding utf8}
@{timestamp=[DateTimeOffset]::UtcNow.ToString('O');projectId=$projects[0].Name;app=$app;scope='One UI review recording. The collector does not verify the capture target, audio settings or user actions; companion review notes must identify those. No hardware performance, hotkey success or audible media claim.'} | ConvertTo-Json | Set-Content -LiteralPath "$destination\scope.json" -Encoding utf8
$destination | Set-Content -LiteralPath "$PSScriptRoot\..\.artifacts\capture-cache-ui-latest.txt" -Encoding utf8
