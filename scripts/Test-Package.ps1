param(
    [string] $UnityPath = "${env:ProgramFiles}\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe",
    [ValidateSet('Unit', 'Network', 'All')]
    [string] $Category = 'Unit',
    [string] $WorkDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'AtProtoUnity-package-tests')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $UnityPath)) {
    throw "Unity Editor not found at $UnityPath. Specify -UnityPath."
}

$repository = Split-Path $PSScriptRoot -Parent
$work = [System.IO.Path]::GetFullPath($WorkDirectory)
$package = (Join-Path $repository 'AtProtoUnity').Replace('\', '/')
foreach ($directory in @('Assets', 'Packages', 'ProjectSettings')) {
    [System.IO.Directory]::CreateDirectory((Join-Path $work $directory)) | Out-Null
}
$manifest = @{
    dependencies = @{
        'actor.rpg.atproto-unity' = "file:$package"
        'com.unity.test-framework' = '1.6.0'
    }
    testables = @('actor.rpg.atproto-unity')
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $work 'Packages\manifest.json'), ($manifest | ConvertTo-Json -Depth 4), $utf8)
$version = Get-Content (Join-Path $repository 'AtProtoUnityProject\ProjectSettings\ProjectVersion.txt') -Raw
[System.IO.File]::WriteAllText((Join-Path $work 'ProjectSettings\ProjectVersion.txt'), $version, $utf8)

$results = Join-Path $work "results-$Category.xml"
$log = Join-Path $work "editor-$Category.log"
if (Test-Path $results) {
    Remove-Item $results
}
$arguments = @(
    '-batchmode',
    '-projectPath', "`"$work`"",
    '-runTests', '-testPlatform', 'EditMode',
    '-assemblyNames', 'AtProtoUnity.Tests.Editor',
    '-testResults', "`"$results`"",
    '-logFile', "`"$log`""
)
if ($Category -ne 'All') {
    $arguments += @('-testCategory', $Category)
}

$process = Start-Process -FilePath $UnityPath -ArgumentList $arguments -PassThru
$process.WaitForExit()
if (-not (Test-Path $results)) {
    if (Test-Path $log) {
        Get-Content $log -Tail 60 | Write-Output
    }
    throw "Unity did not produce test results. Exit code: $($process.ExitCode). Log: $log"
}
[xml] $report = Get-Content $results -Raw
$run = $report.'test-run'
Write-Output "Tests: $($run.total), passed: $($run.passed), failed: $($run.failed), skipped: $($run.skipped)"
Write-Output "Results: $results"
if ($process.ExitCode -ne 0 -or [int]$run.failed -gt 0 -or [int]$run.total -eq 0) {
    $report.SelectNodes('//test-case[@result="Failed"]') | ForEach-Object {
        Write-Output $_.fullname
        Write-Output $_.failure.message
    }
    throw "Unity package tests failed. Log: $log"
}