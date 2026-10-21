#Requires -Version 5.1
<#
.SYNOPSIS
Previews or promotes only the specified Clutch-CS Remote Desktop icons for a loaded user profile.
.DESCRIPTION
Windows 11's NotifyIconSettings is an undocumented user preference, not a supported Shell policy.
Existing GUID registrations only; no Explorer restart, notifications, global tray changes, or hive loading.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]] $CoreExecutablePath,
    [string] $UserSid,
    [switch] $Apply,
    [string] $BackupPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows is required.' }
$absolutePattern = '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+[\\/])'
if ($Apply -and (-not $BackupPath -or $BackupPath -notmatch $absolutePattern)) { throw 'Apply requires a new absolute BackupPath.' }
if ($Apply -and [IO.File]::Exists($BackupPath + '.result.json')) { throw 'An earlier result already exists for this backup path.' }
if (-not $UserSid) {
    $account = (Get-CimInstance Win32_ComputerSystem).UserName
    if (-not $account) { throw 'No console user is logged on. Specify a loaded user SID explicitly.' }
    $UserSid = ([Security.Principal.NTAccount]::new($account)).Translate([Security.Principal.SecurityIdentifier]).Value
}
$sid = [Security.Principal.SecurityIdentifier]::new($UserSid)
if (-not $sid.AccountDomainSid) { throw 'A user account SID is required; service identities are not tray users.' }
$userKey = [Microsoft.Win32.Registry]::Users.OpenSubKey($sid.Value)
if (-not $userKey) { throw 'The requested user profile is not loaded.' }
$userKey.Dispose()
$targets = [Collections.Generic.Dictionary[string,guid]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($executable in $CoreExecutablePath) {
    if ($executable -notmatch $absolutePattern) { throw 'CoreExecutablePath must be absolute.' }
    $path = [IO.Path]::GetFullPath($executable)
    if ([IO.Path]::GetFileName($path) -ne 'Clutch-CS.CoreService.exe' -or -not [IO.File]::Exists($path)) { throw 'Specify an installed Clutch-CS CoreService executable.' }
    # The v1 identity is shared with DesktopTrayIdentity; preserve it across in-place deployments.
    $identity = "ReArtWord.Contents.RemoteDesktop.Tray.v1`n" + $path.ToUpperInvariant() + "`nReArtWord.Contents.RemoteDesktop.Connection.v1"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity)) } finally { $sha.Dispose() }
    $targets[$path] = [guid]::new([byte[]]$hash[0..15])
}
$rootPath = $sid.Value + '\Control Panel\NotifyIconSettings'
$root = [Microsoft.Win32.Registry]::Users.OpenSubKey($rootPath)
$entries = [Collections.Generic.List[object]]::new()
try {
    if ($root) {
        foreach ($name in $root.GetSubKeyNames()) {
            $key = $root.OpenSubKey($name)
            if (-not $key) { continue }
            try {
                $path = [string]$key.GetValue('ExecutablePath', '')
                $guid = [guid]::Empty
                if (-not $targets.ContainsKey($path) -or -not [guid]::TryParse([string]$key.GetValue('IconGuid', ''), [ref]$guid) -or $guid -ne $targets[$path]) { continue }
                $exists = $key.GetValueNames() -contains 'IsPromoted'
                $value = $key.GetValue('IsPromoted', $null)
                $kind = if ($exists) { $key.GetValueKind('IsPromoted').ToString() } else { $null }
                $status = if ($exists -and $kind -ne 'DWord') { 'UnsupportedValueKind' } elseif ($value -eq 1) { 'AlreadyPromoted' } else { 'WouldPromote' }
                $entries.Add([pscustomobject]@{executable=$path;iconGuid=$guid.ToString('D');key=$rootPath+'\'+$name;previousValueExists=$exists;previousValue=$value;previousValueKind=$kind;status=$status})
            } finally { $key.Dispose() }
        }
    }
} finally { if ($root) { $root.Dispose() } }
foreach ($path in $targets.Keys) {
    if (-not @($entries | Where-Object { $_.executable -eq $path }).Count) {
        $entries.Add([pscustomobject]@{executable=$path;iconGuid=$targets[$path].ToString('D');key=$null;previousValueExists=$false;previousValue=$null;previousValueKind=$null;status='NotRegistered'})
    }
}
$report = [pscustomobject]@{schema=1;at=[DateTimeOffset]::UtcNow.ToString('o');computer=$env:COMPUTERNAME;userSid=$sid.Value;applied=[bool]$Apply;entries=@($entries.ToArray());error=$null}
if ($Apply) {
    # Persist the complete prior state before the first write; never overwrite an earlier operation.
    $backup = [IO.File]::Open([IO.Path]::GetFullPath($BackupPath), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($report | ConvertTo-Json -Depth 6))
        $backup.Write($bytes, 0, $bytes.Length)
        $backup.Flush($true)
    } finally { $backup.Dispose() }
    try {
        foreach ($entry in $entries) {
            if ($entry.status -ne 'WouldPromote') { continue }
            $key = [Microsoft.Win32.Registry]::Users.OpenSubKey($entry.key, $true)
            if (-not $key) { throw 'The icon registration changed after the preview.' }
            try {
                $exists = $key.GetValueNames() -contains 'IsPromoted'
                if ($key.GetValue('ExecutablePath', '') -ne $entry.executable -or [guid]$key.GetValue('IconGuid', '') -ne [guid]$entry.iconGuid -or $exists -ne $entry.previousValueExists -or $key.GetValue('IsPromoted', $null) -ne $entry.previousValue -or ($exists -and $key.GetValueKind('IsPromoted').ToString() -ne $entry.previousValueKind)) { throw 'The icon preference changed after the preview; no overwrite was attempted for this entry.' }
                $key.SetValue('IsPromoted', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
                if ($key.GetValue('IsPromoted', $null) -ne 1 -or $key.GetValueKind('IsPromoted') -ne [Microsoft.Win32.RegistryValueKind]::DWord) { throw 'The icon preference readback failed.' }
                $entry.status = 'Promoted'
            } finally { $key.Dispose() }
        }
    } catch {
        $report.error = $_.Exception.Message
        throw
    } finally {
        $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath ($BackupPath + '.result.json') -Encoding UTF8
    }
}
$report
