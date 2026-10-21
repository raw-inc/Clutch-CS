#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string] $ServiceName = 'ReArtWord.Contents.BackgroundService',
    [scriptblock] $CommandInvoker
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -and -not $CommandInvoker) { throw 'Windows service recovery configuration requires Windows PowerShell 7.' }

# Only configure recovery for an existing service; never register, start or restart it here.
$commands = @(
    @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000'),
    @('failureflag', $ServiceName, '1')
)
foreach ($arguments in $commands) {
    if ($CommandInvoker) {
        $exitCode = & $CommandInvoker 'sc.exe' $arguments
        if ($exitCode -isnot [int]) { throw 'The recovery command invoker must return an integer exit code.' }
    } else {
        & sc.exe @arguments | Write-Verbose
        $exitCode = $LASTEXITCODE
    }
    if ($exitCode -ne 0) { throw "Windows service recovery configuration failed at '$($arguments[0])' (exit code $exitCode)." }
}
