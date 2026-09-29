$ErrorActionPreference = 'Stop'
$staged = Get-Content -LiteralPath "$PSScriptRoot\..\.artifacts\audio-ui-staged.json" -Raw | ConvertFrom-Json
$id = $staged.projectId
if ($id -notmatch '^[a-f0-9]{32}$') { throw 'Invalid fixture identifier.' }
$evidence = "$PSScriptRoot\..\.artifacts\audio-ui\run-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $evidence | Out-Null
Copy-Item -LiteralPath "$env:LOCALAPPDATA\Glide\projects\$id" -Destination "$evidence\project" -Recurse
$appDirectory = (Get-Content -LiteralPath "$env:LOCALAPPDATA\GlideDev\app-current.txt" -Raw).Trim()
Get-FileHash -Algorithm SHA256 -LiteralPath "$appDirectory\Glide.App.exe","$appDirectory\Glide.App.dll","$appDirectory\Glide.Core.dll","$appDirectory\Glide.Media.dll","$appDirectory\Glide.Capture.dll" | Select-Object Path,Hash | ConvertTo-Json | Set-Content -LiteralPath "$evidence\build-manifest.json" -Encoding utf8
Get-ChildItem -LiteralPath "$env:LOCALAPPDATA\GlideDev" -File -Filter 'Glide-*.mp4' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $evidence }
@{ timestamp=[DateTimeOffset]::UtcNow.ToString('O'); projectId=$id; app=$appDirectory; scope='Only the staged audio UI fixture and its review exports. GUI actions are recorded separately.' } | ConvertTo-Json | Set-Content -LiteralPath "$evidence\scope.json" -Encoding utf8
