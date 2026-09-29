$ErrorActionPreference='Stop'
$work=Join-Path $env:TEMP ('Glide-runner-check-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$results=@()
try{
    foreach($name in @('Verify-Modes.cmd','Verify-Instance.cmd')){
        $source=Join-Path $PSScriptRoot $name
        $body=Get-Content -LiteralPath $source -Raw
        $steps=@([regex]::Matches($body,'call "%~dp0([^"\r\n]+)"') | ForEach-Object {$_.Groups[1].Value})
        $cases=@(@{index=-1;code=0})
        for($i=0;$i -lt $steps.Count;$i++){foreach($code in @(1,-1,-1073741819)){$cases+=@{index=$i;code=$code}}}
        foreach($case in $cases){
            $folder=Join-Path $work ($name+'-'+$case.index+'-'+$case.code)
            New-Item -ItemType Directory -Path $folder | Out-Null
            Copy-Item -LiteralPath $source -Destination (Join-Path $folder $name)
            for($i=0;$i -lt $steps.Count;$i++){
                $code=if($i -eq $case.index){$case.code}else{0}
                $stub="@echo off`r`n>>`"%~dp0executed.log`" echo $($steps[$i])`r`nexit /b $code`r`n"
                Set-Content -LiteralPath (Join-Path $folder $steps[$i]) -Value $stub -Encoding ascii
            }
            & $env:ComSpec /d /c (Join-Path $folder $name) *> (Join-Path $folder 'output.log')
            $exitCode=$LASTEXITCODE
            $actual=@([IO.File]::ReadAllLines((Join-Path $folder 'executed.log')))
            $count=if($case.index -lt 0){$steps.Count}else{$case.index+1}
            $expected=@($steps | Select-Object -First $count)
            $passed=($actual -join '|') -eq ($expected -join '|') -and (($case.index -lt 0 -and $exitCode -eq 0) -or ($case.index -ge 0 -and $exitCode -ne 0))
            $results+=@{verifier=$name;injectedStage=$case.index;injectedExitCode=$case.code;exitCode=$exitCode;executed=$actual;passed=$passed}
        }
    }
    # Exercise the actual render fixture preflight with only its probe/path
    # dependencies replaced. This catches CMD's early %ERRORLEVEL% expansion
    # inside parenthesized blocks, without touching the real render fixture.
    $renderSource=Join-Path $PSScriptRoot 'Test-Render.cmd'
    $renderBody=Get-Content -LiteralPath $renderSource -Raw
    $preflight=$renderBody.Substring(0,$renderBody.IndexOf('robocopy '))
    foreach($case in @(@{exists=$false;code=0},@{exists=$false;code=1},@{exists=$false;code=-1},@{exists=$false;code=-1073741819},@{exists=$true;code=0})){
        $folder=Join-Path $work ('render-'+$case.exists+'-'+$case.code)
        New-Item -ItemType Directory -Path $folder | Out-Null
        $fixture=Join-Path $folder 'fixture'
        New-Item -ItemType Directory -Path $fixture | Out-Null
        if($case.exists){Set-Content -LiteralPath (Join-Path $fixture 'probe-result.json') -Value '{}' -Encoding ascii}
        $body=$preflight.Replace('set "GLIDE_FIXTURE=%LOCALAPPDATA%\GlideDev\render-fixture"',"set `"GLIDE_FIXTURE=$fixture`"")
        $body=$body.Replace('"%LOCALAPPDATA%\GlideDev\native-x64\glide-capture-probe.exe" "%GLIDE_FIXTURE%"','call "%~dp0probe.cmd"')
        $body+="`r`n>>`"%~dp0executed.log`" echo ready`r`nexit /b 0`r`n"
        Set-Content -LiteralPath (Join-Path $folder 'preflight.cmd') -Value $body -Encoding ascii
        Set-Content -LiteralPath (Join-Path $folder 'probe.cmd') -Value "@echo off`r`n>>`"%~dp0executed.log`" echo probe`r`nexit /b $($case.code)`r`n" -Encoding ascii
        & $env:ComSpec /d /c (Join-Path $folder 'preflight.cmd') *> (Join-Path $folder 'output.log')
        $exitCode=$LASTEXITCODE
        $actual=@([IO.File]::ReadAllLines((Join-Path $folder 'executed.log')))
        $expected=if($case.exists){'ready'}elseif($case.code -eq 0){'probe|ready'}else{'probe'}
        $passed=($actual -join '|') -eq $expected -and (($case.code -eq 0 -and $exitCode -eq 0) -or ($case.code -ne 0 -and $exitCode -ne 0))
        $results+=@{verifier='Test-Render.cmd preflight';fixtureExists=$case.exists;injectedExitCode=$case.code;exitCode=$exitCode;executed=$actual;passed=$passed}
    }
    $destination="$PSScriptRoot\..\.artifacts\verification-exit-codes-$([Guid]::NewGuid().ToString('N')).json"
    $failed=@($results | Where-Object {-not $_.passed}).Count
    @{timestamp=[DateTimeOffset]::UtcNow.ToString('O');tests=$results.Count;failed=$failed;results=$results;scope='Actual CMD verifier files and render fixture preflight with disposable child-command stubs; validates fail-stop orchestration, not application functionality.';sourceHashes=@(Get-FileHash -Algorithm SHA256 -LiteralPath "$PSScriptRoot\Verify-Modes.cmd","$PSScriptRoot\Verify-Instance.cmd","$PSScriptRoot\Test-Render.cmd" | Select-Object Path,Hash)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $destination -Encoding utf8
    Write-Output "$($results.Count) CMD exit-code cases, $failed failures"
    if($failed -ne 0){exit 1}
}finally{Remove-Item -LiteralPath $work -Recurse -Force}

# A successful suite must not inherit the final injected child exit code.
exit 0
