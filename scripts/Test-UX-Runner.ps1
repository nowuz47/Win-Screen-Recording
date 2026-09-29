$ErrorActionPreference = 'Stop'
$fixture = Join-Path $env:TEMP ('Glide-ux-runner-' + [guid]::NewGuid().ToString('N'))
$results = @()
try {
    foreach ($scenario in @(
        @{ Name = 'success'; Codes = @(0,0,0); Exit = 0; Order = 'native,presentation,app' },
        @{ Name = 'native-failure'; Codes = @(2,0,0); Exit = 2; Order = 'native' },
        @{ Name = 'presentation-failure'; Codes = @(0,7,0); Exit = 7; Order = 'native,presentation' },
        @{ Name = 'app-failure'; Codes = @(0,0,9); Exit = 9; Order = 'native,presentation,app' }
    )) {
        $folder = Join-Path $fixture $scenario.Name
        $scripts = Join-Path $folder 'scripts'
        New-Item -ItemType Directory -Path $scripts -Force | Out-Null
        Copy-Item (Join-Path $PSScriptRoot 'Verify-UX.cmd') $scripts
        $files = @('Build-Native.cmd','Test-Presentation.cmd','Build-App.cmd')
        $stages = @('native','presentation','app')
        for ($index = 0; $index -lt 3; $index++) {
            @('@echo off', ('>> "%~dp0..\order.txt" echo ' + $stages[$index]),
                ('exit /b ' + $scenario.Codes[$index])) | Set-Content (Join-Path $scripts $files[$index]) -Encoding ASCII
        }
        & (Join-Path $scripts 'Verify-UX.cmd') | Out-Null
        $actualExit = $LASTEXITCODE
        $order = (Get-Content (Join-Path $folder 'order.txt')) -join ','
        $status = Get-Content (Join-Path $folder '.artifacts\ux-verification\latest-status.txt') -Raw
        $logs = @(Get-ChildItem (Join-Path $folder '.artifacts\ux-verification') -Filter 'run-*.log')
        $passed = $actualExit -eq $scenario.Exit -and $order -eq $scenario.Order -and $logs.Count -eq 1
        if ($scenario.Exit -eq 0) { $passed = $passed -and $status.Contains('PASS') }
        else { $passed = $passed -and $status.Contains('FAIL') -and $status.Contains("exit=$actualExit") }
        $results += [pscustomobject]@{ name=$scenario.Name; passed=$passed; exit=$actualExit; order=$order; status=$status.Trim() }
        Write-Host "$(if ($passed) {'PASS'} else {'FAIL'}) $($scenario.Name)"
    }
    $failed = @($results | Where-Object { -not $_.passed }).Count
    $evidence = Join-Path $PSScriptRoot '..\.artifacts'
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    @{ timestamp=[DateTimeOffset]::UtcNow.ToString('O'); scope='Verification runner only, using isolated stub stages; not app regression results'; tests=$results.Count; failed=$failed; results=$results } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $evidence 'ux-runner-tests.json') -Encoding UTF8
    if ($failed -gt 0) { exit 1 }
} finally {
    if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
}

# A successful suite must not inherit the final injected child exit code.
exit 0
