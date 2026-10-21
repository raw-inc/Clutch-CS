# Only the assembly-owned code is executed. Request fields remain data throughout.
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:deviceStarted = $false
$script:deviceSteps = [System.Collections.Generic.List[string]]::new()

function Add-DeviceStep([string]$name) {
    $script:deviceSteps.Add($name)
}

function ConvertTo-DeviceCanonical($value) {
    if ($null -eq $value -or $value -is [string] -or $value -is [ValueType]) { return $value }
    if ($value -is [System.Collections.IDictionary] -or $value -is [pscustomobject]) {
        $keys = @($value.PSObject.Properties.Name)
        if ($value -is [System.Collections.IDictionary]) { $keys = @($value.Keys) }
        [string[]]$orderedKeys = @($keys | Where-Object { $_ -cne 'revision' })
        [Array]::Sort($orderedKeys, [StringComparer]::Ordinal)
        $result = [ordered]@{}
        foreach ($key in $orderedKeys) { $result[$key] = ConvertTo-DeviceCanonical $value.$key }
        return $result
    }
    $items = @($value | ForEach-Object { ConvertTo-DeviceCanonical $_ })
    return ,$items
}

function Test-DeviceValue($first, $second) {
    $left = ConvertTo-DeviceCanonical $first | ConvertTo-Json -Compress -Depth 32
    $right = ConvertTo-DeviceCanonical $second | ConvertTo-Json -Compress -Depth 32
    return [string]::Equals($left, $right, [StringComparison]::Ordinal)
}

function Get-DeviceError($errorRecord) {
    $known = @('Device.SettingsChanged', 'Device.InterfaceUnavailable', 'Device.VirtualNicUnavailable',
        'Device.VirtualSwitchUnavailable', 'Device.VirtualNicExists', 'Device.VlanModeUnsupported',
        'Device.HyperVUnavailable', 'Device.RuleUnavailable', 'Device.RuleReadOnly', 'Device.RuleExists',
        'Device.VirtualSwitchExists', 'Device.VirtualSwitchReadOnly', 'Device.VirtualSwitchInUse',
        'Device.PhysicalInterfaceInUse', 'Device.SwitchInterfaceUnconfirmed',
        'Device.PermissionDenied', 'Device.InvalidRequest', 'Device.ReadIncomplete')
    $message = $errorRecord.Exception.Message
    if ($known -ccontains $message) { return $message }
    return 'Device.NativeCommandFailed'
}

function Assert-DeviceAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Device.PermissionDenied' }
    } finally { $identity.Dispose() }
}
