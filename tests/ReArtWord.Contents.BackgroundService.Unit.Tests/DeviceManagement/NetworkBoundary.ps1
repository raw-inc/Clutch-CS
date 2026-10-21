# No networking module can load in this process; every writable boundary below is an in-memory function.
$PSModuleAutoLoadingPreference = 'None'
$script:calls = [System.Collections.Generic.List[object]]::new()
function Get-NetAdapter {
    [pscustomobject]@{ InterfaceGuid = [guid]$request.id; ifIndex = 42; Name = $request.name }
    [pscustomobject]@{ InterfaceGuid = [guid]'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'; ifIndex = 99; Name = 'other' }
}
function Get-NetAdapterBinding {
    param([switch]$AllBindings, [switch]$IncludeHidden)
    $v4 = $true; if ($request.PSObject.Properties['initialV4']) { $v4 = $request.initialV4 }
    [pscustomobject]@{ Name = $request.name; ComponentID = 'ms_tcpip'; Enabled = $v4 }
    if (-not $request.PSObject.Properties['missingV6'] -or -not $request.missingV6) { [pscustomobject]@{ Name = $request.name; ComponentID = 'ms_tcpip6'; Enabled = $true } }
}
function Get-DnsClientServerAddress {
    param($InterfaceIndex)
    if ($InterfaceIndex -ne 42) { throw 'Unexpected interface' }
    [pscustomobject]@{ AddressFamily = 2; InterfaceIndex = 42 }
    [pscustomobject]@{ AddressFamily = 23; InterfaceIndex = 42 }
}
function Set-DnsClientServerAddress {
    param($InputObject, $ServerAddresses, [switch]$ResetServerAddresses)
    $script:calls.Add(@{ kind = 'dns'; index = $InputObject.InterfaceIndex; family = $InputObject.AddressFamily; servers = @($ServerAddresses); reset = [bool]$ResetServerAddresses })
    if ($request.fail) { throw 'Synthetic OS failure after starting the DNS operation' }
}
function Clear-DeviceDnsServers {
    param([int]$interfaceIndex,[int]$family)
    $script:calls.Add(@{kind='dns-none';index=$interfaceIndex;family=$family})
    if ($request.fail) { throw 'Synthetic DNS clear failure' }
}
function Enable-NetAdapterBinding {
    param($InputObject,$Confirm)
    $script:calls.Add(@{kind='binding';component=$InputObject.ComponentID;enabled=$true})
}
function Disable-NetAdapterBinding {
    param($InputObject,$Confirm)
    $script:calls.Add(@{kind='binding';component=$InputObject.ComponentID;enabled=$false})
    if ($request.fail) { throw 'Synthetic binding failure' }
}
function Set-NetIPInterface {
    param($InterfaceIndex, $AddressFamily, $Dhcp, $AutomaticMetric, $InterfaceMetric, $RouterDiscovery, $Confirm)
    $script:calls.Add(@{ kind = 'interface'; index = $InterfaceIndex; family = $AddressFamily; dhcp = $Dhcp; metric = $InterfaceMetric; routerDiscovery = $RouterDiscovery })
}
try {
    if ($request.PSObject.Properties['action'] -and $request.action -eq 'networkProtocols') {
        Set-DeviceNetworkProtocols ([pscustomobject]@{ query = [pscustomobject]@{ resourceId = $request.id }; change = $request.change })
    } else { Set-DeviceNetwork ([pscustomobject]@{ query = [pscustomobject]@{ resourceId = $request.id }; change = $request.change }) }
    $success = $true
} catch { $success = $false }
[ordered]@{ success = $success; started = $script:deviceStarted; calls = @($script:calls.ToArray()); steps = @($script:deviceSteps.ToArray()) } | ConvertTo-Json -Depth 32 -Compress
