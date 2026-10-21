using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Targets;

/// <summary>実行PCの候補集約、優先順位、実測値の比較を確認します。</summary>
public sealed class PcSelectionTests
{
    private static DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddDays(100);

    /// <summary>名前順を使わず、重複を除いた候補の最優先PCを選びます。</summary>
    [Fact]
    public void HighestPriorityWinsAcrossCombinedSelections()
    {
        PeerInfo low = Peer("a-low", "first", 100);
        PeerInfo high = Peer("z-high", "second", 900);
        PeerInfo sameHost = Peer("duplicate", "second", 300);
        PcSelectionResolver resolver = Resolver([low, high, sameHost]);
        PcSelection[] choices = [new(["first", "second"]), new(["second"])];
        TargetResolution one = resolver.Resolve(choices, EPcExecutionMode.One);
        Assert.Equal(high.InstanceId, Assert.Single(one.Peers).InstanceId);
        TargetResolution all = resolver.Resolve(choices, EPcExecutionMode.All);
        Assert.Equal(2, all.Peers.Count);
        Assert.Contains(all.Peers, item => item.InstanceId == high.InstanceId);
        Assert.DoesNotContain(all.Peers, item => item.InstanceId == sameHost.InstanceId);
    }

    /// <summary>最低値のPCを先に選び、複数の選択結果は最後に優先度で絞ります。</summary>
    [Fact]
    public void ResourceChoicesAreCombinedBeforeFinalPrioritySelection()
    {
        PeerInfo cpu = Peer("cpu", "cpu-host", 900);
        PeerInfo ram = Peer("ram", "ram-host", 500);
        PcSelectionResolver resolver = Resolver([cpu, ram],
            Sample(cpu, new("cpu.usage", "machine", 10, "percent"), new("memory.usage", "machine", 70, "percent")),
            Sample(ram, new("cpu.usage", "machine", 60, "percent"), new("memory.usage", "machine", 20, "percent")));
        PcSelection[] choices = [new(Lowest: new(EPcResource.Cpu)), new(Lowest: new(EPcResource.Ram))];
        TargetResolution result = resolver.Resolve(choices, EPcExecutionMode.One);
        Assert.Equal(cpu.InstanceId, Assert.Single(result.Peers).InstanceId);
        Assert.Equal(4, result.Selection!.Observations.Count);
        Assert.Equal(2, result.Selection.CandidateHostIds.Count);
    }

    /// <summary>欠測・古い観測・別実体の値を、低い使用率として採用しません。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("instance")]
    public void UnconfirmedMeasurementsCannotBeatMeasuredCandidates(string failure)
    {
        PeerInfo available = Peer("measured", "measured-host", 100);
        PeerInfo unavailable = Peer("unconfirmed", "other-host", 1000);
        ResourceSample bad = Sample(unavailable, new ResourceMetric("cpu.usage", "machine", 0, "percent"));
        bad = failure switch
        {
            "expired" => bad with { ObservedAt = Now.AddSeconds(-11) },
            "instance" => bad with { InstanceId = Guid.NewGuid() },
            _ => bad with { Metrics = [] },
        };
        PcSelectionResolver resolver = Resolver([available, unavailable], Sample(available, new ResourceMetric("cpu.usage", "machine", 80, "percent")), bad);
        TargetResolution result = resolver.Resolve([new(Lowest: new(EPcResource.Cpu))], EPcExecutionMode.One);
        Assert.Equal(available.InstanceId, Assert.Single(result.Peers).InstanceId);
        Assert.Single(result.Selection!.Observations, item => item.Value is null && item.Reason is not null);
    }

    /// <summary>比較対象がなければ未実行とし、既定のPCへ置き換えません。</summary>
    [Fact]
    public void EmptySelectionDoesNotFallBackToAvailablePeer()
    {
        PcSelectionResolver resolver = Resolver([Peer("a", "host", 1000)]);
        Assert.Equal(EOutcome.NotExecuted, resolver.Resolve([new(HostIds: [])], EPcExecutionMode.One).Error!.Outcome);
        Assert.Equal(EOutcome.NotExecuted, resolver.Resolve([new(Lowest: new(EPcResource.Gpu))], EPcExecutionMode.One).Error!.Outcome);
        Assert.Equal(EOutcome.NotExecuted, resolver.Resolve([new(["host", "unknown"])], EPcExecutionMode.All).Error!.Outcome);
    }

    /// <summary>全候補実行では停止PCも対象として残し、後続の対象別結果へ渡します。</summary>
    [Fact]
    public void AllKeepsUnavailableExplicitTargetsWhileOneUsesEligiblePeer()
    {
        PeerInfo ready = Peer("ready", "first", 100);
        PeerInfo unavailable = Peer("offline", "second", 1000) with { Status = EPeerStatus.Unavailable };
        PcSelectionResolver resolver = Resolver([ready, unavailable]);
        PcSelection[] choices = [new(["first", "second"])];
        Assert.Equal(2, resolver.Resolve(choices, EPcExecutionMode.All).Peers.Count);
        Assert.Equal(ready.InstanceId, Assert.Single(resolver.Resolve(choices, EPcExecutionMode.One).Peers).InstanceId);
        TargetResolution waiting = resolver.Resolve([new(["second"])], EPcExecutionMode.One);
        Assert.Equal(unavailable.InstanceId, Assert.Single(waiting.Peers).InstanceId);
        Assert.Equal("selection.pcUnavailable", waiting.Selection!.CandidateProblems!["second"]);
        TargetResolution eligible = resolver.Resolve([new(["first", "unknown"])], EPcExecutionMode.One);
        Assert.Equal(ready.InstanceId, Assert.Single(eligible.Peers).InstanceId);
        Assert.Equal("selection.pcMissing", eligible.Selection!.CandidateProblems!["unknown"]);
        Assert.Equal(2, resolver.Resolve([new(AllHosts: true)], EPcExecutionMode.All).Peers.Count);
    }

    /// <summary>GPU未指定時は最大使用率を使い、指定時はそのGPUだけを比較します。</summary>
    [Fact]
    public void GpuSelectionUsesTheBusiestDeviceUnlessExplicitlySelected()
    {
        PeerInfo peer = Peer("gpu", "host", 100);
        ResourceSample sample = Sample(peer, new("gpu.usage", "gpu-0", 10, "percent"), new("gpu.usage", "gpu-1", 90, "percent"));
        PcSelectionObservation all = PcResourceComparison.Read(peer, sample, new(EPcResource.Gpu), Now);
        Assert.Equal(90, all.Value);
        Assert.Equal("gpu-1", all.GpuId);
        PcSelectionObservation chosen = PcResourceComparison.Read(peer, sample, new(EPcResource.Gpu, GpuId: "gpu-0"), Now);
        Assert.Equal(10, chosen.Value);
        Assert.Equal("gpu-0", chosen.GpuId);
    }

    /// <summary>専用VRAMの使用量と容量が同じGPUに対応するときだけ使用率を算出します。</summary>
    [Fact]
    public void VramRatioUsesDedicatedMemoryAndIgnoresSharedMemory()
    {
        PeerInfo peer = Peer("gpu", "host", 100);
        ResourceSample sample = Sample(peer, new("gpu.memory.used", "gpu", 2, "bytes"), new("gpu.memory.total", "gpu", 8, "bytes"),
            new("gpu.memory.shared.used", "gpu", 100, "bytes"));
        Assert.Equal(25, PcResourceComparison.Read(peer, sample, new(EPcResource.Vram), Now).Value);
        Assert.Equal(2, PcResourceComparison.Read(peer, sample, new(EPcResource.Vram, UsedBytes: true), Now).Value);
    }

    /// <summary>同じ参加状態でもDegradedを実行候補から除外しません。</summary>
    [Fact]
    public void CoordinatableDegradedPeerRemainsEligible()
    {
        PeerInfo peer = Peer("degraded", "host", 100) with { Status = EPeerStatus.Degraded };
        Assert.Equal(peer.InstanceId, Assert.Single(Resolver([peer]).Resolve([new(AllHosts: true)], EPcExecutionMode.One).Peers).InstanceId);
    }

    /// <summary>試験開始時の観測と参加状態を解決器へ渡します。</summary>
    /// <returns>一件の実行対象を固定する解決器。</returns>
    private static PcSelectionResolver Resolver(PeerInfo[] peers, params ResourceSample[] samples)
        => new(peers, samples.ToDictionary(sample => sample.PeerId, StringComparer.Ordinal), Now, peers[0].InstanceId);

    /// <summary>異なる実体を持つPCの参加情報を作ります。</summary>
    /// <returns>実行可能なPeer。</returns>
    private static PeerInfo Peer(string id, string host, int priority)
        => new(id, Guid.NewGuid(), host, "127.0.0.1", 1, EPeerStatus.Ready, priority, DateTimeOffset.UnixEpoch, Now);

    /// <summary>指定Peerの実体と時刻に対応する観測を作ります。</summary>
    /// <returns>履歴保存状態に依存しない実測値。</returns>
    private static ResourceSample Sample(PeerInfo peer, params ResourceMetric[] metrics)
        => new(Guid.NewGuid(), peer.PeerId, Now, metrics, InstanceId: peer.InstanceId);
}
