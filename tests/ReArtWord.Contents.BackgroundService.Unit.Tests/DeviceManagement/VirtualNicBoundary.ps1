$PSModuleAutoLoadingPreference = 'None'
$script:hyperVProblem = $null
$script:calls = [System.Collections.Generic.List[object]]::new()
function Get-VMNetworkAdapter {
    param([switch]$ManagementOS)
    if (-not $ManagementOS) { throw 'Management OS is required' }
    [pscustomobject]@{ Id='target[*]'; Name='target'; SwitchId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' }
    [pscustomobject]@{ Id='target-other'; Name='other'; SwitchId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' }
}
function Get-VMNetworkAdapterVlan {
    param($VMNetworkAdapter)
    [pscustomobject]@{ OperationMode=$request.mode }
}
function Set-VMNetworkAdapterVlan {
    param($VMNetworkAdapter,[switch]$Access,[switch]$Untagged,$VlanId)
    $script:calls.Add(@{ id=$VMNetworkAdapter.Id; access=[bool]$Access; untagged=[bool]$Untagged; vlanId=$VlanId })
}
try {
    Set-DeviceVirtualNic ([pscustomobject]@{ action='virtualNicVlan'; query=[pscustomobject]@{resourceId='vnic/target[*]'}; change=[pscustomobject]@{vlanId=$request.vlanId} })
    $success=$true
} catch { $success=$false }
[ordered]@{success=$success;started=$script:deviceStarted;calls=@($script:calls.ToArray())} | ConvertTo-Json -Depth 16 -Compress
