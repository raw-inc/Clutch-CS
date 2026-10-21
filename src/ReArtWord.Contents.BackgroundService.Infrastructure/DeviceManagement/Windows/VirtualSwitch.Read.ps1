function Get-DeviceVirtualSwitchInventory {
    try {
        $script:deviceSwitchObjects = @(Get-VMSwitch -ErrorAction Stop | Sort-Object Id)
        $script:deviceSwitchAdapters = @(Get-NetAdapter -ErrorAction Stop | Where-Object { $_.HardwareInterface } | Sort-Object InterfaceGuid)
        $ports = @(Get-VMNetworkAdapter -All -ErrorAction Stop | Sort-Object Id)
        $bindings = @(Get-NetAdapterBinding -AllBindings -IncludeHidden -ErrorAction Stop | Where-Object { $_.ComponentID -eq 'vms_pp' })
    } catch { throw 'Device.HyperVUnavailable' }
    if ($script:deviceSwitchObjects.Count -gt 128 -or $script:deviceSwitchAdapters.Count -gt 128 -or $ports.Count -gt 2048) { throw 'Device.ReadIncomplete' }
    $switches = @(foreach ($switch in $script:deviceSwitchObjects) {
        $descriptions = @()
        if ($switch.PSObject.Properties['NetAdapterInterfaceDescriptions']) { $descriptions = @($switch.NetAdapterInterfaceDescriptions | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) }
        if (-not $descriptions.Count -and $switch.PSObject.Properties['NetAdapterInterfaceDescription'] -and -not [string]::IsNullOrWhiteSpace($switch.NetAdapterInterfaceDescription)) { $descriptions = @([string]$switch.NetAdapterInterfaceDescription) }
        $ids = @(); $problem = $null
        foreach ($description in $descriptions) {
            $matching = @($script:deviceSwitchAdapters | Where-Object { $_.InterfaceDescription -ceq $description })
            if ($matching.Count -ne 1) { $problem = 'Device.SwitchInterfaceUnconfirmed' }
            else { $ids += ([guid]$matching[0].InterfaceGuid).ToString('D') }
        }
        if ([string]$switch.SwitchType -eq 'External' -and -not $ids.Count) { $problem = 'Device.SwitchInterfaceUnconfirmed' }
        $connections = @($ports | Where-Object { [string]$_.SwitchId -eq [string]$switch.Id } | ForEach-Object {
            $vm = $null
            if (-not $_.IsManagementOs) { $vm = [string]$_.VMName }
            [ordered]@{ id = [string]$_.Id; name = [string]$_.Name; managementOS = [bool]$_.IsManagementOs; virtualMachine = $vm }
        })
        if (@($connections | Where-Object { -not $_.managementOS }).Count) { $problem = 'Device.VirtualSwitchInUse' }
        # Windows owns the well-known Default Switch independently of its localized display name.
        if ([string]$switch.Id -eq 'c08cb7b8-9b3c-408e-8e30-5e16a3aeb444' -or [string]$switch.SwitchType -notin @('External','Internal','Private')) { $problem = 'Device.VirtualSwitchReadOnly' }
        [ordered]@{ switch = [ordered]@{ id = [string]$switch.Id; name = [string]$switch.Name; kind = [string]$switch.SwitchType; physicalInterface = ($descriptions -join ', ') }
            physicalInterfaceIds = @($ids | Sort-Object); allowManagementOS = [bool]$switch.AllowManagementOS; ports = $connections; editable = ($null -eq $problem); problem = $problem }
    })
    $interfaces = @(foreach ($adapter in $script:deviceSwitchAdapters) {
        $id = ([guid]$adapter.InterfaceGuid).ToString('D')
        $binding = @($bindings | Where-Object { $_.Name -ceq $adapter.Name })
        $ambiguous = @($script:deviceSwitchAdapters | Where-Object { $_.InterfaceDescription -ceq $adapter.InterfaceDescription }).Count -ne 1
        $used = $ambiguous -or [string]$adapter.Status -eq 'Not Present' -or $binding.Count -ne 1 -or $binding[0].Enabled -or @($switches | Where-Object { $_.physicalInterfaceIds -contains $id }).Count -gt 0
        [ordered]@{ id = $id; name = [string]$adapter.Name; description = [string]$adapter.InterfaceDescription; inUse = [bool]$used }
    })
    return [ordered]@{ switches = $switches; physicalInterfaces = $interfaces }
}

function Get-DeviceVirtualSwitchState($query) {
    $state = Get-DeviceVirtualSwitchInventory
    if ($query.resourceId -ceq 'switches') { return $state }
    $id = [guid]$query.resourceId.Substring(7)
    $found = @($state.switches | Where-Object { [guid]$_.switch.id -eq $id })
    if ($found.Count -gt 1) { throw 'Device.ReadIncomplete' }
    if (-not $found.Count) { return $null }
    return $found[0]
}
