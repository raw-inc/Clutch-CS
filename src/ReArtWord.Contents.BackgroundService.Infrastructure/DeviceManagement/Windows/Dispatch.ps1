try {
    if ($request.mode -eq 'read') {
        $data = Get-DeviceState $request.query
    } elseif ($request.mode -eq 'apply') {
        Assert-DeviceAdministrator
        $current = Get-DeviceState $request.query
        if (-not (Test-DeviceValue $current $request.before)) { throw 'Device.SettingsChanged' }
        Set-DeviceState $request
        $data = $null
    } else { throw 'Device.InvalidRequest' }
    [ordered]@{ success = $true; started = $script:deviceStarted; data = $data; steps = @($script:deviceSteps.ToArray()); reason = $null } |
        ConvertTo-Json -Compress -Depth 32
} catch {
    [ordered]@{ success = $false; started = $script:deviceStarted; data = $null; steps = @($script:deviceSteps.ToArray()); reason = (Get-DeviceError $_) } |
        ConvertTo-Json -Compress -Depth 32
}
