using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Runtime.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Detached;

/// <summary>離脱用途制限の既定・複合操作・実ファイル範囲を検証します。</summary>
public sealed class DetachedPolicyTests
{
    /// <summary>未知の拡張も既定Cでは使え、制限時だけ分類不能を明示します。</summary>
    [Fact]
    public void UnknownExtensionRequiresClassificationOnlyWhenRestricted()
    {
        NodeDescriptor descriptor = Descriptor("extension", "Custom");
        Assert.Null(DetachedOperationPolicy.GetProblem(descriptor, JsonSerializer.SerializeToElement(new { }), new()));
        Assert.NotNull(DetachedOperationPolicy.GetProblem(descriptor, JsonSerializer.SerializeToElement(new { }), new(FileChange: false)));
    }

    /// <summary>監視の暗黙自動復旧はプロセス操作であり、明示観測だけなら制限しません。</summary>
    [Fact]
    public void ProcessObservationAndAutomaticRecoveryAreDifferentPurposes()
    {
        DetachedOperationOptions options = new(ProcessChange: false);
        NodeDescriptor watch = Descriptor("process.watch", "Processes") with { Kind = ENodeKind.Source };
        Assert.NotNull(DetachedOperationPolicy.GetProblem(watch, JsonSerializer.SerializeToElement(new { }), options));
        Assert.Null(DetachedOperationPolicy.GetProblem(watch, JsonSerializer.SerializeToElement(new { autoRecover = false }), options));
        Assert.Null(DetachedOperationPolicy.GetProblem(Descriptor("process.observe", "Processes"), JsonSerializer.SerializeToElement(new { }), options));
        Assert.NotNull(DetachedOperationPolicy.GetProblem(Descriptor("process.start", "Processes"), JsonSerializer.SerializeToElement(new { }), options));
    }

    /// <summary>外部通信と変換の複合副作用を計算だけの許可へ読み替えません。</summary>
    [Fact]
    public void CommunicationAndMediaRequireTheirActualEffects()
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        Assert.NotNull(DetachedOperationPolicy.GetProblem(Descriptor("http.listen", "Communication"), settings, new(ExternalCommunication: false)));
        Assert.NotNull(DetachedOperationPolicy.GetProblem(Descriptor("media.video.convert", "Media"), settings, new(ProcessChange: false)));
        Assert.NotNull(DetachedOperationPolicy.GetProblem(Descriptor("media.video.convert", "Media"), settings, new(FileChange: false)));
        Assert.NotNull(DetachedOperationPolicy.GetProblem(Descriptor("ai.inspect", "AI"), JsonSerializer.SerializeToElement(new { mediaKind = "video" }), new(ProcessChange: false)));
    }

    /// <summary>実接続実装のfootprintを使い、ファイル変更禁止でも読取りを許します。</summary>
    /// <returns>実ファイル範囲の検証の完了。</returns>
    [Fact]
    public async Task RealFileFootprintsDistinguishReadFromWriteAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-detached-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "value.txt");
            await File.WriteAllTextAsync(path, "original").ConfigureAwait(true);
            IReadOnlyList<INode> nodes = FileSystemNodeRegistration.Create(new([directory], RecoveryDirectory: Path.Combine(directory, "recovery")));
            INode read = nodes.Single(node => string.Equals(node.Descriptor.Type, "file.read", StringComparison.Ordinal));
            INode write = nodes.Single(node => string.Equals(node.Descriptor.Type, "file.upsert", StringComparison.Ordinal));
            NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", "node", "peer",
                JsonSerializer.SerializeToElement(new { path }), new Dictionary<string, NodeValue>(), TimeProvider.System, TimeZoneInfo.Utc);
            Assert.Null(await DetachedOperationPolicy.GetProblemAsync(read, context, new(FileChange: false)).ConfigureAwait(true));
            Assert.NotNull(await DetachedOperationPolicy.GetProblemAsync(write, context, new(FileChange: false)).ConfigureAwait(true));
            Assert.NotNull(await DetachedOperationPolicy.GetProblemAsync(read, context, new(Compute: false)).ConfigureAwait(true));
            Assert.Equal("original", await File.ReadAllTextAsync(path).ConfigureAwait(true));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>用途以外を固定した公開メタデータです。</summary>
    /// <returns>独立したノード情報です。</returns>
    private static NodeDescriptor Descriptor(string type, string category) => NodeSupport.Describe(type, type, category, "{}", []);
}
