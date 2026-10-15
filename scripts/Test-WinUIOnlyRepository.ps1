#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Get-Content (Join-Path $root 'SensoryStudio.sln') -Raw
$projects = [regex]::Matches($solution, '(?m)^Project\("[^"]+"\) = "([^"]+)", "([^"]+)", "([^"]+)"')
if ($projects.Count -ne 1 -or
    $projects[0].Groups[1].Value -ne 'SensoryStudio.WinUI' -or
    $projects[0].Groups[2].Value -ne 'src\SensoryStudio.WinUI\SensoryStudio.WinUI.csproj') {
    throw 'The solution must contain only the WinUI application.'
}

$id = [regex]::Escape($projects[0].Groups[3].Value)
foreach ($configuration in @('Debug', 'Release')) {
    foreach ($platform in @('Any CPU', 'x64', 'ARM64')) {
        $target = if ($platform -eq 'Any CPU') { 'x64' } else { $platform }
        foreach ($entry in @('ActiveCfg', 'Build.0')) {
            $key = [regex]::Escape("$configuration|$platform.$entry")
            $value = [regex]::Escape("$configuration|$target")
            if ($solution -notmatch "(?m)^\s*$id\.$key = $value\r?$") {
                throw "Incorrect solution mapping: $configuration|$platform.$entry"
            }
        }
    }
}

# Check version-controlled and new source files, not ignored local build caches.
$files = @(& git -C $root ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate repository source files.' }
$files = @($files | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf })
$retired = @($files | Where-Object {
    $_ -like 'legacy/*' -or
    $_ -match '(^|/)(package(-lock)?\.json|npm-shrinkwrap\.json|yarn\.lock|pnpm-lock\.yaml|vite\.config\.[^/]+)$'
})
if ($retired.Count) {
    throw "Retired source or npm toolchain remains: $($retired -join ', ')"
}
foreach ($path in @('docs\EngineeringSpecification.md', 'docs\SelfhostInstructions.md', 'UI_FEATURE_INVENTORY.md')) {
    if (Test-Path -LiteralPath (Join-Path $root $path)) { throw "Retired WPF guide remains: $path" }
}
$projectFiles = @($files | Where-Object { $_ -like '*.csproj' })
if ($projectFiles.Count -ne 1 -or $projectFiles[0] -ne 'src/SensoryStudio.WinUI/SensoryStudio.WinUI.csproj') {
    throw 'Unexpected application project remains in the repository.'
}
[xml]$project = Get-Content (Join-Path $root $projectFiles[0]) -Raw
if ($project.SelectNodes('//ProjectReference').Count -ne 0) {
    throw 'Review project references before adding another project to the WinUI-only application.'
}
Write-Host 'Passed WinUI-only source/solution checks, including all 12 architecture mappings; ignored build caches were not inspected.'
