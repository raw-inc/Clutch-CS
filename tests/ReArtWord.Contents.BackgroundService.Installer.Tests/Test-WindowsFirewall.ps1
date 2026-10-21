# NetSecurity is replaced by an in-memory OS boundary. This script never edits Windows Firewall.
$ErrorActionPreference = 'Stop'
$global:clutchFirewallFixture = @{ rules = @{} }
$global:clutchFirewallFixture.alias = 'Fixture Network'
$global:clutchFirewallFixture.interfaceId = [guid]::NewGuid()
$global:clutchFirewallFixture.allowLocal = 'True'
$global:clutchFirewallFixture.hideActive = $false
$global:clutchFirewallFixture.created = 0
$global:clutchFirewallFixture.updated = 0
function Get-NetFirewallRule {
    [CmdletBinding()] param([string]$PolicyStore)
    if ($PolicyStore -eq 'ActiveStore' -and $global:clutchFirewallFixture.hideActive) { return }
    $global:clutchFirewallFixture.rules.Values
}
function New-NetFirewallRule {
    [CmdletBinding()] param($Name, $PolicyStore, $Enabled, $Action, $Direction, $Profile, $Protocol, $LocalPort, $Program, $InterfaceAlias, $RemoteAddress, $EdgeTraversalPolicy, $DisplayName, $Group)
    if ($global:clutchFirewallFixture.rules.ContainsKey($Name)) { throw 'Duplicate rule.' }
    $global:clutchFirewallFixture.created++
    $global:clutchFirewallFixture.rules[$Name] = [pscustomobject]@{ Name=$Name; Enabled=$Enabled; Action=$Action; Direction=$Direction; Profile=$Profile; Protocol=$Protocol; LocalPort=$LocalPort; Program=$Program; InterfaceAlias=$InterfaceAlias; RemoteAddress=$RemoteAddress; EdgeTraversalPolicy=$EdgeTraversalPolicy }
}
function Set-NetFirewallRule {
    [CmdletBinding()] param($Name, $PolicyStore, $Enabled, $Action, $Direction, $Profile, $Protocol, $LocalPort, $Program, $InterfaceAlias, $RemoteAddress, $EdgeTraversalPolicy)
    if (-not $global:clutchFirewallFixture.rules.ContainsKey($Name)) { throw 'Unknown rule.' }
    $global:clutchFirewallFixture.updated++
    $global:clutchFirewallFixture.rules[$Name] = [pscustomobject]@{ Name=$Name; Enabled=$Enabled; Action=$Action; Direction=$Direction; Profile=$Profile; Protocol=$Protocol; LocalPort=$LocalPort; Program=$Program; InterfaceAlias=$InterfaceAlias; RemoteAddress=$RemoteAddress; EdgeTraversalPolicy=$EdgeTraversalPolicy }
}
function Remove-NetFirewallRule {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject)
    process { $global:clutchFirewallFixture.rules.Remove($InputObject.Name) }
}
function Get-NetFirewallPortFilter { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject) process { $InputObject } }
function Get-NetFirewallInterfaceFilter { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject) process { $InputObject } }
function Get-NetFirewallApplicationFilter { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject) process { $InputObject } }
function Get-NetFirewallAddressFilter { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject) process { $InputObject } }
function Get-NetAdapter { [CmdletBinding()] param([switch]$IncludeHidden) [pscustomobject]@{ InterfaceGuid=$global:clutchFirewallFixture.interfaceId; ifIndex=1; Name=$global:clutchFirewallFixture.alias } }
function Get-NetIPAddress { [CmdletBinding()] param($InterfaceIndex, $AddressFamily) [pscustomobject]@{ IPAddress='192.0.2.10' } }
function Get-NetConnectionProfile { [CmdletBinding()] param($InterfaceIndex) [pscustomobject]@{ NetworkCategory='Private' } }
function Get-NetFirewallProfile { [CmdletBinding()] param($Name, $PolicyStore) [pscustomobject]@{ Enabled='True'; AllowLocalFirewallRules=$global:clutchFirewallFixture.allowLocal; AllowInboundRules='True' } }
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('clutch-firewall-tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$firewallScript = Join-Path $PSScriptRoot '../../src/ReArtWord.Contents.BackgroundService.Infrastructure/Installation/WindowsFirewall.ps1'
$specificationPath = Join-Path $fixtureRoot 'specification.json'
$corePath = Join-Path $fixtureRoot 'previous-core.exe'
$nextCore = Join-Path $fixtureRoot 'Clutch-CS.CoreService.exe'
[IO.File]::WriteAllText($corePath, 'fixture')
[IO.File]::WriteAllText($nextCore, 'replacement')
$specification = @{ Name='Clutch-CS-' + [guid]::NewGuid().ToString('N') + '-Network'; InterfaceId=$global:clutchFirewallFixture.interfaceId.ToString('B'); BindAddress='192.0.2.10'; Ports=@(47740,50123); CorePath=$corePath; IncludeApplication=$true; Remove=$false }
function Invoke-Fixture([int]$ExpectedExit = 0) {
    $specification | ConvertTo-Json | Set-Content -LiteralPath $specificationPath -Encoding UTF8
    & $firewallScript -SpecificationPath $specificationPath 2>$null
    if ($LASTEXITCODE -ne $ExpectedExit) {
        throw ('Unexpected firewall result: ' + $LASTEXITCODE + ' / ' + (Get-Content -LiteralPath ($specificationPath + '.error.txt') -Raw))
    }
}
try {
    Invoke-Fixture
    Assert-True ($global:clutchFirewallFixture.rules.Count -eq 6) 'Expected two management-port rules and four application rules.'
    foreach ($rule in $global:clutchFirewallFixture.rules.Values) {
        Assert-True ($rule.InterfaceAlias -eq $global:clutchFirewallFixture.alias -and $rule.RemoteAddress -eq 'LocalSubnet') 'The selected network scope was lost.'
        Assert-True ($rule.Action -eq 'Allow' -and $rule.Enabled -eq 'True') 'The rule was not enabled.'
        if ($rule.Name.Contains('-Core-')) {
            Assert-True ($rule.Program -eq $corePath -and $rule.LocalPort -eq 'Any') 'Dynamic application ports were not scoped to the executable.'
        } else {
            Assert-True ($rule.Protocol -eq 'UDP' -and $rule.LocalPort.Count -eq 2) 'Management/discovery port scope is incorrect.'
        }
    }
    $specification.CorePath = $nextCore
    Invoke-Fixture
    Assert-True ($global:clutchFirewallFixture.created -eq 6 -and $global:clutchFirewallFixture.updated -eq 6 -and $global:clutchFirewallFixture.rules.Count -eq 6) 'Update created duplicates.'
    Assert-True (@($global:clutchFirewallFixture.rules.Values | Where-Object { $_.Program -eq $corePath }).Count -eq 0) 'The previous executable remained allowed after update.'
    Assert-True (@($global:clutchFirewallFixture.rules.Values | Where-Object { $_.Program -eq $nextCore }).Count -eq 4) 'TCP/UDP application permissions did not follow the renamed executable.'
    $specification.CorePath = $corePath
    Invoke-Fixture
    Assert-True (@($global:clutchFirewallFixture.rules.Values | Where-Object { $_.Program -eq $nextCore }).Count -eq 0) 'Rollback retained permissions for the replaced executable.'
    $originalName = $specification.Name
    $specification.Name = 'Clutch-CS-' + [guid]::NewGuid().ToString('N') + '-Network'
    Invoke-Fixture
    $specification.Name = $originalName
    $specification.Remove = $true
    Invoke-Fixture
    Assert-True ($global:clutchFirewallFixture.rules.Count -eq 6) 'Uninstall changed a different installation.'
    Invoke-Fixture
    Assert-True ($global:clutchFirewallFixture.rules.Count -eq 6) 'Repeated removal changed another installation.'
    $specification.Remove = $false
    $global:clutchFirewallFixture.allowLocal = 'False'
    Invoke-Fixture 1
    Assert-True ((Get-Content -LiteralPath ($specificationPath + '.error.txt') -Raw).Contains('network policy')) 'Policy failure was hidden.'
    $global:clutchFirewallFixture.allowLocal = 'True'
    $global:clutchFirewallFixture.hideActive = $true
    Invoke-Fixture 1
    Assert-True ((Get-Content -LiteralPath ($specificationPath + '.error.txt') -Raw).Contains('activate')) 'Inactive rules were reported as successful.'
    $global:clutchFirewallFixture.hideActive = $false
    $specification.Name = 'Clutch-CS-*'
    Invoke-Fixture 1
    Assert-True ($global:clutchFirewallFixture.rules.Count -eq 12) 'Invalid ownership modified rules.'
    Write-Output 'Firewall fixture passed: create, selected network, TCP/UDP, update, rollback, uninstall isolation, policy failure, inactive rules, invalid ownership.'
} finally {
    $canonical = [IO.Path]::GetFullPath($fixtureRoot)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $canonical.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($canonical)).StartsWith('clutch-firewall-tests-', [StringComparison]::Ordinal)) { throw 'Invalid fixture cleanup path.' }
    Remove-Item -LiteralPath $canonical -Recurse -Force
    Remove-Variable -Name clutchFirewallFixture -Scope Global
}
