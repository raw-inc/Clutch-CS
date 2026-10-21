[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RecoveryScriptPath,
    [Parameter(Mandatory)][string] $RegistrationScriptPath
)
$ErrorActionPreference = 'Stop'
$observed = [Collections.Generic.List[object]]::new()
function New-Service([string] $Name, [string] $DisplayName, [string] $BinaryPathName, [string] $StartupType) {
    $observed.Add(@{action='register';name=$Name;displayName=$DisplayName;binary=$BinaryPathName;startup=$StartupType})
}
function Start-Service([string] $Name) { $observed.Add(@{action='start';name=$Name}) }

$invoke = {
    param($executable,$arguments)
    $observed.Add(@{action=$arguments[0];executable=$executable;arguments=@($arguments)})
    return 0
}.GetNewClosure()
& $RecoveryScriptPath -ServiceName fixture-service -CommandInvoker $invoke
if ($observed.Count -ne 2 -or $observed[0].executable -ne 'sc.exe' -or $observed[1].executable -ne 'sc.exe') { throw 'Recovery must invoke exactly two service-controller commands.' }
if (($observed[0].arguments -join ',') -ne 'failure,fixture-service,reset=,86400,actions=,restart/5000/restart/15000/restart/60000') { throw 'Recovery restart actions/reset arguments are incorrect.' }
if (($observed[1].arguments -join ',') -ne 'failureflag,fixture-service,1') { throw 'Non-crash failure recovery is not enabled.' }

# Exercise the native exit-code branch with a PowerShell command mock, never the OS executable.
if ($IsWindows) {
    $previousNativeExitCode = $global:LASTEXITCODE
    function sc.exe {
        $observed.Add(@{action=$args[0]})
        $global:LASTEXITCODE = $nativeExitCode
    }
    if ((Get-Command sc.exe).CommandType -ne 'Function') { throw 'The native service-controller mock was not installed.' }
    foreach ($nativeExitCode in @(0,5)) {
        $observed.Clear()
        $rejected = $false
        try { & $RecoveryScriptPath -ServiceName fixture-service } catch { $rejected = $true }
        $expectedCount = if ($nativeExitCode -eq 0) { 2 } else { 1 }
        if ($rejected -ne ($nativeExitCode -ne 0) -or $observed.Count -ne $expectedCount) { throw 'Native recovery exit code was ignored.' }
    }
    Remove-Item -LiteralPath Function:sc.exe
    $global:LASTEXITCODE = $previousNativeExitCode
}

foreach ($failure in @('failure','failureflag','unavailable','invalid-result')) {
    $observed.Clear()
    $failing = {
        param($executable,$arguments)
        $observed.Add(@{action=$arguments[0]})
        if ($failure -eq 'unavailable') { throw 'Service controller unavailable.' }
        if ($failure -eq 'invalid-result') { return }
        if ($failure -eq $arguments[0]) { return 5 }
        return 0
    }.GetNewClosure()
    $rejected = $false
    try { & $RecoveryScriptPath -ServiceName fixture-service -CommandInvoker $failing } catch { $rejected = $true }
    $expectedCount = if ($failure -eq 'failureflag') { 2 } else { 1 }
    if (-not $rejected -or $observed.Count -ne $expectedCount) { throw 'Failed recovery command was accepted or subsequent commands continued.' }
}

$observed.Clear()
& $RegistrationScriptPath -SecurityInvoker { param($action,$data) if ($action -ne 'secure' -or -not $data.installationRoot) { throw 'Invalid security request.' } } -RecoveryCommandInvoker $invoke
if (($observed.action -join ',') -ne 'register,failure,failureflag,start') { throw 'Manual registration did not configure recovery before starting.' }
if ($observed[0].displayName -ne "Clutch-CS ($($observed[0].name))") { throw 'The service display name must identify Clutch-CS and its service instance.' }
if ($observed[0].startup -ne 'Automatic' -or $observed[0].name -ne $observed[1].arguments[1] -or $observed[0].name -ne $observed[2].arguments[1] -or $observed[0].name -ne $observed[3].name) { throw 'Manual registration changed service identity or automatic startup.' }
foreach ($failure in @('failure','failureflag')) {
    $observed.Clear()
    $failing = {
        param($executable,$arguments)
        $observed.Add(@{action=$arguments[0]})
        if ($failure -eq $arguments[0]) { return 5 }
        return 0
    }.GetNewClosure()
    $rejected = $false
    try { & $RegistrationScriptPath -SecurityInvoker { param($action,$data) if ($action -ne 'secure' -or -not $data.installationRoot) { throw 'Invalid security request.' } } -RecoveryCommandInvoker $failing } catch { $rejected = $true }
    if (-not $rejected -or $observed.action -contains 'start') { throw 'Manual registration started after failed recovery configuration.' }
}
Write-Output 'PASS: service recovery commands/exit codes, explicit repair without registration/start, generated registration order and failure stop; no OS service was changed.'
