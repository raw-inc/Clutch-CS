#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ExecutablePath,
    [Parameter(Mandatory)][string] $StateDirectory,
    [string] $User = [Security.Principal.WindowsIdentity]::GetCurrent().Name,
    [uri] $Listen = 'http://127.0.0.1:5081/',
    [Guid] $InstallationId = [Guid]::Empty,
    [string] $StartScriptPath = (Join-Path $PSScriptRoot 'Start-Client.ps1'),
    [int] $StopTimeoutMilliseconds = 30000,
    [int] $PollIntervalMilliseconds = 250,
    [switch] $Remove
)
$ErrorActionPreference = 'Stop'
if (-not $Listen.IsLoopback -or $Listen.Scheme -ne 'http') { throw 'ClientHost must listen on a loopback HTTP URL.' }
if (-not [IO.Path]::IsPathRooted($ExecutablePath) -or (-not $Remove -and -not [IO.File]::Exists($ExecutablePath))) { throw 'An installed ClientHost executable is required.' }
if (-not [IO.Path]::IsPathRooted($StateDirectory)) { throw 'StateDirectory must be absolute.' }
$sid = (New-Object Security.Principal.NTAccount($User)).Translate([Security.Principal.SecurityIdentifier]).Value
if ($sid -in @('S-1-5-18','S-1-5-19','S-1-5-20')) { throw 'Choose the interactive Dashboard user, not a service account.' }
$name = 'Contents-Dashboard-' + $sid + '-' + $Listen.Port
$description = 'Start the local Clutch-CS WebUI with the saved network profile, without elevation.'
if ($InstallationId -ne [Guid]::Empty) {
    $name = 'Clutch-CS-WebClient-' + $InstallationId.ToString('N')
    $description = 'Clutch-CS Web Client installation ' + $InstallationId.ToString('D')
}
$existing = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
if ($existing) {
    $existingSid = $existing.Principal.UserId
    if (-not $existingSid.StartsWith('S-1-', [StringComparison]::Ordinal)) {
        $existingSid = (New-Object Security.Principal.NTAccount($existingSid)).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    if ($existingSid -ne $sid) { throw 'The startup task belongs to another user.' }
    if ($InstallationId -ne [Guid]::Empty -and $existing.Description -ne $description) { throw 'The startup task belongs to another installation.' }
}
if ($Remove) {
    if ($existing) {
        Stop-ScheduledTask -TaskName $name
        $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($StopTimeoutMilliseconds)
        while ((Get-ScheduledTask -TaskName $name).State -in @('Running', 'Queued')) {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'The startup task has not stopped. The installed files were retained.' }
            Start-Sleep -Milliseconds $PollIntervalMilliseconds
        }
        Unregister-ScheduledTask -TaskName $name -Confirm:$false
    }
    return
}
$powershell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
if (-not [IO.Path]::IsPathRooted($StartScriptPath) -or -not [IO.File]::Exists($StartScriptPath)) { throw 'The installed ClientHost launcher is required.' }
# 手動起動と同じ所有確認・HTTP確認を使い、実行ポリシーは変更しません。
$command = '& ([ScriptBlock]::Create([IO.File]::ReadAllText(''' + [IO.Path]::GetFullPath($StartScriptPath).Replace("'", "''") + '''))) -ExecutablePath ''' + [IO.Path]::GetFullPath($ExecutablePath).Replace("'", "''") + ''' -StateDirectory ''' + [IO.Path]::GetFullPath($StateDirectory).Replace("'", "''") + ''' -Listen ''' + $Listen.AbsoluteUri + ''' -NoBrowser'
$arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ExecutablePath)))
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description $description -Force | Out-Null
Start-ScheduledTask -TaskName $name
Write-Output $name
