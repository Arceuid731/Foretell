param([string]$Dotnet = 'dotnet')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
foreach ($project in @('ForetellRuntimeTests', 'ForetellCoreTests')) {
    $state = & $Dotnet run --project $project -c Release --no-build -- --test-host-state
    if ($LASTEXITCODE -ne 0) { throw "$project could not verify non-interactive error handling" }
    $state | Write-Output
    $failure = & $Dotnet run --project $project -c Release --no-build -- --test-host-failure 2>&1
    if ($LASTEXITCODE -ne 1 -or ($failure | Out-String) -notmatch 'Controlled test-host failure probe') {
        throw "$project did not report a controlled failure with exit code 1"
    }
    Write-Output "${project}: controlled failure logged with exit code 1."
}
$global:LASTEXITCODE = 0
