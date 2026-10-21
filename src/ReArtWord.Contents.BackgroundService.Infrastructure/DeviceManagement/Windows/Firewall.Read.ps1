function Get-DeviceFilterValues($source, [string[]]$names) {
    $result = [ordered]@{}
    foreach ($name in $names) {
        $property = $source.PSObject.Properties[$name]
        $result[$name] = $null
        if ($null -ne $property) { $result[$name] = $property.Value }
    }
    return $result
}

function Test-DeviceFirewallTemplate($details) {
    $defaults = @{
        rule = @{ Description=@(); Group=@(); EdgeTraversalPolicy=@('Block','0'); LooseSourceMapping=@('False','0'); LocalOnlyMapping=@('False','0'); Platform=@('Any'); Owner=@(); PolicyAppId=@(); RemoteDynamicKeywordAddresses=@() }
        port = @{ IcmpType=@('Any'); DynamicTarget=@('Any') }
        application = @{ Package=@('Any') }
        service = @{ Service=@('Any') }
        network = @{ InterfaceAlias=@('Any') }
        networkType = @{ InterfaceType=@('Any') }
        security = @{ Authentication=@('NotRequired'); Encryption=@('NotRequired'); OverrideBlockRules=@('False','0'); LocalUser=@('Any'); RemoteUser=@('Any'); RemoteMachine=@('Any') }
    }
    foreach($group in $defaults.Keys) {
        foreach($name in $defaults[$group].Keys) {
            foreach($value in @($details[$group][$name])) {
                if ($null -ne $value -and [string]$value -ne '' -and [string]$value -notin $defaults[$group][$name]) { return $false }
            }
        }
    }
    return $true
}

function Get-DeviceFirewallRule($rule, [switch]$IncludeTemplate) {
    $port = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop
    $address = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop
    $application = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop
    $service = Get-NetFirewallServiceFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop
    $network = Get-NetFirewallInterfaceFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop
    $networkType = Get-NetFirewallInterfaceTypeFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop
    $security = Get-NetFirewallSecurityFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop
    $protocol = [string]$port.Protocol
    switch ($protocol) { '256' { $protocol = 'Any' }; '6' { $protocol = 'TCP' }; '17' { $protocol = 'UDP' }; '1' { $protocol = 'ICMPv4' }; '58' { $protocol = 'ICMPv6' } }
    $details = [ordered]@{
        rule = (Get-DeviceFilterValues $rule @('Name','DisplayName','Description','Group','Enabled','Profile','Platform','Direction','Action','EdgeTraversalPolicy',
            'LooseSourceMapping','LocalOnlyMapping','Owner','PolicyStoreSource','PolicyStoreSourceType','PolicyAppId','RemoteDynamicKeywordAddresses'))
        port = (Get-DeviceFilterValues $port @('Protocol','LocalPort','RemotePort','IcmpType','DynamicTarget'))
        address = (Get-DeviceFilterValues $address @('LocalAddress','RemoteAddress'))
        application = (Get-DeviceFilterValues $application @('Program','Package'))
        service = (Get-DeviceFilterValues $service @('Service'))
        network = (Get-DeviceFilterValues $network @('InterfaceAlias'))
        networkType = (Get-DeviceFilterValues $networkType @('InterfaceType'))
        security = (Get-DeviceFilterValues $security @('Authentication','Encryption','OverrideBlockRules','LocalUser','RemoteUser','RemoteMachine'))
    }
    $canonical = ConvertTo-DeviceCanonical $details | ConvertTo-Json -Compress -Depth 32
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $editable = $script:localRules.Contains([string]$rule.Name) -and [string]$rule.PolicyStoreSourceType -eq 'Local'
    $result = [ordered]@{ id = ([string]$rule.Name); name = ([string]$rule.DisplayName); direction = ([string]$rule.Direction)
        action = ([string]$rule.Action); enabled = ([string]$rule.Enabled -eq 'True'); profile = ([string]$rule.Profile); protocol = $protocol
        localPorts = @($port.LocalPort | ForEach-Object { [string]$_ }); remotePorts = @($port.RemotePort | ForEach-Object { [string]$_ })
        localAddresses = @($address.LocalAddress | ForEach-Object { [string]$_ }); remoteAddresses = @($address.RemoteAddress | ForEach-Object { [string]$_ })
        program = ([string]$application.Program); editable = $editable; revision = $null; detailsHash = $digest }
    if ($IncludeTemplate) { return [ordered]@{rule=$result;cloneable=($editable -and (Test-DeviceFirewallTemplate $details))} }
    return $result
}

function Get-DeviceState($query) {
    $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop | Sort-Object Name | ForEach-Object {
        [ordered]@{ name = ([string]$_.Name); enabled = [bool]$_.Enabled; revision = $null }
    })
    if ($null -ne $query.resourceId -and $query.resourceId.StartsWith('profile/', [StringComparison]::Ordinal)) {
        $selected = @($profiles | Where-Object { $_.name -ceq $query.resourceId.Substring(8) })
        if ($selected.Count -ne 1) { throw 'Device.InvalidRequest' }
        return $selected[0]
    }
    $script:localRules = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop | ForEach-Object { [void]$script:localRules.Add([string]$_.Name) }
    $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop | Sort-Object Name)
    if ($query.resourceId -ceq 'overview') {
        if ($rules.Count -gt 2048) { throw 'Device.ReadIncomplete' }
        $summaries = @($rules | ForEach-Object { [ordered]@{id=[string]$_.Name;name=[string]$_.DisplayName;direction=[string]$_.Direction;action=[string]$_.Action;profile=[string]$_.Profile;enabled=([string]$_.Enabled -eq 'True');editable=($script:localRules.Contains([string]$_.Name) -and [string]$_.PolicyStoreSourceType -eq 'Local')} })
        return [ordered]@{profiles=$profiles;rules=$summaries}
    }
    if ($query.resourceId -ceq 'bulk') {
        $decoded=$query.filter | ConvertFrom-Json
        $ids=@($decoded)
        if ($ids.Count -lt 1 -or $ids.Count -gt 16 -or @($ids | Where-Object { $_ -isnot [string] -or $_.Length -gt 512 -or [string]::IsNullOrWhiteSpace($_) }).Count) { throw 'Device.InvalidRequest' }
        $selected=@($rules | Where-Object { $ids -ccontains [string]$_.Name })
        if ($selected.Count -ne $ids.Count) { throw 'Device.SettingsChanged' }
        return [ordered]@{rules=@($selected | ForEach-Object { Get-DeviceFirewallRule $_ -IncludeTemplate })}
    }
    if ($null -ne $query.resourceId) {
        if (-not $query.resourceId.StartsWith('rule/', [StringComparison]::Ordinal)) { throw 'Device.InvalidRequest' }
        $selected = @($rules | Where-Object { $_.Name -ceq $query.resourceId.Substring(5) })
        if ($selected.Count -gt 1) { throw 'Device.ReadIncomplete' }
        if ($selected.Count -eq 0) { return $null }
        return Get-DeviceFirewallRule $selected[0]
    }
    if (-not [string]::IsNullOrWhiteSpace($query.filter)) {
        $rules = @($rules | Where-Object { $_.DisplayName.IndexOf($query.filter, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $_.Name.IndexOf($query.filter, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
    }
    $page = @($rules | Select-Object -Skip $query.offset -First 10 | ForEach-Object { Get-DeviceFirewallRule $_ })
    $next = $null
    if ($query.offset + $page.Count -lt $rules.Count) { $next = $query.offset + $page.Count }
    return [ordered]@{ profiles = $profiles; rules = $page; nextOffset = $next }
}
