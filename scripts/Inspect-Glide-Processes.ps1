$ErrorActionPreference='Stop'
$destination="$PSScriptRoot\..\.artifacts\glide-processes-$([Guid]::NewGuid().ToString('N')).json"
$entries=@(Get-Process Glide* | Select-Object Id,ProcessName,Path,MainWindowTitle,StartTime,CPU,Responding)
@{timestamp=[DateTimeOffset]::UtcNow.ToString('O');processes=$entries} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $destination -Encoding utf8
$entries | Format-Table Id,ProcessName,MainWindowTitle,Responding
