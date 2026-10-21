function Set-DeviceVirtualSwitch($operation) {
    # Recheck connections and physical adapter identity immediately before the OS command.
    $current = Get-DeviceVirtualSwitchState $operation.query
    if (-not (Test-DeviceValue $current $operation.before)) { throw 'Device.SettingsChanged' }
    if ($operation.action -eq 'virtualSwitchDelete') {
        if ($null -eq $current) { throw 'Device.VirtualSwitchUnavailable' }
        if (-not $current.editable) { throw $current.problem }
        $found = @($script:deviceSwitchObjects | Where-Object { [guid]$_.Id -eq [guid]$current.switch.id })
        if ($found.Count -ne 1) { throw 'Device.VirtualSwitchUnavailable' }
        $script:deviceStarted = $true
        # Hyper-V uses an additional prompt even with Confirm disabled. The verified connection set above excludes guests.
        Remove-VMSwitch -VMSwitch $found[0] -Force -Confirm:$false
        Add-DeviceStep 'network.switch.removed'
        return
    }
    if ($operation.action -ne 'virtualSwitchCreate') { throw 'Device.InvalidRequest' }
    $change = $operation.change
    if (@($current.switches | Where-Object { $_.switch.name -eq $change.name }).Count) { throw 'Device.VirtualSwitchExists' }
    if ($change.kind -eq 'External') {
        $selected = @($current.physicalInterfaces | Where-Object { [guid]$_.id -eq [guid]$change.physicalInterfaceId })
        if ($selected.Count -ne 1 -or $selected[0].inUse) { throw 'Device.PhysicalInterfaceInUse' }
        $adapter = @($script:deviceSwitchAdapters | Where-Object { [guid]$_.InterfaceGuid -eq [guid]$change.physicalInterfaceId })
        if ($adapter.Count -ne 1 -or @($script:deviceSwitchAdapters | Where-Object { $_.InterfaceDescription -ceq $adapter[0].InterfaceDescription }).Count -ne 1) { throw 'Device.SwitchInterfaceUnconfirmed' }
        $script:deviceStarted = $true
        New-VMSwitch -Name $change.name -NetAdapterInterfaceDescription $adapter[0].InterfaceDescription -AllowManagementOS $change.allowManagementOS -Confirm:$false | Out-Null
    } elseif ($change.kind -in @('Internal','Private')) {
        $script:deviceStarted = $true
        New-VMSwitch -Name $change.name -SwitchType $change.kind -Confirm:$false | Out-Null
    } else { throw 'Device.InvalidRequest' }
    Add-DeviceStep 'network.switch.created'
}
