function Get-DeviceState($query) {
    $adapters = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Sort-Object InterfaceGuid)
    if ($adapters.Count -gt 128) { throw 'Device.ReadIncomplete' }
    $interfaces = @($adapters | Where-Object { $_.InterfaceGuid -and $_.MacAddress } | ForEach-Object {
        [ordered]@{ id = ([guid]$_.InterfaceGuid).ToString('D'); name = [string]$_.Name; macAddress = [string]$_.MacAddress; physical = ($_.HardwareInterface -eq $true) }
    })
    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object { $_.IPAddress -ne '127.0.0.1' } | Sort-Object IPAddress | ForEach-Object {
        [ordered]@{ address = [string]$_.IPAddress; prefixLength = [int]$_.PrefixLength; automatic = $false }
    })
    if ($addresses.Count -gt 256) { throw 'Device.ReadIncomplete' }
    [ordered]@{ interfaces = $interfaces; addresses = $addresses }
}
