$PSModuleAutoLoadingPreference = 'None'
$script:calls = [System.Collections.Generic.List[object]]::new()
$adapterId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
$switchId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
if ($request.scenario -eq 'protected') { $switchId = 'c08cb7b8-9b3c-408e-8e30-5e16a3aeb444' }
$script:switchList = @([pscustomobject]@{ Id=$switchId; Name='existing[*]'; SwitchType='Internal'; AllowManagementOS=$true; NetAdapterInterfaceDescriptions=@() })
$script:portList = @([pscustomobject]@{Id='port[*]';Name='Host NIC';SwitchId=$switchId;IsManagementOs=$true;VMName=$null})
if ($request.scenario -eq 'guest') { $script:portList += [pscustomobject]@{Id='guest';Name='Guest NIC';SwitchId=$switchId;IsManagementOs=$false;VMName='guest VM'} }
function Get-VMSwitch { $script:switchList }
function Get-NetAdapter {
    $status='Up';if ($request.scenario -eq 'absent') { $status='Not Present' }
    [pscustomobject]@{InterfaceGuid=$adapterId;Name='NIC[*]';InterfaceDescription='Physical[*]; not code';HardwareInterface=$true;Status=$status}
    if ($request.scenario -eq 'ambiguous') { [pscustomobject]@{InterfaceGuid='dddddddd-dddd-dddd-dddd-dddddddddddd';Name='other';InterfaceDescription='Physical[*]; not code';HardwareInterface=$true;Status='Up'} }
}
function Get-NetAdapterBinding {
    param([switch]$AllBindings,[switch]$IncludeHidden)
    [pscustomobject]@{Name='NIC[*]';ComponentID='vms_pp';Enabled=($request.scenario -eq 'occupied')}
}
function Get-VMNetworkAdapter {
    param([switch]$All)
    if (-not $All) { throw 'All ports must be checked' }
    $script:portList
}
function New-VMSwitch {
    param($Name,[ValidateSet('Internal','Private')]$SwitchType,$NetAdapterInterfaceDescription,[bool]$AllowManagementOS,[switch]$Confirm)
    $script:calls.Add(@{action='create';name=$Name;kind=$SwitchType;description=$NetAdapterInterfaceDescription;shared=$AllowManagementOS})
    if ($request.scenario -eq 'failure') { throw 'OS refused' }
    $kind=$SwitchType; $descriptions=@();$shared=$SwitchType -eq 'Internal'
    if ($NetAdapterInterfaceDescription) {$kind='External';$descriptions=@($NetAdapterInterfaceDescription);$shared=$AllowManagementOS}
    $script:switchList += [pscustomobject]@{Id='cccccccc-cccc-cccc-cccc-cccccccccccc';Name=$Name;SwitchType=$kind;AllowManagementOS=$shared;NetAdapterInterfaceDescriptions=$descriptions}
}
function Remove-VMSwitch {
    param($VMSwitch,[switch]$Force,[switch]$Confirm)
    if (-not $Force) { throw 'Non-interactive switch removal must suppress the Hyper-V connection prompt.' }
    $script:calls.Add(@{action='delete';id=[string]$VMSwitch.Id;name=$VMSwitch.Name})
    if ($request.scenario -eq 'failure') { throw 'OS refused' }
    $script:switchList=@($script:switchList | Where-Object { $_.Id -ne $VMSwitch.Id })
    $script:portList=@($script:portList | Where-Object { $_.SwitchId -ne $VMSwitch.Id })
}
$resource='switches'
if ($request.action -eq 'virtualSwitchDelete') { $resource='switch/'+$switchId }
$query=[pscustomobject]@{resourceId=$resource}
$before=Get-DeviceVirtualSwitchState $query
if ($request.scenario -eq 'changed') { $script:portList += [pscustomobject]@{Id='new-port';Name='new';SwitchId=$switchId;IsManagementOs=$true;VMName=$null} }
if ($request.scenario -eq 'duplicate') { $request.change.name='existing[*]' }
$operation=[pscustomobject]@{action=$request.action;query=$query;change=$request.change;before=$before}
$reason=$null
try { Set-DeviceVirtualSwitch $operation; $success=$true } catch { $success=$false; $reason=Get-DeviceError $_ }
$after=Get-DeviceVirtualSwitchState $query
[ordered]@{success=$success;started=$script:deviceStarted;reason=$reason;calls=@($script:calls.ToArray());before=$before;after=$after;steps=@($script:deviceSteps.ToArray())}|ConvertTo-Json -Depth 32 -Compress
