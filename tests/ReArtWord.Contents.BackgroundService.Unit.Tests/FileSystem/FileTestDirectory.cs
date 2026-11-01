using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;

/// <summary>試験専用rootとファイル能力を所有します。</summary>
internal sealed class FileTestDirectory : IDisposable
{
    /// <summary>他の試験と独立した許可ルートを作成します。</summary>
    internal FileTestDirectory(EFileConflictPolicy conflict = EFileConflictPolicy.Fail, EFileInternalFailurePolicy failure = EFileInternalFailurePolicy.Keep)
    {
        Root = Path.Combine(Path.GetTempPath(), "ContentsFileTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Options = new([Root], conflict, failure);
        Service = new(Options);
        Nodes = FileSystemNodeRegistration.Create(Options, transferService: Service);
    }

    /// <summary>試験専用許可ルートです。</summary>
    internal string Root
    {
        get;
    }

    /// <summary>共通設定です。</summary>
    internal FileSystemOptions Options
    {
        get;
    }

    /// <summary>共有サービスです。</summary>
    internal FileTransferService Service
    {
        get;
    }

    /// <summary>登録能力です。</summary>
    internal IReadOnlyList<INode> Nodes
    {
        get;
    }

    /// <summary>指定した有限操作を取得します。</summary>
    /// <returns>操作ノードです。</returns>
    internal IActionNode Action(string type)
    {
        return (IActionNode)Nodes.Single(node => string.Equals(node.Descriptor.Type, type, StringComparison.Ordinal));
    }

    /// <summary>独立した一件を作成します。</summary>
    /// <returns>固定された設定と値です。</returns>
    internal static NodeExecutionContext Context(object settings, NodeValue? input = null)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal);
        if (input is not null)
        {
            inputs["value"] = input;
        }
        return new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", "node", "peer", JsonSerializer.SerializeToElement(settings), inputs, TimeProvider.System);
    }

    /// <summary>Windowsでは一般権限のjunction、それ以外ではsymlinkを作成します。</summary>
    /// <returns>link作成完了です。</returns>
    internal static async Task CreateDirectoryLinkAsync(string path, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            ProcessStartInfo start = new("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(target);
            using Process process = Process.Start(start) ?? throw new IOException("junction作成を開始できません。");
            await ProcessCompletion.WaitAsync(process).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new IOException("junctionを作成できません。");
            }
        }
        else
        {
            Directory.CreateSymbolicLink(path, target);
        }
    }

    /// <summary>自分が作ったrootだけを片付けます。</summary>
    public void Dispose()
    {
        string parent = Path.Combine(Path.GetTempPath(), "ContentsFileTests") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("試験rootが不正です。");
        }
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }
}
