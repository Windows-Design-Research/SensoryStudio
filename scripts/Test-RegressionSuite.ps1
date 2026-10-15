#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BuildOutput,
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$node = (Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$started = [DateTime]::UtcNow
if (-not $ResultsDirectory) {
    $ResultsDirectory = Join-Path $repo "artifacts\regression\$($started.ToString('yyyyMMddTHHmmssfff'))"
}

if (-not $BuildOutput) {
    $targetDirectory = & dotnet msbuild (Join-Path $repo 'src\SensoryStudio.WinUI\SensoryStudio.WinUI.csproj') `
        -nologo --verbosity:quiet "-p:Configuration=$Configuration" -p:Platform=x64 -getProperty:TargetDir
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the x64 build output.' }
    $BuildOutput = ($targetDirectory -join "`n").Trim()
}
$BuildOutput = (Resolve-Path -LiteralPath $BuildOutput).Path
foreach ($name in @('SensoryStudio.WinUI.dll', 'WinRT.Runtime.dll', 'Microsoft.Windows.SDK.NET.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $BuildOutput $name) -PathType Leaf)) {
        throw "Build the x64 app before running regression checks; missing $name in $BuildOutput."
    }
}
$revision = & git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not record the source revision.' }
$changes = @(& git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Could not record working-tree state.' }
$sdkVersion = & dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'Could not record the .NET SDK version.' }
$nodeVersion = & $node --version
if ($LASTEXITCODE -ne 0) { throw 'Could not record the Node version.' }

if ((Test-Path -LiteralPath $ResultsDirectory) -and
    @(Get-ChildItem -LiteralPath $ResultsDirectory -Force).Count) {
    throw 'Choose an empty results directory so stale reports cannot be published.'
}
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$ResultsDirectory = (Resolve-Path -LiteralPath $ResultsDirectory).Path
$tests = @(
    'Test-AppSettings', 'Test-CrashReports', 'Test-HapticFallback',
    'Test-SoundToggle', 'Test-WebViewBoundaries', 'Test-ExperienceCatalog',
    'Test-CompactResizeCoordinates',
    'Test-CompactResizeInput', 'Test-CompactWindowFrame',
    'Test-CompactWindowGeometry', 'Test-WindowPlacement', 'Test-WinUIOnlyRepository'
)
$results = [System.Collections.Generic.List[object]]::new()
foreach ($name in @($tests) + @('test-zen-garden-audio', 'test-zen-garden-assets')) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $ResultsDirectory "$name.log"
    if ($name -like 'test-zen-garden-*') {
        $arguments = @((Join-Path $PSScriptRoot "$name.cjs"))
        if ($name -eq 'test-zen-garden-assets') {
            $arguments += $BuildOutput
        }
        & $node @arguments 2>&1 | Tee-Object -FilePath $log
    }
    else {
        $arguments = @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot "$name.ps1"))
        if ($name -in @('Test-CompactWindowGeometry', 'Test-WindowPlacement')) {
            $arguments += @('-BuildOutput', $BuildOutput)
        }
        & $pwsh @arguments 2>&1 | Tee-Object -FilePath $log
    }
    $exitCode = $LASTEXITCODE
    $timer.Stop()
    $results.Add([pscustomobject]@{
        Name = $name
        ExitCode = $exitCode
        Seconds = $timer.Elapsed.TotalSeconds
        Log = "$name.log"
    })
}

$summary = [ordered]@{
    StartedUtc = $started.ToString('o')
    CompletedUtc = [DateTime]::UtcNow.ToString('o')
    SourceCommit = $revision
    WorkingTreeDirty = $changes.Count -ne 0
    PipelineSourceCommit = $env:BUILD_SOURCEVERSION
    BuildId = $env:BUILD_BUILDID
    BuildDefinitionId = $env:SYSTEM_DEFINITIONID
    TemplateCommit = $env:SENSORY_TEMPLATE_COMMIT
    Configuration = $Configuration
    PowerShell = $PSVersionTable.PSVersion.ToString()
    DotNetSdk = $sdkVersion
    Node = $nodeVersion
    TestedAssemblySHA256 = (Get-FileHash -LiteralPath (Join-Path $BuildOutput 'SensoryStudio.WinUI.dll')).Hash
    Results = @($results.ToArray())
}
$summary | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ResultsDirectory 'summary.json') -Encoding utf8

[xml]$report = '<testsuites><testsuite name="SensoryStudio regressions" /></testsuites>'
$suite = $report.testsuites.testsuite
$failures = @($results | Where-Object ExitCode -ne 0)
$suite.SetAttribute('tests', [string]$results.Count)
$suite.SetAttribute('failures', [string]$failures.Count)
$suite.SetAttribute('timestamp', $started.ToString('o'))
foreach ($result in $results) {
    $testCase = $report.CreateElement('testcase')
    $testCase.SetAttribute('name', $result.Name)
    $testCase.SetAttribute('classname', 'SensoryStudio.Regression')
    $testCase.SetAttribute('time', $result.Seconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture))
    if ($result.ExitCode -ne 0) {
        $failure = $report.CreateElement('failure')
        $failure.SetAttribute('message', "Exit code $($result.ExitCode); see $($result.Log) in the evidence artifact.")
        [void]$testCase.AppendChild($failure)
    }
    [void]$suite.AppendChild($testCase)
}
$report.Save((Join-Path $ResultsDirectory 'results.xml'))
if ($failures.Count) {
    throw "$($failures.Count) regression scripts failed. Reports: $ResultsDirectory"
}
Write-Output "Passed all $($results.Count) regression scripts. Reports: $ResultsDirectory"
