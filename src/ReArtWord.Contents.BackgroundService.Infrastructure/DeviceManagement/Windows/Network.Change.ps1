function Get-DeviceExactAdapter([string]$id) {
    $found = @(Get-NetAdapter -ErrorAction Stop | Where-Object { [guid]$_.InterfaceGuid -eq [guid]$id })
    if ($found.Count -ne 1) { throw 'Device.InterfaceUnavailable' }
    return $found[0]
}

function Clear-DeviceDnsServers([int]$interfaceIndex, [int]$family) {
    & "$env:SystemRoot/System32/netsh.exe" interface ("ipv" + $family) set dnsservers ("name=" + $interfaceIndex) source=static address=none validate=no | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Device.NativeCommandFailed' }
}

function Set-DeviceNetwork($operation) {
    $adapter = Get-DeviceExactAdapter $operation.query.resourceId
    # Sparse browser patches omit unchanged properties. Keep StrictMode while supplying only null defaults.
    $change = [ordered]@{ family = $null; enabled = $null; dhcp = $null; routerDiscovery = $null; automaticMetric = $null; metric = $null; addresses = $null; gateways = $null; automaticDns = $null; dns = $null }
    foreach ($property in $operation.change.PSObject.Properties) { $change[$property.Name] = $property.Value }
    $family = 'IPv4'; $component = 'ms_tcpip'; $prefix = '0.0.0.0/0'; $numericFamily = 2
    if ($change.family -eq 6) { $family = 'IPv6'; $component = 'ms_tcpip6'; $prefix = '::/0'; $numericFamily = 23 }
    $binding = @(Get-NetAdapterBinding -AllBindings -IncludeHidden | Where-Object { $_.Name -ceq $adapter.Name -and $_.ComponentID -eq $component })
    if ($binding.Count -ne 1) { throw 'Device.InterfaceUnavailable' }
    if ($change.enabled -eq $true -and -not $binding[0].Enabled) {
        $script:deviceStarted = $true
        Enable-NetAdapterBinding -InputObject $binding[0] -Confirm:$false | Out-Null
        Add-DeviceStep 'network.protocol.enabled'
    }
    if ($null -ne $change.dhcp) {
        $mode = 'Disabled'; if ($change.dhcp) { $mode = 'Enabled' }
        $script:deviceStarted = $true
        Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -AddressFamily $family -Dhcp $mode -Confirm:$false | Out-Null
        Add-DeviceStep 'network.addressing'
    }
    if ($null -ne $change.routerDiscovery) {
        $mode = 'Disabled'; if ($change.routerDiscovery) { $mode = 'Enabled' }
        $script:deviceStarted = $true
        Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -AddressFamily $family -RouterDiscovery $mode -Confirm:$false | Out-Null
        Add-DeviceStep 'network.routerDiscovery'
    }
    if ($null -ne $change.automaticMetric) {
        $mode = 'Disabled'; if ($change.automaticMetric) { $mode = 'Enabled' }
        $arguments = @{ InterfaceIndex = $adapter.ifIndex; AddressFamily = $family; AutomaticMetric = $mode; Confirm = $false }
        if ($null -ne $change.metric) { $arguments.InterfaceMetric = [uint32]$change.metric }
        $script:deviceStarted = $true
        Set-NetIPInterface @arguments | Out-Null
        Add-DeviceStep 'network.metric'
    }
    if ($null -ne $change.addresses) {
        $existing = @(Get-NetIPAddress | Where-Object { $_.InterfaceIndex -eq $adapter.ifIndex -and [string]$_.AddressFamily -eq $family -and [string]$_.PrefixOrigin -eq 'Manual' })
        foreach ($wanted in $change.addresses) {
            $found = @($existing | Where-Object { [Net.IPAddress]$_.IPAddress -eq [Net.IPAddress]$wanted.address })
            $script:deviceStarted = $true
            if ($found.Count -eq 0) {
                New-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily $family -IPAddress $wanted.address -PrefixLength $wanted.prefixLength -Confirm:$false | Out-Null
            } elseif ($found.Count -eq 1) {
                if ($found[0].PrefixLength -ne $wanted.prefixLength) { Set-NetIPAddress -InputObject $found[0] -PrefixLength $wanted.prefixLength -Confirm:$false | Out-Null }
            } else { throw 'Device.ReadIncomplete' }
            Add-DeviceStep 'network.address.saved'
        }
        foreach ($old in $existing) {
            $keep = @($change.addresses | Where-Object { [Net.IPAddress]$_.address -eq [Net.IPAddress]$old.IPAddress })
            if ($keep.Count -eq 0) {
                $script:deviceStarted = $true
                Remove-NetIPAddress -InputObject $old -Confirm:$false | Out-Null
                Add-DeviceStep 'network.address.removed'
            }
        }
    }
    if ($null -ne $change.gateways) {
        $existing = @(Get-NetRoute | Where-Object { $_.InterfaceIndex -eq $adapter.ifIndex -and [string]$_.AddressFamily -eq $family -and $_.DestinationPrefix -eq $prefix })
        foreach ($wanted in $change.gateways) {
            $found = @($existing | Where-Object { [Net.IPAddress]$_.NextHop -eq [Net.IPAddress]$wanted })
            if ($found.Count -eq 0) {
                $script:deviceStarted = $true
                New-NetRoute -InterfaceIndex $adapter.ifIndex -DestinationPrefix $prefix -NextHop $wanted -RouteMetric 0 -Confirm:$false | Out-Null
                Add-DeviceStep 'network.gateway.saved'
            }
        }
        foreach ($old in $existing) {
            $keep = @($change.gateways | Where-Object { [Net.IPAddress]$_ -eq [Net.IPAddress]$old.NextHop })
            if ($keep.Count -eq 0) {
                $script:deviceStarted = $true
                Remove-NetRoute -InputObject $old -Confirm:$false | Out-Null
                Add-DeviceStep 'network.gateway.removed'
            }
        }
    }
    if ($null -ne $change.automaticDns) {
        $dns = @(Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex | Where-Object { [int]$_.AddressFamily -eq $numericFamily })
        if ($dns.Count -ne 1) { throw 'Device.ReadIncomplete' }
        $script:deviceStarted = $true
        if ($change.automaticDns) { Set-DnsClientServerAddress -InputObject $dns[0] -ResetServerAddresses | Out-Null }
        elseif (@($change.dns).Count -eq 0) { Clear-DeviceDnsServers $adapter.ifIndex $change.family }
        else { Set-DnsClientServerAddress -InputObject $dns[0] -ServerAddresses @($change.dns) | Out-Null }
        Add-DeviceStep 'network.dns'
    }
    if ($change.enabled -eq $false -and $binding[0].Enabled) {
        $script:deviceStarted = $true
        Disable-NetAdapterBinding -InputObject $binding[0] -Confirm:$false | Out-Null
        Add-DeviceStep 'network.protocol.disabled'
    }
}

function Set-DeviceVirtualNic($operation) {
    if ($null -ne $script:hyperVProblem) { throw 'Device.HyperVUnavailable' }
    $change = $operation.change
    if ($operation.action -eq 'virtualNicCreate') {
        $availableSwitches = @(Get-VMSwitch)
        $switch = @($availableSwitches | Where-Object { [string]$_.Id -eq $change.switchId })
        if ($switch.Count -ne 1) { throw 'Device.VirtualSwitchUnavailable' }
        if (@($availableSwitches | Where-Object { $_.Name -eq $switch[0].Name }).Count -ne 1) { throw 'Device.VirtualSwitchUnavailable' }
        $duplicate = @(Get-VMNetworkAdapter -ManagementOS | Where-Object { $_.Name -eq $change.name })
        if ($duplicate.Count -ne 0) { throw 'Device.VirtualNicExists' }
        $script:deviceStarted = $true
        $nic = Add-VMNetworkAdapter -ManagementOS -Name $change.name -SwitchName $switch[0].Name -Passthru
        Add-DeviceStep 'network.vnic.created'
    } else {
        $id = $operation.query.resourceId.Substring(5)
        $found = @(Get-VMNetworkAdapter -ManagementOS | Where-Object { [string]$_.Id -ceq $id })
        if ($found.Count -ne 1) { throw 'Device.VirtualNicUnavailable' }
        $nic = $found[0]
        if ($operation.action -eq 'virtualNicDelete') {
            $script:deviceStarted = $true
            Remove-VMNetworkAdapter -VMNetworkAdapter $nic -Confirm:$false
            Add-DeviceStep 'network.vnic.removed'
            return
        }
        $vlan = Get-VMNetworkAdapterVlan -VMNetworkAdapter $nic
        if ([string]$vlan.OperationMode -notin @('Access', 'Untagged')) { throw 'Device.VlanModeUnsupported' }
    }
    $script:deviceStarted = $true
    if ($null -eq $change.vlanId) { Set-VMNetworkAdapterVlan -VMNetworkAdapter $nic -Untagged }
    else { Set-VMNetworkAdapterVlan -VMNetworkAdapter $nic -Access -VlanId $change.vlanId }
    Add-DeviceStep 'network.vnic.vlan'
}

function Set-DeviceState($operation) {
    if ($operation.action -eq 'network') { Set-DeviceNetwork $operation }
    elseif ($operation.action -eq 'networkProtocols') { Set-DeviceNetworkProtocols $operation }
    elseif ($operation.action -in @('virtualSwitchCreate', 'virtualSwitchDelete')) { Set-DeviceVirtualSwitch $operation }
    else { Set-DeviceVirtualNic $operation }
}

function Set-DeviceNetworkProtocols($operation) {
    $adapter = Get-DeviceExactAdapter $operation.query.resourceId
    $bindings = @(Get-NetAdapterBinding -AllBindings -IncludeHidden | Where-Object { $_.Name -ceq $adapter.Name })
    $changes = @()
    foreach ($family in @(4,6)) {
        $property = $operation.change.PSObject.Properties[("ipv" + $family + 'Enabled')]
        if ($null -eq $property -or $null -eq $property.Value) { continue }
        $component = 'ms_tcpip'; if ($family -eq 6) { $component = 'ms_tcpip6' }
        $binding = @($bindings | Where-Object { $_.ComponentID -eq $component })
        if ($binding.Count -ne 1) { throw 'Device.InterfaceUnavailable' }
        $changes += [pscustomobject]@{ binding = $binding[0]; enabled = [bool]$property.Value; family = $family }
    }
    # Resolve both bindings before starting; enable first so switching families does not unnecessarily remove both.
    foreach ($change in @($changes | Sort-Object -Property @{Expression='enabled';Descending=$true},family)) {
        if ([bool]$change.binding.Enabled -eq $change.enabled) { continue }
        $script:deviceStarted = $true
        if ($change.enabled) { Enable-NetAdapterBinding -InputObject $change.binding -Confirm:$false | Out-Null }
        else { Disable-NetAdapterBinding -InputObject $change.binding -Confirm:$false | Out-Null }
        Add-DeviceStep ("network.ipv" + $change.family + '.binding')
    }
}
