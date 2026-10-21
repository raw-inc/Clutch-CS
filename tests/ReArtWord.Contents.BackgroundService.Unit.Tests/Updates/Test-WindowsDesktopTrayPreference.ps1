#Requires -Version 5.1
param([Parameter(Mandatory)][string] $PreferenceScriptPath, [string] $CoreAssemblyPath)
$ErrorActionPreference = 'Stop'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('contents-tray-preference-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$exe = Join-Path $fixture 'Clutch-CS.CoreService.exe'
[IO.File]::WriteAllText($exe, 'fixture; never executed')
$names = @()
$root = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Control Panel\NotifyIconSettings')
function Add-Fixture($Path, $Guid, $Value, $Kind = [Microsoft.Win32.RegistryValueKind]::DWord) {
    $name = 'Contents.Preference.Test.' + [guid]::NewGuid().ToString('N')
    $script:names += $name
    $key = $root.CreateSubKey($name)
    try {
        $key.SetValue('ExecutablePath', $Path)
        if ($Guid) { $key.SetValue('IconGuid', $Guid) }
        if ($null -ne $Value) { $key.SetValue('IsPromoted', $Value, $Kind) }
    } finally { $key.Dispose() }
    return $name
}
function Read-Value($Name) {
    $key = $root.OpenSubKey($Name)
    try { return $key.GetValue('IsPromoted', $null) } finally { $key.Dispose() }
}
try {
    $missing = & $PreferenceScriptPath -CoreExecutablePath $exe -UserSid $sid
    if ($missing.entries.Count -ne 1 -or $missing.entries[0].status -ne 'NotRegistered') { throw 'Missing registrations must not be manufactured.' }
    $guid = $missing.entries[0].iconGuid
    if ($CoreAssemblyPath) {
        $assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($CoreAssemblyPath))
        $type = $assembly.GetType('ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray.DesktopTrayIdentity', $true)
        $method = $type.GetMethod('Create', [Reflection.BindingFlags]'NonPublic,Static')
        $actual = $method.Invoke($null, [object[]]@([string]$exe, 'ReArtWord.Contents.RemoteDesktop.Connection.v1'))
        if ([guid]$guid -ne $actual) { throw 'Deployment identity differs from the actual Core implementation.' }
    }
    $zero = Add-Fixture $exe $guid 0
    $absent = Add-Fixture $exe $guid $null
    $already = Add-Fixture $exe $guid 1
    $wrongPath = Add-Fixture ($exe + '.other') $guid 0
    $wrongGuid = Add-Fixture $exe ([guid]::NewGuid().ToString()) 0
    $legacy = Add-Fixture $exe $null 0
    $unsupported = Add-Fixture $exe $guid '0' ([Microsoft.Win32.RegistryValueKind]::String)
    $preview = & $PreferenceScriptPath -CoreExecutablePath $exe -UserSid $sid
    if (@($preview.entries | Where-Object status -eq WouldPromote).Count -ne 2 -or (Read-Value $zero) -ne 0 -or $null -ne (Read-Value $absent)) { throw 'Preview mutated data or matched the wrong records.' }
    $backup = Join-Path $fixture 'before.json'
    $result = & $PreferenceScriptPath -CoreExecutablePath $exe -UserSid $sid -Apply -BackupPath $backup
    if (@($result.entries | Where-Object status -eq Promoted).Count -ne 2 -or (Read-Value $zero) -ne 1 -or (Read-Value $absent) -ne 1 -or (Read-Value $already) -ne 1) { throw 'Expected DWORD preferences were not applied.' }
    foreach ($name in @($wrongPath, $wrongGuid, $legacy)) { if ((Read-Value $name) -ne 0) { throw 'An unrelated or legacy icon was changed.' } }
    if ((Read-Value $unsupported) -cne '0') { throw 'An unexpected registry value type was changed.' }
    $prior = Get-Content -LiteralPath $backup -Raw | ConvertFrom-Json
    if (@($prior.entries | Where-Object { $_.previousValueExists -eq $false }).Count -ne 1 -or -not [IO.File]::Exists($backup + '.result.json')) { throw 'Backup or result journal is incomplete.' }
    $duplicateRejected = $false
    try { $null = & $PreferenceScriptPath -CoreExecutablePath $exe -UserSid $sid -Apply -BackupPath $backup } catch { $duplicateRejected = $true }
    if (-not $duplicateRejected) { throw 'An existing backup was overwritten.' }
    $again = & $PreferenceScriptPath -CoreExecutablePath $exe -UserSid $sid -Apply -BackupPath (Join-Path $fixture 'again.json')
    if (@($again.entries | Where-Object status -eq AlreadyPromoted).Count -ne 3 -or @($again.entries | Where-Object status -eq Promoted).Count) { throw 'Repeated deployment is not idempotent.' }
    $serviceRejected = $false
    try { $null = & $PreferenceScriptPath -CoreExecutablePath $exe -UserSid 'S-1-5-18' } catch { $serviceRejected = $true }
    if (-not $serviceRejected) { throw 'SYSTEM was accepted as the tray user.' }
    'Passed: preview, exact path/GUID scope, missing/legacy registration, DWORD/missing preference, backup, duplicate audit rejection, idempotence, SYSTEM rejection.'
} finally {
    foreach ($name in $names) { $root.DeleteSubKey($name, $false) }
    $root.Dispose()
    # All fixture files are direct children created above; no recursive or computed-root deletion.
    foreach ($name in @('before.json', 'before.json.result.json', 'again.json', 'again.json.result.json', 'Clutch-CS.CoreService.exe')) { [IO.File]::Delete((Join-Path $fixture $name)) }
    [IO.Directory]::Delete($fixture)
}
