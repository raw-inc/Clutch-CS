using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>独立した保存先とUDP通信を使う、アーカイブ試験の環境。</summary>
internal sealed class ArchiveTestEnvironment : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    internal readonly ConcurrentQueue<Exception> Errors = [];

    /// <summary>試験専用の保存先とUDPピアを作成します。</summary>
    internal ArchiveTestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "ClutchArchiveTests", Guid.NewGuid().ToString("N"));
        JsonFileRepository repository = new(Path.Combine(Root, "state.json"));
        Repository = repository;
        using UdpClient port = new(new IPEndPoint(IPAddress.Loopback, 0));
        int number = ((IPEndPoint)port.Client.LocalEndPoint!).Port;
        Cluster = new(new ClusterOptions(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", number, 1, true, Root), repository)
        {
            ReadinessCheck = _ => ValueTask.FromResult(true),
        };
        NodeRegistry registry = new([new PassNode()]);
        registry.Dynamic = new SubworkflowCatalog(registry, () => Effective.Values.ToArray());
        Workflows = new(repository, Cluster, new WorkflowCompiler(registry))
        {
            GetEffective = id => Effective.GetValueOrDefault(id),
            ActivateHandler = (workflow, _) =>
            {
                Effective[workflow.Definition.Id] = workflow;
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
            DeactivateHandler = (id, token) =>
            {
                Effective.TryRemove(id, out _);
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
        };
        Common = new(repository, Cluster);
        Log = new(new("one", Path.Combine(Root, "logs")), repository, new SecretStore(repository));
        Archives = new(Root, repository, Cluster, Workflows, Common, Log);
        worker = Archives.RunAsync(Errors.Enqueue, stopping.Token);
    }

    internal string Root
    {
        get;
    }

    internal PeerCluster Cluster
    {
        get;
    }

    internal JsonFileRepository Repository
    {
        get;
    }

    internal WorkflowStore Workflows
    {
        get;
    }

    internal ClusterConfigurationStore Common
    {
        get;
    }

    internal WorkflowLogService Log
    {
        get;
    }

    internal ClusterArchiveService Archives
    {
        get;
    }

    internal ConcurrentDictionary<string, CompiledWorkflow> Effective
    {
        get;
    } = new(StringComparer.Ordinal);

    /// <summary>共通設定とログ保存を開始します。</summary>
    /// <returns>初期化の完了です。</returns>
    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Cluster.StartAsync(cancellationToken).ConfigureAwait(false);
        ClusterOperationalSettings settings = new("UTC", default, default, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
        await Common.InitializeAsync(settings, true, cancellationToken).ConfigureAwait(false);
        await Log.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>入力値で版を区別できる、試験用のWFを作成します。</summary>
    /// <returns>定数入力を持つWF。</returns>
    internal static WorkflowDefinition Definition(string id, int value) => new(id, id, [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }),
        new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(value) })], [], Enabled: true);

    /// <summary>バックグラウンド処理を停止し、試験用の保存先と通信を解放します。</summary>
    /// <returns>試験環境の終了処理。</returns>
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 試験が所有するworkerの通常終了です。
        }
        await Log.DisposeAsync().ConfigureAwait(false);
        Common.Dispose();
        await ((IAsyncDisposable)Cluster).DisposeAsync().ConfigureAwait(false);
        Archives.Dispose();
        stopping.Dispose();
        string parent = Path.Combine(Path.GetTempPath(), "ClutchArchiveTests") + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(Root, true);
        }
    }
}
