#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Project = (Join-Path $PSScriptRoot 'Clutch-CS.slnx'),
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug',
    [switch] $NoRestore
)
$ErrorActionPreference = 'Stop'
$version = & (Join-Path $PSScriptRoot 'version.ps1') show
$arguments = @('build', $Project, '-c', $Configuration)
if ($NoRestore) { $arguments += '--no-restore' }
Write-Host "Building Clutch-CS $version"
& dotnet @arguments
exit $LASTEXITCODE
