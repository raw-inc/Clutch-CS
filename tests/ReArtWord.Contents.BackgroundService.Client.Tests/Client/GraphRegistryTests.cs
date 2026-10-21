using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>カタログの仮想ポートを実装契約からのみ公開します。</summary>
public sealed class GraphRegistryTests
{
    /// <summary>スキーマに関わらず実補償ノードだけがtransaction出力を持ちます。</summary>
    [Fact]
    public void CatalogUsesImplementedFileCapability()
    {
        NodeRegistry registry = new([new Ordinary(), new Compensable()]);
        NodeDescriptor file = registry.Descriptors.Single(item => string.Equals(item.Type, "file", StringComparison.Ordinal));
        Assert.Equal(EPortValueKind.Json, file.Outputs.Single(item => string.Equals(item.Name, "fileTransaction", StringComparison.Ordinal)).ValueKind);
        Assert.DoesNotContain(registry.Descriptors.Single(item => string.Equals(item.Type, "ordinary", StringComparison.Ordinal)).Outputs, port => port.Name is "fileTransaction");
        Assert.Empty(registry.Find("file")!.Descriptor.Outputs);
    }

    /// <summary>rollbackAllに似たスキーマだけでは補償能力になりません。</summary>
    private class Ordinary : IActionNode
    {
        /// <summary>実装側の元宣言です。</summary>
        public virtual NodeDescriptor Descriptor => new("ordinary", "Ordinary", "Data", "", ENodeKind.Action, EExecutionLocation.Independent, [], [], [], JsonSerializer.Deserialize<JsonElement>("{\"properties\":{\"failurePolicy\":{\"enum\":[\"rollbackAll\"]}}}"));

        /// <summary>表示試験では設定問題を作りません。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>本試験はノードを実行しません。</summary>
        /// <returns>正常結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => ValueTask.FromResult(NodeResult.Success());
    }

    /// <summary>スキーマではなく実補償インターフェースを提供します。</summary>
    private sealed class Compensable : Ordinary, ICompensableFileNode
    {
        /// <summary>空スキーマの実ファイル補償ノードです。</summary>
        public override NodeDescriptor Descriptor => base.Descriptor with { Type = "file", SettingsSchema = JsonSerializer.Deserialize<JsonElement>("{}") };

        /// <summary>表示だけの試験で退避を開始しません。</summary>
        /// <returns>未対応です。</returns>
        public Task<FileCompensationToken> CaptureAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>表示だけの試験で復元を開始しません。</summary>
        /// <returns>未対応です。</returns>
        public Task RestoreAsync(FileCompensationToken token, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>表示だけの試験で解放を開始しません。</summary>
        /// <returns>未対応です。</returns>
        public Task ReleaseAsync(FileCompensationToken token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
