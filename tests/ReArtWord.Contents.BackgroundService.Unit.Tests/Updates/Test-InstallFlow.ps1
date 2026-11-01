[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $InstallerPath,
    [Parameter(Mandatory)][string] $CoreExecutablePath,
    [Parameter(Mandatory)][string] $VerifierExecutablePath,
    [switch] $RequireAvx2Disabled
)
$ErrorActionPreference = 'Stop'
# AVX2 無効時の試験では、親プロセスの実状態と子プロセスへ渡す設定を確認します。
if ($RequireAvx2Disabled -and ([System.Runtime.Intrinsics.X86.Avx2]::IsSupported -or $env:DOTNET_EnableAVX2 -ne '0')) {
    throw 'Start this test with DOTNET_EnableAVX2=0 before launching PowerShell.'
}
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('contents-install-flow-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$testFailure = $null
try {
    $network = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object { $_.GetIPProperties().UnicastAddresses.Address.IPAddressToString -contains '127.0.0.1' } | Select-Object -First 1
    if (-not $network) { throw 'An explicit loopback NIC is required.' }
    $socket = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Any,0))
    $port = $socket.Client.LocalEndPoint.Port
    $socket.Dispose()
    $payload = Join-Path $fixtureRoot 'payload'
    Copy-Item -LiteralPath ([IO.Path]::GetDirectoryName($CoreExecutablePath)) -Destination $payload -Recurse
    $entry = [IO.Path]::GetFileName($CoreExecutablePath)
    $assembly = Join-Path $payload ([IO.Path]::GetFileNameWithoutExtension($entry) + '.dll')
    $buildVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductVersion
    $rid = [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
    & (Join-Path ([IO.Path]::GetDirectoryName($InstallerPath)) 'New-ReleaseManifest.ps1') -PayloadRoot $payload -ReleaseId fixture -RuntimeIdentifier $rid -EntryPoint $entry -ExpectedBuildVersion $buildVersion
    $clusterId = 'install-' + [Guid]::NewGuid().ToString('N')
    $configuration = Join-Path $fixtureRoot 'contents.toml'
    $data = (Join-Path $fixtureRoot 'data').Replace('\','/')
    $content = (Join-Path $fixtureRoot 'content').Replace('\','/')
    [IO.Directory]::CreateDirectory($content) | Out-Null
    @"
[cluster]
id = "$clusterId"
peerId = "install-peer"
hostId = "$([Net.Dns]::GetHostName())"
address = "127.0.0.1"
broadcast = "127.255.255.255"
interfaceId = "$($network.Id)"
port = $port
initialize = true
[storage]
directory = "$data"
[files]
roots = ["$content"]
"@ | Set-Content -LiteralPath $configuration -Encoding utf8
    $platform = 'Windows'
    if ($IsLinux) { $platform = 'Linux' }
    if ($IsMacOS) { $platform = 'MacOS' }
    $steps = [Collections.Generic.List[string]]::new()
    $artifacts = Join-Path $fixtureRoot 'artifacts'
    $actionsPath = Join-Path ([IO.Path]::GetDirectoryName($InstallerPath)) 'Install-ServiceActions.ps1'
    $realVerifyFakeService = {
        param($action,$data)
        $steps.Add($action)
        if ($action -eq 'verify') {
            . $actionsPath
            return Invoke-InstallAction $action $data $null $VerifierExecutablePath $artifacts
        }
        if ($action -in @('secure','register','recovery')) { return }
        if ($action -eq 'start') {
            $start = [Diagnostics.ProcessStartInfo]::new($data.executable)
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.ArgumentList.Add('--config')
            $start.ArgumentList.Add($data.configuration)
            $start.WorkingDirectory = [IO.Path]::GetDirectoryName($data.executable)
            $process = [Diagnostics.Process]::Start($start)
            $owned.Add($process)
            return
        }
        throw 'Unexpected action.'
    }.GetNewClosure()
    & $InstallerPath -PayloadRoot $payload -InstallationRoot (Join-Path $fixtureRoot 'installed') -ConfigurationPath $configuration -ServiceArtifactRoot $artifacts -Platform $platform -RegisterAndStart -VerifierExecutablePath $VerifierExecutablePath -ActionInvoker $realVerifyFakeService
    $result = Get-Content -LiteralPath (Join-Path $artifacts 'installation-result.json') -Raw | ConvertFrom-Json
    $expectedSteps = if ($platform -eq 'Windows') { 'verify,secure,register,recovery,start,verify' } else { 'verify,register,start,verify' }
    if (-not $result.success -or $result.stage -ne 'ready' -or ($steps -join ',') -ne $expectedSteps) { throw 'The real verification flow did not complete.' }

    # A second empty store must join the running cluster without initial-creation authorization.
    $joinConfiguration = Join-Path $fixtureRoot 'join.toml'
    $joinData = (Join-Path $fixtureRoot 'join-data').Replace('\','/')
    $joinText = (Get-Content -LiteralPath $configuration -Raw).Replace('peerId = "install-peer"', 'peerId = "joined-peer"').Replace('initialize = true', 'initialize = false').Replace($data, $joinData)
    $joinText | Set-Content -LiteralPath $joinConfiguration -Encoding utf8
    $joinArtifacts = Join-Path $fixtureRoot 'join-artifacts'
    $joinActions = {
        param($action,$data)
        if ($action -eq 'verify') {
            . $actionsPath
            return Invoke-InstallAction $action $data $null $VerifierExecutablePath $joinArtifacts
        }
        & $realVerifyFakeService $action $data
    }.GetNewClosure()
    & $InstallerPath -PayloadRoot $payload -InstallationRoot (Join-Path $fixtureRoot 'joined-installation') -ConfigurationPath $joinConfiguration -ServiceArtifactRoot $joinArtifacts -Platform $platform -RegisterAndStart -VerifierExecutablePath $VerifierExecutablePath -ActionInvoker $joinActions
    $joined = Get-Content -LiteralPath (Join-Path $joinArtifacts 'installation-result.json') -Raw | ConvertFrom-Json
    if (-not $joined.success -or $joined.stage -ne 'ready' -or $joined.peerId -ne 'joined-peer' -or $joined.clusterId -ne $result.clusterId -or $owned.Count -ne 2) { throw 'The empty second peer did not join the existing cluster.' }

    # Preserve verifier diagnostics even when preflight fails before installation begins.
    $invalidConfiguration = Join-Path $fixtureRoot 'invalid-network.toml'
    (Get-Content -LiteralPath $configuration -Raw).Replace('address = "127.0.0.1"', 'address = "203.0.113.10"') | Set-Content -LiteralPath $invalidConfiguration -Encoding utf8
    $invalidInstallation = Join-Path $fixtureRoot 'invalid-installation'
    $invalidArtifacts = Join-Path $fixtureRoot 'invalid-artifacts'
    $rejected = $false
    try {
        & $InstallerPath -PayloadRoot $payload -InstallationRoot $invalidInstallation -ConfigurationPath $invalidConfiguration -ServiceArtifactRoot $invalidArtifacts -Platform $platform -RegisterAndStart -VerifierExecutablePath $VerifierExecutablePath
    } catch { $rejected = $true }
    $diagnostic = Get-ChildItem -LiteralPath $invalidArtifacts -Filter '*.result.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json }
    if (-not $rejected -or (Test-Path -LiteralPath $invalidInstallation) -or $diagnostic.success -ne $false -or $diagnostic.stage -ne 'network') { throw 'Failed network preflight did not retain its diagnostic before refusing installation.' }

    $readyRequest = Get-ChildItem -LiteralPath $artifacts -Filter 'verify-*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json } | Where-Object checkReady | Select-Object -First 1
    $readyRequest.configurationHash = 'changed-input'
    $negativePath = Join-Path $artifacts 'negative-input.json'
    $readyRequest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $negativePath -Encoding utf8
    $negative = (& $VerifierExecutablePath --verify-install $negativePath) | ConvertFrom-Json
    if ($LASTEXITCODE -eq 0 -or $negative.success -or $negative.stage -ne 'ready') { throw 'Changed configuration fingerprint was accepted after registration.' }
    $manifestPath = Join-Path $payload 'release-manifest.json'
    $originalManifest = Get-Content -LiteralPath $manifestPath -Raw
    $wrongRid = $originalManifest | ConvertFrom-Json
    $wrongRid.platform = 'win-arm64'
    $wrongRid | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    @{configurationPath=$configuration;manifestPath=$manifestPath} | ConvertTo-Json | Set-Content -LiteralPath $negativePath -Encoding utf8
    $negative = (& $VerifierExecutablePath --verify-install $negativePath) | ConvertFrom-Json
    if ($LASTEXITCODE -eq 0 -or $negative.success -or $negative.stage -ne 'platform') { throw 'Unsupported/wrong RID was accepted in preflight.' }
    [IO.File]::WriteAllText($manifestPath,$originalManifest)
    $failures = @('register','start','ready')
    if ($platform -eq 'Windows') { $failures += 'permissions','recovery' }
    foreach ($failure in $failures) {
        $observed = [Collections.Generic.List[string]]::new()
        $fake = {
            param($action,$data)
            $observed.Add($action)
            if ($action -eq $failure -or ($failure -eq 'permissions' -and $action -eq 'secure') -or ($failure -eq 'ready' -and $action -eq 'verify' -and $data.checkReady)) { throw 'Injected fixture failure.' }
            if ($action -eq 'verify') { return @{success=$true;stage='preflight';configurationHash='fixed';manifestHash='fixed';startedAfter=[DateTimeOffset]::UtcNow;peerId='p';clusterId='c';platform='fixture'} }
        }.GetNewClosure()
        $failedArtifacts = Join-Path $fixtureRoot ($failure + '-artifacts')
        $rejected = $false
        try { & $InstallerPath -PayloadRoot $payload -InstallationRoot (Join-Path $fixtureRoot ($failure + '-installed')) -ConfigurationPath $configuration -ServiceArtifactRoot $failedArtifacts -Platform $platform -RegisterAndStart -ActionInvoker $fake } catch { $rejected = $true }
        $failureResult = Get-Content -LiteralPath (Join-Path $failedArtifacts 'installation-result.json') -Raw | ConvertFrom-Json
        if (-not $rejected -or $failureResult.success -or $failureResult.stage -ne $failure) { throw 'Failure phase became installation success.' }
        if ($failure -in @('permissions','register','recovery') -and $observed.Contains('start')) { throw 'Start continued after failed permissions, registration or recovery configuration.' }
    }
    Write-Output 'PASS: two isolated Core processes install/create/join through UDP, preflight diagnostics retained, wrong RID/changed configuration rejected, fake OS permissions/registration/recovery, failed permissions/registration/recovery/start/Ready never succeeds.'
    if ($RequireAvx2Disabled) { Write-Output 'PASS: installation verification and both Core processes completed with DOTNET_EnableAVX2=0.' }
} catch {
    $testFailure = $_.Exception
    throw
} finally {
    $cleanupFailures = [Collections.Generic.List[Exception]]::new()
    foreach ($process in $owned) {
        try {
            if (-not $process.HasExited) { $process.Kill($true) }
            # 試験が起動したプロセスの終了後に、配置とデータを削除します。
            $process.WaitForExit()
        } catch {
            $cleanupFailures.Add($_.Exception)
        } finally {
            $process.Dispose()
        }
    }
    if ($cleanupFailures.Count -ne 0) {
        if ($null -ne $testFailure) { $cleanupFailures.Insert(0, $testFailure) }
        throw [AggregateException]::new('Installation test processes could not be stopped. The test files were retained.', $cleanupFailures)
    }
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('contents-install-flow-',[StringComparison]::Ordinal)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
# 拒否を確認した Verifier の終了コードを、試験全体の失敗として返さないようにします。
exit 0
