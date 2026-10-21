using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>WFDashboardの保存とValue編集を、実SQLiteと単一の隔離Peerで確認します。</summary>
internal sealed class WfDashboardFixture : IAsyncDisposable
{
    private readonly MediaTestFiles files = new();
    private readonly PeerCluster cluster;
    private readonly Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);

    /// <summary>外部通信や実行副作用のないノードだけを登録します。</summary>
    internal WfDashboardFixture()
    {
        Registry = new([new ConstantValueNode(), new TypeValueNode(), new ButtonNode(), new PassNode()]);
        Compiler = new(Registry);
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        cluster = new(new ClusterOptions(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", port, 1, true, files.Root), repository)
        {
            ReadinessCheck = _ => ValueTask.FromResult(true),
        };
        Store = new(repository, cluster, Compiler)
        {
            GetEffective = id => effective.GetValueOrDefault(id),
            ActivateHandler = (workflow, _) =>
            {
                effective[workflow.Definition.Id] = workflow;
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
        };
    }

    internal NodeRegistry Registry
    {
        get;
    }

    internal WorkflowCompiler Compiler
    {
        get;
    }

    internal WorkflowStore Store
    {
        get;
    }

    /// <summary>試験用の自己Peerを参加可能にします。</summary>
    /// <returns>開始の完了です。</returns>
    internal Task StartAsync(CancellationToken cancellationToken = default) => cluster.StartAsync(cancellationToken);

    /// <summary>自分が作成したPeerとDBだけを終了・削除します。</summary>
    /// <returns>全所有資源の終了です。</returns>
    public async ValueTask DisposeAsync()
    {
        await ((IAsyncDisposable)cluster).DisposeAsync().ConfigureAwait(false);
        files.Dispose();
    }
}
