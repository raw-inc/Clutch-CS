using ReArtWord.Contents.BackgroundService.CoreService.Files;
using ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;
using ReArtWord.Contents.BackgroundService.CoreService.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;
using ReArtWord.Contents.BackgroundService.Nodes.AI;
using ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Nodes.Comparison;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Database;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;
using ReArtWord.Contents.BackgroundService.Nodes.Logic;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Nodes.Processes;
using ReArtWord.Contents.BackgroundService.Nodes.Resources;
using ReArtWord.Contents.BackgroundService.Nodes.Scheduling;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Nodes.State;
using ReArtWord.Contents.BackgroundService.Nodes.Targets;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Resources;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

/// <summary>標準ノードと、それぞれが使用する実行サービスを一か所で登録します。</summary>
internal static class RuntimeNodeRegistration
{
    /// <summary>通信・保存・監視の既存サービスを、標準ノードの依存先として接続します。</summary>
    /// <returns>実ノードのカタログと、待受の正常終了要求を登録する処理。</returns>
    internal static (NodeRegistry Registry, SourceEndBinding SourceEnd, FileTransferService Files) Create(CommonConfiguration configuration, PeerCluster cluster, SecretStore secrets,
        IWorkflowLog log, IWorkflowEvents events, IAvailableNodeStateStore state, IProcessService processes, IResourceService resources, NodePreviewStore previews, ScriptPackageStore packages, ScriptRuntimeRegistry runtimes)
    {
        FileTransferService files = new(configuration.Files);
        PeerFileTransfer filePeers = new(cluster, files);
        MediaOptions media = new(configuration.FfmpegPath, configuration.FfprobePath, configuration.Files);
        SourceEndBinding sourceEnd = new(cluster);
        List<INode> nodes = [.. ValuesNodeRegistration.Create(), .. DataNodeRegistration.Create(), .. ArithmeticNodeRegistration.Create(),
            .. ComparisonNodeRegistration.Create(), .. LogicNodeRegistration.Create(), .. ControlNodeRegistration.Create(events, log),
            new PreviewNode(previews, configuration.Files), new ProgressBarNode(previews), new PcSelectionNode(), new LowestResourcePcNode(), new EndSourceNode(sourceEnd.EndAsync),
            new ServiceStartedNode(cluster.LocalInstanceId, ServiceIdentity.Current.ProcessStartedAt),
            .. StateNodeRegistration.Create(state), new ToggleButtonNode(state), new SignalMergeNode("each", state), new SignalMergeNode("first", state), new SignalMergeNode("all", state),
            .. SchedulingNodeRegistration.Create(), .. CommunicationNodeRegistration.Create(secrets),
            .. DatabaseNodeRegistration.Create(secrets, configuration.Files),
            .. ScriptingNodeRegistration.Create(ScriptHostCommand.Current(), packages, runtimes),
            .. FileSystemNodeRegistration.Create(configuration.Files, filePeers, files), .. AiNodeRegistration.Create(secrets, new AiOptions(), media),
            .. MediaNodeRegistration.Create(media), .. ProcessNodeRegistration.Create(processes, events), .. ResourceNodeRegistration.Create(resources)];
        return (new NodeRegistry(nodes), sourceEnd, files);
    }
}
