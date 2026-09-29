$ErrorActionPreference = 'Stop'
$fixture = $null
foreach ($run in (Get-ChildItem -LiteralPath "$env:LOCALAPPDATA\GlideDev\render-results" -Directory | Sort-Object LastWriteTimeUtc -Descending)) {
    if (!(Test-Path -LiteralPath "$($run.FullName)\result.json")) { continue }
    foreach ($project in (Get-ChildItem -LiteralPath "$($run.FullName)\projects" -Directory)) {
        $metadata = Get-Content -LiteralPath "$($project.FullName)\project.json" -Raw | ConvertFrom-Json
        if ($metadata.name -eq 'Controlled audio on actual video' -and $metadata.audio.Count -eq 2) { $fixture = $project; break }
    }
    if ($fixture) { break }
}
if (!$fixture) { throw 'Run the audio render tests first.' }
$target = "$env:LOCALAPPDATA\Glide\projects\$($fixture.Name)"
if (Test-Path -LiteralPath $target) { throw 'Test project is already staged; existing edits are retained.' }
Copy-Item -LiteralPath $fixture.FullName -Destination $target -Recurse
$metadata = Get-Content -LiteralPath "$target\project.json" -Raw | ConvertFrom-Json
@{ timestamp=[DateTimeOffset]::UtcNow; projectId=$metadata.id; source=$fixture.FullName; destination=$target; syntheticPcm=$true; actualWgcVideo=$true } | ConvertTo-Json | Set-Content -LiteralPath "$PSScriptRoot\..\.artifacts\audio-ui-staged.json" -Encoding utf8
