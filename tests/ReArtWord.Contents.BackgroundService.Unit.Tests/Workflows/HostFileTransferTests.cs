using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>転送元Hostと共通PC選択を、所属クラスタ内の固定配送へ接続します。</summary>
public sealed class HostFileTransferTests
{
    /// <summary>実行時に渡された転送元Hostを解決し、古い保存値へ戻りません。</summary>
    /// <returns>配送直前に一つの転送元Peerへ固定された要求です。</returns>
    [Fact]
    public async Task RuntimeSourceHostIsResolvedBeforeDispatchAsync()
    {
        WorkflowFixture.ActionNode supply = new((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject("host-source"))));
        WorkflowFixture fixture = new([supply, .. FileSystemNodeRegistration.Create(new FileSystemOptions([Path.GetTempPath()]))]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Peers.Others = [Peer("source", "host-source")];
        RemoteExecutionRequest? sent = null;
        string transferType = "file.transfer";
        fixture.Dispatcher.Handler = (request, token) =>
        {
            if (string.Equals(request.Node.Type, transferType, StringComparison.Ordinal))
            {
                sent = request;
                return Task.FromResult(NodeResult.Success());
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        NodeDefinition transfer = Transfer("unavailable-old-host");
        WorkflowDefinition definition = new("dynamic-transfer", "Transfer", [WorkflowFixture.Definition("supply"), transfer], [new("supply", "value", transfer.Id, SettingPorts.Name("sourceHost"))]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "supply").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.NotNull(sent);
        Assert.Equal("source", sent.Node.Settings.GetProperty("sourcePeer").GetString());
        Assert.False(sent.Node.Settings.TryGetProperty("sourceHost", out _));
        Assert.Equal("unavailable-old-host", transfer.Settings.GetProperty("sourceHost").GetString());
    }

    /// <summary>一Hostの複数Peerを一回にまとめ、転送元を配送中に再選定しません。</summary>
    /// <returns>実ランタイムから配送された要求です。</returns>
    [Fact]
    public async Task HostSelectionFixesSourceAndDeduplicatesDestinationsAsync()
    {
        await using WorkflowFixture fixture = Create();
        fixture.Peers.Others = [Peer("c", "host-b"), Peer("b", "host-b"), Peer("source", "host-source")];
        NodeDefinition transfer = Transfer() with { PcExecution = EPcExecutionMode.All };
        WorkflowDefinition definition = new("flow", "transfer", [new("pc", PcSelectionPorts.SelectNode, JsonSerializer.Deserialize<JsonElement>("""{"mode":"selected","hostIds":["host-a","host-b"]}""")), transfer], [new("pc", "value", "transfer", PcSelectionPorts.Execution)]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        List<RemoteExecutionRequest> requests = [];
        fixture.Dispatcher.Handler = (request, _) =>
        {
            requests.Add(request);
            fixture.Peers.Others = [Peer("replacement", "host-source")];
            return Task.FromResult(NodeResult.Success());
        };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(["a", "b"], requests.Select(request => request.TargetPeerId).Order(StringComparer.Ordinal));
        Assert.All(requests, request => Assert.Equal("source", request.Node.Settings.GetProperty("sourcePeer").GetString()));
        Assert.Equal("host-source", transfer.Settings.GetProperty("sourceHost").GetString());
        Assert.False(transfer.Settings.TryGetProperty("sourcePeer", out _));
    }

    /// <summary>転送元Hostが不在または未同期なら配送しません。</summary>
    /// <returns>副作用を開始しない結果です。</returns>
    [Theory]
    [InlineData("missing", EPeerStatus.Ready)]
    [InlineData("host-source", EPeerStatus.Syncing)]
    public async Task MissingSourceDoesNotDispatchAsync(string host, EPeerStatus status)
    {
        await using WorkflowFixture fixture = Create();
        fixture.Peers.Others = [Peer("source", "host-source") with { Status = status }];
        CompilationResult compiled = fixture.Compiler.Compile(new("flow", "transfer", [Transfer(host)], []));
        Assert.Empty(compiled.Issues);
        int dispatched = 0;
        fixture.Dispatcher.Handler = (_, _) =>
        {
            dispatched++;
            return Task.FromResult(NodeResult.Success());
        };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!);
        Assert.Equal(EOutcome.NotExecuted, report.Outcome);
        Assert.Equal(0, dispatched);
    }

    /// <summary>旧Peer設定は受理し、二重指定とクラスタ設定は拒否します。</summary>
    /// <returns>ノードの公開設定契約です。</returns>
    [Fact]
    public async Task LegacyPeerIsCompatibleButClusterSettingIsNotAllowedAsync()
    {
        await using WorkflowFixture fixture = Create();
        NodeDefinition legacy = Transfer() with { Settings = JsonSerializer.SerializeToElement(new { sourcePeer = "a", sourcePath = "/source", path = "/destination" }) };
        Assert.Empty(fixture.Compiler.Compile(new("flow", "transfer", [legacy], [])).Issues);
        NodeDefinition mixed = Transfer() with { Settings = JsonSerializer.SerializeToElement(new { sourcePeer = "a", sourceHost = "host-a", sourcePath = "/source", path = "/destination" }) };
        Assert.Contains(fixture.Compiler.Compile(new("flow", "transfer", [mixed], [])).Issues, issue => string.Equals(issue.Code, "file.source.required", StringComparison.Ordinal));
        NodeDefinition cluster = Transfer() with { Settings = JsonSerializer.SerializeToElement(new { sourceHost = "host-a", clusterId = "other", sourcePath = "/source", path = "/destination" }) };
        Assert.Null(fixture.Compiler.Compile(new("flow", "transfer", [cluster], [])).Workflow);
    }

    /// <summary>転送とPC選択の実装を使用します。</summary>
    /// <returns>試験用ランタイム。</returns>
    private static WorkflowFixture Create() => new([.. FileSystemNodeRegistration.Create(new FileSystemOptions([Path.GetTempPath()])), new PcSelectionNode()]);

    /// <summary>転送元PCだけを設定し、宛先は共通実行PC選択へ委ねます。</summary>
    /// <returns>保存用の転送定義。</returns>
    private static NodeDefinition Transfer(string host = "host-source") => new("transfer", "file.transfer", JsonSerializer.SerializeToElement(new { sourceHost = host, sourcePath = "/source", path = "/destination" }));

    /// <summary>同順位Peerの比較を再現します。</summary>
    /// <returns>通常参加の実体。</returns>
    private static PeerInfo Peer(string id, string host) => new(id, Guid.NewGuid(), host, "127.0.0.1", 0, EPeerStatus.Ready, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
}
