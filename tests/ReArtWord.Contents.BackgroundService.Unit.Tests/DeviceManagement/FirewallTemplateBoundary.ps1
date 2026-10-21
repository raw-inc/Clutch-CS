$PSModuleAutoLoadingPreference='None'
function Get-NetFirewallProfile { param($PolicyStore) [pscustomobject]@{Name='Private';Enabled=$true} }
function Get-NetFirewallRule {
 param($PolicyStore)
 foreach($id in @('literal[*]','second')){
  if($PolicyStore -eq 'PersistentStore' -and $request.scenario -eq 'readonly'){continue}
  [pscustomobject]@{Name=$id;DisplayName='App';Direction='Inbound';Action='Allow';Enabled='True';Profile='Private';Platform=@();EdgeTraversalPolicy='Block';LooseSourceMapping=$false;LocalOnlyMapping=$false;Owner='';PolicyAppId='';RemoteDynamicKeywordAddresses=@();PolicyStoreSourceType='Local'}
 }
}
function Get-NetFirewallPortFilter {
 param($AssociatedNetFirewallRule)
 $icmp='Any';if($request.scenario -eq 'icmp'){$icmp='8'}
 [pscustomobject]@{Protocol='TCP';LocalPort=@('1234');RemotePort=@('Any');IcmpType=$icmp;DynamicTarget='Any'}
}
function Get-NetFirewallAddressFilter { param($AssociatedNetFirewallRule) [pscustomobject]@{LocalAddress=@('Any');RemoteAddress=@('Any')} }
function Get-NetFirewallApplicationFilter {
 param($AssociatedNetFirewallRule)
 $package='Any';if($request.scenario -eq 'package'){$package='S-1-15-2-123'}
 [pscustomobject]@{Program='Any';Package=$package}
}
function Get-NetFirewallServiceFilter {
 param($AssociatedNetFirewallRule)
 $service='Any';if($request.scenario -eq 'service'){$service='Spooler'}
 [pscustomobject]@{Service=$service}
}
function Get-NetFirewallInterfaceFilter { param($AssociatedNetFirewallRule) [pscustomobject]@{InterfaceAlias=@('Any')} }
function Get-NetFirewallInterfaceTypeFilter { param($AssociatedNetFirewallRule) [pscustomobject]@{InterfaceType='Any'} }
function Get-NetFirewallSecurityFilter {
 param($AssociatedNetFirewallRule)
 $authentication='NotRequired';if($request.scenario -eq 'security'){$authentication='Required'}
 [pscustomobject]@{Authentication=$authentication;Encryption='NotRequired';OverrideBlockRules=$false;LocalUser='Any';RemoteUser='Any';RemoteMachine='Any'}
}
$query=[pscustomobject]@{resourceId='bulk';filter='["literal[*]","second"]';offset=0}
$result=Get-DeviceState $query
$result|ConvertTo-Json -Depth 20 -Compress
