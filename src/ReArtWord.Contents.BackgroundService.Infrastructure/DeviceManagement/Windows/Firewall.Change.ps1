function Set-DeviceState($operation) {
    if ($operation.action -eq 'firewallProfile') {
        $name = $operation.query.resourceId.Substring(8)
        if ($name -cnotin @('Domain','Private','Public')) { throw 'Device.InvalidRequest' }
        $enabled = 'False'; if ($operation.change.enabled) { $enabled = 'True' }
        $script:deviceStarted = $true
        Set-NetFirewallProfile -PolicyStore PersistentStore -Profile $name -Enabled $enabled | Out-Null
        Add-DeviceStep 'firewall.profile'
        return
    }
    $id = $operation.query.resourceId.Substring(5)
    $existing = @(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop | Where-Object { $_.Name -ceq $id })
    if ($operation.action -eq 'firewallRuleAdd') {
        if ($existing.Count -ne 0 -or $null -ne $operation.before) { throw 'Device.RuleExists' }
        $expectedId = 'Contents.HostManager.' + ([guid]$operation.operationId).ToString('N')
        if ($id -cne $expectedId) { throw 'Device.InvalidRequest' }
        $rule = $operation.change.rule
        $ports = @{}
        if ($rule.protocol -in @('TCP', 'UDP')) { $ports.LocalPort = @($rule.localPorts); $ports.RemotePort = @($rule.remotePorts) }
        $enabled = 'False'; if ($rule.enabled) { $enabled = 'True' }
        $script:deviceStarted = $true
        New-NetFirewallRule -PolicyStore PersistentStore -Name $id -DisplayName $rule.name -Direction $rule.direction -Action $rule.action -Enabled $enabled `
            -Profile ($rule.profile -split ',' | ForEach-Object { $_.Trim() }) -Protocol $rule.protocol @ports `
            -LocalAddress @($rule.localAddresses) -RemoteAddress @($rule.remoteAddresses) -Program $rule.program | Out-Null
        Add-DeviceStep 'firewall.rule.created'
        return
    }
    if ($existing.Count -ne 1) { throw 'Device.RuleUnavailable' }
    if ($null -eq $operation.before -or -not $operation.before.editable) { throw 'Device.RuleReadOnly' }
    $script:deviceStarted = $true
    if ($operation.action -eq 'firewallRuleRemove') {
        Remove-NetFirewallRule -InputObject $existing[0] -Confirm:$false | Out-Null
        Add-DeviceStep 'firewall.rule.removed'
    } elseif ($operation.action -eq 'firewallRuleEnabled') {
        $enabled = 'False'; if ($operation.change.enabled) { $enabled = 'True' }
        Set-NetFirewallRule -InputObject $existing[0] -Enabled $enabled | Out-Null
        Add-DeviceStep 'firewall.rule.enabled'
    } else { throw 'Device.InvalidRequest' }
}
