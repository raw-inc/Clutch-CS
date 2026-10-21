#Requires -Version 5.1
# タスクスケジューラをメモリ内の境界へ差し替え、実際の起動登録は変更しません。
$ErrorActionPreference = 'Stop'
$global:clutchStartupFixture = @{ Task=$null; Started=0; Registered=0; Removed=0; Stopped=0 }
function Get-ScheduledTask { [CmdletBinding()] param($TaskName) $global:clutchStartupFixture.Task }
function New-ScheduledTaskAction { param($Execute, $Argument, $WorkingDirectory) [pscustomobject]@{ Execute=$Execute; Arguments=$Argument; WorkingDirectory=$WorkingDirectory } }
function New-ScheduledTaskTrigger { param([switch]$AtLogOn, $User) [pscustomobject]@{ User=$User; AtLogOn=$AtLogOn } }
function New-ScheduledTaskPrincipal { param($UserId, $LogonType, $RunLevel) [pscustomobject]@{ UserId=$UserId; LogonType=$LogonType; RunLevel=$RunLevel } }
function New-ScheduledTaskSettingsSet { param([switch]$AllowStartIfOnBatteries, [switch]$DontStopIfGoingOnBatteries, [switch]$StartWhenAvailable, $MultipleInstances, $ExecutionTimeLimit) [pscustomobject]@{ MultipleInstances=$MultipleInstances } }
function Register-ScheduledTask {
    param($TaskName, $Action, $Trigger, $Principal, $Settings, $Description, [switch]$Force)
    $global:clutchStartupFixture.Registered++
    $global:clutchStartupFixture.Task = [pscustomobject]@{ Name=$TaskName; Action=$Action; Trigger=$Trigger; Principal=$Principal; Settings=$Settings; Description=$Description; State='Running' }
}
function Start-ScheduledTask { param($TaskName) $global:clutchStartupFixture.Started++ }
function Stop-ScheduledTask { param($TaskName) $global:clutchStartupFixture.Stopped++; $global:clutchStartupFixture.Task.State='Ready' }
function Unregister-ScheduledTask { param($TaskName, [switch]$Confirm) $global:clutchStartupFixture.Removed++; $global:clutchStartupFixture.Task=$null }
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('clutch-startup-tests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$script = Join-Path $PSScriptRoot '../../installer/Register-ClientStartup.ps1'
$entry = Join-Path $fixture 'ClientHost.exe'
$launcher = Join-Path $fixture 'Start-Client.ps1'
[IO.File]::WriteAllText($entry, 'fixture')
[IO.File]::WriteAllText($launcher, 'fixture')
$arguments = @{ ExecutablePath=$entry; StateDirectory=(Join-Path $fixture 'state'); Listen='http://127.0.0.1:5182/'; InstallationId=[Guid]::NewGuid(); StartScriptPath=$launcher }
try {
    & $script @arguments | Out-Null
    $task = $global:clutchStartupFixture.Task
    Assert-True ($task.Principal.RunLevel -eq 'Limited' -and $task.Principal.LogonType -eq 'Interactive') 'The task must use the interactive user without elevation.'
    Assert-True ($task.Trigger.AtLogOn -and $global:clutchStartupFixture.Started -eq 1) 'Startup was not registered and started.'
    $encoded = ($task.Action.Arguments -split ' -EncodedCommand ')[1]
    $command = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($encoded))
    Assert-True ($command.Contains($entry) -and $command.Contains($launcher) -and $command.Contains('-NoBrowser')) 'The verified launcher or fixed installation was lost.'
    & $script @arguments | Out-Null
    Assert-True ($global:clutchStartupFixture.Registered -eq 2 -and $global:clutchStartupFixture.Task.Name -eq $task.Name) 'Repair did not reuse the same startup name.'
    $task = $global:clutchStartupFixture.Task
    $task.Description = 'Another installation'
    $rejected = $false
    try { & $script @arguments | Out-Null } catch { $rejected = $true }
    Assert-True ($rejected -and $global:clutchStartupFixture.Registered -eq 2) 'Another installation was overwritten.'
    $task.Description = 'Clutch-CS Web Client installation ' + $arguments.InstallationId.ToString('D')
    Remove-Item -LiteralPath $entry, $launcher
    & $script @arguments -Remove
    Assert-True ($global:clutchStartupFixture.Stopped -eq 1 -and $global:clutchStartupFixture.Removed -eq 1 -and $null -eq $global:clutchStartupFixture.Task) 'Startup was not stopped and removed before uninstall.'
    & $script @arguments -Remove
    Assert-True ($global:clutchStartupFixture.Removed -eq 1) 'An absent startup task was removed twice.'
    Write-Output 'Windows user startup: registration, limited privileges, repair, ownership and removal passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFileName($resolved).StartsWith('clutch-startup-tests-')) { throw 'Unexpected fixture path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    Remove-Variable clutchStartupFixture -Scope Global
}
