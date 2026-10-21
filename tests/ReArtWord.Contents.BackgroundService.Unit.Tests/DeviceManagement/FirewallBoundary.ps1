$PSModuleAutoLoadingPreference = 'None'
# The real cmdlets require enums, not Boolean values. Keep that parameter-binding boundary in the fixture.
enum DeviceEnabled { False = 0; True = 1 }
$script:calls = [System.Collections.Generic.List[object]]::new()
function Get-NetFirewallRule {
    param($PolicyStore)
    [pscustomobject]@{ Name='target[*]' }
    [pscustomobject]@{ Name='target-other' }
}
function Set-NetFirewallProfile {
    param($PolicyStore,$Profile,[DeviceEnabled]$Enabled)
    $script:calls.Add(@{kind='profile';name=$Profile;enabled=$Enabled.ToString()})
}
function Set-NetFirewallRule {
    param($InputObject,[DeviceEnabled]$Enabled)
    $script:calls.Add(@{kind='enabled';name=$InputObject.Name;enabled=$Enabled.ToString()})
}
function Remove-NetFirewallRule {
    param($InputObject,$Confirm)
    $script:calls.Add(@{kind='remove';name=$InputObject.Name})
}
try { Set-DeviceState $request; $success=$true } catch { $success=$false }
[ordered]@{success=$success;started=$script:deviceStarted;calls=@($script:calls.ToArray())} | ConvertTo-Json -Depth 16 -Compress
