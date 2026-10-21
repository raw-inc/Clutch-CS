function Initialize-DeviceNetwork {
    # Hyper-V module loading and network provider queries are independent. Both finish before publishing the snapshot.
    $hyperV = [powershell]::Create()
    try {
        [void]$hyperV.AddScript(${function:Get-DeviceHyperVNetwork}.ToString())
        $pending = $hyperV.BeginInvoke()
        $script:adapters = @(Get-NetAdapter -ErrorAction Stop | Sort-Object InterfaceGuid)
        $script:ipInterfaces = @(Get-CimInstance -Namespace root/StandardCimv2 -ClassName MSFT_NetIPInterface -ErrorAction Stop)
        $script:ipAddresses = @(Get-CimInstance -Namespace root/StandardCimv2 -ClassName MSFT_NetIPAddress -ErrorAction Stop)
        $script:dnsAddresses = @(Get-CimInstance -Namespace root/StandardCimv2 -ClassName MSFT_DNSClientServerAddress -ErrorAction Stop)
        $script:routes = @(Get-CimInstance -Namespace root/StandardCimv2 -ClassName MSFT_NetRoute -Filter "DestinationPrefix='0.0.0.0/0' OR DestinationPrefix='::/0'" -ErrorAction Stop)
        $script:bindings = @(Get-NetAdapterBinding -ComponentID @('ms_tcpip','ms_tcpip6') -IncludeHidden -ErrorAction Stop)
        if ($script:adapters.Count -gt 128) { throw 'Device.ReadIncomplete' }
        $result = @($hyperV.EndInvoke($pending))
        if ($result.Count -ne 1) { throw 'Device.ReadIncomplete' }
        $script:switches = @($result[0].switches)
        $script:virtualNics = @($result[0].virtualNics)
        $script:hyperVProblem = $result[0].problem
    } finally { $hyperV.Dispose() }
}

function Get-DeviceHyperVNetwork {
    Set-StrictMode -Version 3.0
    $ErrorActionPreference = 'Stop'
    try {
        $switches = @(Get-VMSwitch -ErrorAction Stop | Sort-Object Id | ForEach-Object {
            $physical = $null
            if ($_.PSObject.Properties['NetAdapterInterfaceDescriptions']) { $physical = @($_.NetAdapterInterfaceDescriptions) -join ', ' }
            elseif ($_.PSObject.Properties['NetAdapterInterfaceDescription']) { $physical = [string]$_.NetAdapterInterfaceDescription }
            [ordered]@{ id = ([string]$_.Id); name = ([string]$_.Name); kind = ([string]$_.SwitchType); physicalInterface = $physical }
        })
        $virtualNics = @(Get-VMNetworkAdapter -ManagementOS -ErrorAction Stop | Sort-Object Id | ForEach-Object {
            $nic = $_
            $vlan = Get-VMNetworkAdapterVlan -VMNetworkAdapter $nic -ErrorAction Stop
            $vlanId = $null
            if ([string]$vlan.OperationMode -eq 'Access') { $vlanId = [int]$vlan.AccessVlanId }
            [ordered]@{ id = ([string]$nic.Id); name = ([string]$nic.Name); switchId = ([string]$nic.SwitchId); switchName = ([string]$nic.SwitchName)
                macAddress = ([string]$nic.MacAddress); vlanMode = ([string]$vlan.OperationMode); vlanId = $vlanId; revision = $null }
        })
        return [pscustomobject]@{switches=$switches;virtualNics=$virtualNics;problem=$null}
    } catch {
        return [pscustomobject]@{switches=@();virtualNics=@();problem='Device.HyperVUnavailable'}
    }
}

function Get-DeviceIpConfiguration($adapter, [int]$family) {
    $protocol = 'ms_tcpip'
    $addressFamily = 'IPv4'
    $numericFamily = 2
    $service = 'Tcpip'
    $prefix = '0.0.0.0/0'
    if ($family -eq 6) { $protocol = 'ms_tcpip6'; $addressFamily = 'IPv6'; $numericFamily = 23; $service = 'Tcpip6'; $prefix = '::/0' }
    $binding = @($script:bindings | Where-Object { $_.Name -ceq $adapter.Name -and $_.ComponentID -eq $protocol })
    if ($binding.Count -ne 1) { return $null }
    $ip = @($script:ipInterfaces | Where-Object { $_.InterfaceIndex -eq $adapter.ifIndex -and [int]$_.AddressFamily -eq $numericFamily })
    $dhcp = $null
    $automaticMetric = $null
    $metric = $null
    $routerDiscovery = $null
    if ($ip.Count -eq 1) {
        $dhcp = ([int]$ip[0].Dhcp -eq 1)
        $automaticMetric = ([int]$ip[0].AutomaticMetric -eq 1)
        $metric = [int]$ip[0].InterfaceMetric
        if ($family -eq 6) { $routerDiscovery = ([int]$ip[0].RouterDiscovery -ne 0) }
    }
    $automaticDns = $null
    try {
        $guid = '{' + ([guid]$adapter.InterfaceGuid).ToString('D') + '}'
        $key = Get-ItemProperty -LiteralPath ("HKLM:\SYSTEM\CurrentControlSet\Services\$service\Parameters\Interfaces\$guid") -ErrorAction Stop
        $configuredDns = $key.PSObject.Properties['NameServer']
        $automaticDns = $null -eq $configuredDns -or [string]::IsNullOrWhiteSpace([string]$configuredDns.Value)
    } catch { $automaticDns = $null }
    $addresses = @($script:ipAddresses | Where-Object { $_.InterfaceIndex -eq $adapter.ifIndex -and [int]$_.AddressFamily -eq $numericFamily } |
        Sort-Object IPAddress, PrefixLength | ForEach-Object {
            [ordered]@{ address = ([string]$_.IPAddress -split '%')[0]; prefixLength = [int]$_.PrefixLength; automatic = ([int]$_.PrefixOrigin -ne 1) }
        })
    $gateways = @($script:routes | Where-Object { $_.InterfaceIndex -eq $adapter.ifIndex -and $_.DestinationPrefix -eq $prefix } |
        ForEach-Object { [string]$_.NextHop } | Sort-Object -Unique)
    $dns = @($script:dnsAddresses | Where-Object { $_.InterfaceIndex -eq $adapter.ifIndex -and [int]$_.AddressFamily -eq $numericFamily } |
        ForEach-Object { $_.ServerAddresses } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [string]$_ })
    return [ordered]@{ enabled = [bool]$binding[0].Enabled; dhcp = $dhcp; automaticMetric = $automaticMetric; metric = $metric
        automaticDns = $automaticDns; addresses = $addresses; gateways = $gateways; dns = $dns; routerDiscovery = $routerDiscovery }
}

function Get-DeviceInterface($adapter) {
    $mac = ([string]$adapter.MacAddress).Replace('-', '').Replace(':', '')
    $matching = @($script:virtualNics | Where-Object { $_.macAddress -eq $mac -and -not [string]::IsNullOrEmpty($mac) })
    $virtualId = $null; $switchId = $null; $vlanId = $null; $vlanMode = $null
    $hyperV = ([string]$adapter.PnPDeviceID).StartsWith('ROOT\VMS_MP\', [StringComparison]::OrdinalIgnoreCase)
    if ($matching.Count -eq 1) {
        $virtualId = $matching[0].id; $switchId = $matching[0].switchId; $vlanId = $matching[0].vlanId
        $vlanMode = $matching[0].vlanMode; $hyperV = $true
    }
    return [ordered]@{ id = ([guid]$adapter.InterfaceGuid).ToString('D'); name = ([string]$adapter.Name)
        description = ([string]$adapter.InterfaceDescription); status = ([string]$adapter.Status); macAddress = ([string]$adapter.MacAddress)
        physical = [bool]$adapter.HardwareInterface; ipv4 = (Get-DeviceIpConfiguration $adapter 4); ipv6 = (Get-DeviceIpConfiguration $adapter 6)
        virtualNicId = $virtualId; switchId = $switchId; vlanId = $vlanId; revision = $null; hyperV = $hyperV; vlanMode = $vlanMode }
}

function Get-DeviceState($query) {
    if ($null -ne $query.resourceId -and ($query.resourceId -ceq 'switches' -or $query.resourceId.StartsWith('switch/', [StringComparison]::Ordinal))) {
        return Get-DeviceVirtualSwitchState $query
    }
    Initialize-DeviceNetwork
    if ($null -ne $query.resourceId) {
        if ($query.resourceId.StartsWith('vnic/', [StringComparison]::Ordinal)) {
            if ($null -ne $script:hyperVProblem) { throw 'Device.HyperVUnavailable' }
            $id = $query.resourceId.Substring(5)
            $found = @($script:virtualNics | Where-Object { $_.id -ceq $id })
            if ($found.Count -gt 1) { throw 'Device.ReadIncomplete' }
            if ($found.Count -eq 0) { return $null }
            return $found[0]
        }
        $found = @($script:adapters | Where-Object { [guid]$_.InterfaceGuid -eq [guid]$query.resourceId })
        if ($found.Count -ne 1) { throw 'Device.InterfaceUnavailable' }
        return Get-DeviceInterface $found[0]
    }
    return [ordered]@{ interfaces = @($script:adapters | ForEach-Object { Get-DeviceInterface $_ })
        switches = @($script:switches); virtualNics = @($script:virtualNics); hyperVProblem = $script:hyperVProblem }
}
