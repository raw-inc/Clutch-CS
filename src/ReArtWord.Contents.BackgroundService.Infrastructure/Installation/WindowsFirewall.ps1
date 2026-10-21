param([Parameter(Mandatory)][string]$SpecificationPath)

$ErrorActionPreference = 'Stop'
try {
    $specification = Get-Content -LiteralPath $SpecificationPath -Raw | ConvertFrom-Json
    if ($specification.Name -notmatch '^Clutch-CS-[0-9a-f]{32}-(Network|SetupResult)$') {
        throw 'Invalid firewall rule ownership.'
    }
    $names = @('-In', '-Out', '-Core-TCP-In', '-Core-TCP-Out', '-Core-UDP-In', '-Core-UDP-Out') | ForEach-Object { $specification.Name + $_ }
    $owned = @(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop | Where-Object { $_.Name -in $names })
    if ($specification.Remove) {
        foreach ($rule in $owned) { $rule | Remove-NetFirewallRule -ErrorAction Stop }
        $remaining = @(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop | Where-Object { $_.Name -in $names })
        if ($remaining.Count -ne 0) { throw 'Windows Firewall rules could not be removed.' }
        exit 0
    }
    $adapters = @(Get-NetAdapter -IncludeHidden | Where-Object { [guid]$_.InterfaceGuid -eq [guid]$specification.InterfaceId })
    if ($adapters.Count -ne 1) { throw 'The selected network adapter is no longer available.' }
    $adapter = $adapters[0]
    $addresses = @(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 | Where-Object { $_.IPAddress -eq $specification.BindAddress })
    if ($addresses.Count -ne 1) { throw 'The selected network address is no longer assigned to this adapter.' }
    $ports = @($specification.Ports | ForEach-Object {
        $port = [int]$_
        if ($port -lt 1 -or $port -gt 65535) { throw 'Invalid network port.' }
        [string]$port
    } | Sort-Object -Unique)
    if ($ports.Count -eq 0) { throw 'No network ports were specified.' }
    $interfaceAlias = [System.Management.Automation.WildcardPattern]::Escape($adapter.Name)
    $scopes = @([pscustomobject]@{ Suffix = ''; Protocol = 'UDP'; Program = 'Any'; Ports = $ports })
    if ($specification.IncludeApplication) {
        if (-not [IO.Path]::IsPathRooted($specification.CorePath) -or -not (Test-Path -LiteralPath $specification.CorePath -PathType Leaf)) {
            throw 'The installed application is not available for firewall registration.'
        }
        foreach ($protocol in @('TCP', 'UDP')) {
            $scopes += [pscustomobject]@{ Suffix = '-Core-' + $protocol; Protocol = $protocol; Program = $specification.CorePath; Ports = @('Any') }
        }
    }
    foreach ($scope in $scopes) {
      foreach ($direction in @('Inbound', 'Outbound')) {
        $suffix = '-In'
        if ($direction -eq 'Outbound') { $suffix = '-Out' }
        $name = $specification.Name + $scope.Suffix + $suffix
        $parameters = @{
            Name = $name; PolicyStore = 'PersistentStore'; Enabled = 'True'; Action = 'Allow'
            Direction = $direction; Profile = 'Any'; Protocol = $scope.Protocol; LocalPort = $scope.Ports; Program = $scope.Program
            InterfaceAlias = $interfaceAlias; RemoteAddress = 'LocalSubnet'; EdgeTraversalPolicy = 'Block'
        }
        if ($owned.Name -contains $name) {
            Set-NetFirewallRule @parameters | Out-Null
        } else {
            New-NetFirewallRule @parameters -DisplayName ('Clutch-CS - ' + $direction + ' - ' + $specification.Name) -Group 'Clutch-CS' | Out-Null
        }
        $active = @(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { $_.Name -eq $name })
        if ($active.Count -ne 1 -or $active[0].Enabled -ne 'True' -or $active[0].Action -ne 'Allow' -or $active[0].Direction -ne $direction) {
            throw 'Windows Firewall did not activate the requested network rule.'
        }
        $filter = $active[0] | Get-NetFirewallPortFilter
        $network = $active[0] | Get-NetFirewallInterfaceFilter
        $application = $active[0] | Get-NetFirewallApplicationFilter
        $address = $active[0] | Get-NetFirewallAddressFilter
        if ($filter.Protocol -ne $scope.Protocol -or (Compare-Object @($filter.LocalPort | Sort-Object) @($scope.Ports | Sort-Object)) -or @($network.InterfaceAlias).Count -ne 1 -or $network.InterfaceAlias -ne $adapter.Name -or $application.Program -ne $scope.Program -or $address.RemoteAddress -ne 'LocalSubnet') {
            throw 'Windows Firewall applied a different network scope.'
        }
      }
    }
    $connections = @(Get-NetConnectionProfile -InterfaceIndex $adapter.ifIndex -ErrorAction SilentlyContinue)
    foreach ($connection in $connections) {
        $profileName = [string]$connection.NetworkCategory
        if ($profileName -eq 'DomainAuthenticated') { $profileName = 'Domain' }
        $profile = Get-NetFirewallProfile -Name $profileName -PolicyStore ActiveStore
        if ($profile.Enabled -eq 'True' -and ($profile.AllowLocalFirewallRules -eq 'False' -or $profile.AllowInboundRules -eq 'False')) {
            throw 'The network policy does not allow local firewall exceptions. Contact the network administrator.'
        }
    }
    exit 0
} catch {
    $_.Exception.Message | Set-Content -LiteralPath ($SpecificationPath + '.error.txt') -Encoding UTF8
    Write-Error $_ -ErrorAction Continue
    exit 1
}
