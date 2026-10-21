$PSModuleAutoLoadingPreference='None'
function Get-NetAdapter {
    [pscustomobject]@{InterfaceGuid=[guid]'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';ifIndex=42;Name='Test NIC';InterfaceDescription='Test';MacAddress='00-11-22-33-44-55';Status='Up';HardwareInterface=$true;PnPDeviceID='PCI\test'}
}
function Get-NetAdapterBinding {
    param($ComponentID,[switch]$IncludeHidden)
    if (@($ComponentID).Count -ne 2 -or -not $IncludeHidden) {throw 'Incorrect binding selection'}
    [pscustomobject]@{Name='Test NIC';ComponentID='ms_tcpip';Enabled=$true}
    [pscustomobject]@{Name='Test NIC';ComponentID='ms_tcpip6';Enabled=$false}
}
function Get-CimInstance {
    param($Namespace,$ClassName,$Filter)
    if($Namespace -ne 'root/StandardCimv2'){throw 'Incorrect namespace'}
    switch($ClassName){
        'MSFT_NetIPInterface' {
            [pscustomobject]@{InterfaceIndex=42;AddressFamily=2;Dhcp=0;AutomaticMetric=1;InterfaceMetric=25;RouterDiscovery=0}
            [pscustomobject]@{InterfaceIndex=42;AddressFamily=23;Dhcp=1;AutomaticMetric=0;InterfaceMetric=40;RouterDiscovery=2}
        }
        'MSFT_NetIPAddress' {
            [pscustomobject]@{InterfaceIndex=42;AddressFamily=2;IPAddress='192.0.2.4';PrefixLength=24;PrefixOrigin=1}
            [pscustomobject]@{InterfaceIndex=42;AddressFamily=23;IPAddress='fe80::4%42';PrefixLength=64;PrefixOrigin=2}
        }
        'MSFT_DNSClientServerAddress' { [pscustomobject]@{InterfaceIndex=42;AddressFamily=2;ServerAddresses=@('192.0.2.53')} }
        'MSFT_NetRoute' { if(-not $Filter){throw 'Unbounded route query'} }
        default {throw 'Unexpected CIM query'}
    }
}
function Get-ItemProperty {param($LiteralPath) [pscustomobject]@{NameServer='192.0.2.53'} }
function Get-DeviceHyperVNetwork { [pscustomobject]@{switches=@();virtualNics=@();problem=$null} }
Get-DeviceState ([pscustomobject]@{resourceId=$null}) | ConvertTo-Json -Depth 20 -Compress
