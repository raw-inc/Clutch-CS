using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>終端プレビューの値・媒体・失効と配線境界を検証します。</summary>
public sealed class PreviewNodeTests
{
    /// <summary>一般値を表示用テキストへ取得し、出力ポートには返しません。</summary>
    /// <returns>実ノードと取得APIの契約確認です。</returns>
    [Theory]
    [InlineData("\"hello\"", "hello")]
    [InlineData("9007199254740993", "9007199254740993")]
    [InlineData("null", "null")]
    [InlineData("true", "true")]
    [InlineData("{\"age\":42}", "{\"age\":42}")]
    public async Task ValuesAreCapturedWithoutOutputs(string json, string expected)
    {
        NodePreviewStore store = new();
        PreviewNode node = new(store, new([]));
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json))));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Outputs);
        Assert.Empty(node.Descriptor.Outputs);
        Assert.True(node.Descriptor.IsTerminal);
        NodePreviewSnapshot snapshot = Assert.Single(store.Read(new("workflow", ["node"])));
        Assert.Equal("text/plain", snapshot.MediaType);
        Assert.Equal(expected, Encoding.UTF8.GetString(store.ReadChunk(new(snapshot.Id, 0)).Data.Span));
    }

    /// <summary>媒体の先頭情報で形式を識別し、所有バッファを共有しません。</summary>
    /// <returns>取得内容と媒体種別の確認です。</returns>
    [Theory]
    [InlineData("89504E470D0A1A0A", "image/png")]
    [InlineData("FFD8FF00", "image/jpeg")]
    [InlineData("524946460000000057415645", "audio/wav")]
    [InlineData("000000186674797069736F6D", "video/mp4")]
    public async Task BinaryMediaIsCaptured(string hex, string type)
    {
        NodePreviewStore store = new();
        byte[] bytes = Convert.FromHexString(hex);
        NodeResult result = await new PreviewNode(store, new([])).ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromBytes(bytes)));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        NodePreviewSnapshot snapshot = Assert.Single(store.Read(new("workflow", ["node"])));
        Assert.Equal(type, snapshot.MediaType);
        Array.Clear(bytes);
        Assert.Equal(hex, Convert.ToHexString(store.ReadChunk(new(snapshot.Id, 0)).Data.Span));
    }

    /// <summary>生成物は同じPeer・許可領域・同じ内容のファイルだけを読みます。</summary>
    /// <returns>実ファイルを使った読取りと拒否の確認です。</returns>
    [Fact]
    public async Task ArtifactRequiresOwnerAllowedPathAndUnchangedHash()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "image.png");
        byte[] bytes = Convert.FromHexString("89504E470D0A1A0A");
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
        try
        {
            NodePreviewStore store = new();
            PreviewNode node = new(store, new([directory]));
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            NodeValue artifact = NodeValue.FromObject(new { peerId = "peer", path, mediaType = "image/png", sha256 = hash });
            Assert.Equal(EOutcome.Succeeded, (await node.ExecuteAsync(NodeTestContext.Create(value: artifact))).Outcome);
            NodeValue foreign = NodeValue.FromObject(new { peerId = "other", path, mediaType = "image/png" });
            Assert.Equal("preview.peer", (await node.ExecuteAsync(NodeTestContext.Create(value: foreign))).ReasonCode);
            Assert.Equal("preview.peer", Assert.Single(store.Read(new("workflow", ["node"]))).Problem);
            Assert.Equal("preview.read", (await new PreviewNode(store, new([])).ExecuteAsync(NodeTestContext.Create(value: artifact))).ReasonCode);
            await File.WriteAllTextAsync(path, "changed").ConfigureAwait(true);
            Assert.Equal("preview.changed", (await node.ExecuteAsync(NodeTestContext.Create(value: artifact))).ReasonCode);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    /// <summary>巨大な入力と未対応形式を、前回成功値と区別します。</summary>
    /// <returns>保持する本文の制限確認です。</returns>
    [Fact]
    public async Task TextTruncatesAndOversizedMediaDoesNotRemain()
    {
        NodePreviewStore store = new();
        PreviewNode node = new(store, new([]));
        await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromObject(new string('a', 20000))));
        Assert.True(Assert.Single(store.Read(new("workflow", ["node"]))).Truncated);
        NodeValue large = NodeValue.FromBytes(new byte[NodePreviewStore.MaximumMediaBytes + 1]);
        Assert.Equal("preview.size", (await node.ExecuteAsync(NodeTestContext.Create(value: large))).ReasonCode);
        Assert.Equal(0, Assert.Single(store.Read(new("workflow", ["node"]))).Length);
        Assert.Equal("preview.format", (await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromBytes(new byte[] { 1, 2, 3 })))).ReasonCode);
    }

    /// <summary>更新されたID・期限切れ・不正範囲で別の本文を返しません。</summary>
    [Fact]
    public void StoreBoundsAgeCountAndChunkIdentity()
    {
        LogTestClock time = new();
        NodePreviewStore store = new(time);
        NodeExecutionContext context = NodeTestContext.Create();
        store.Publish(context, "text/plain", "first"u8.ToArray());
        NodePreviewSnapshot first = Assert.Single(store.Read(new("workflow", ["node"])));
        store.Publish(context, "text/plain", "second"u8.ToArray());
        Assert.Throws<KeyNotFoundException>(() => store.ReadChunk(new(first.Id, 0)));
        NodePreviewSnapshot second = Assert.Single(store.Read(new("workflow", ["node"])));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadChunk(new(second.Id, -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadChunk(new(second.Id, 0, 32769)));
        for (int index = 0; index < 129; index++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            store.Publish(context with { NodeId = index.ToString(System.Globalization.CultureInfo.InvariantCulture) }, "text/plain", new byte[] { 1 });
        }
        Assert.Empty(store.Read(new("workflow", ["0", "node"])));
        Assert.Single(store.Read(new("workflow", ["128"])));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(store.Read(new("workflow", ["128"])));
    }

    /// <summary>手書き定義でも共通result経路を終端から接続できません。</summary>
    [Theory]
    [InlineData("value")]
    [InlineData("result")]
    public void TerminalRejectsAllOutgoingWires(string port)
    {
        WorkflowCompiler compiler = new(new NodeRegistry([new PreviewNode(new(), new([])), new PassNode()]));
        WorkflowDefinition definition = new("flow", "flow", [new("preview", "control.preview", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(1) }),
            new("next", "control.pass", JsonSerializer.SerializeToElement(new { }))], [new("preview", port, "next", "value")]);
        Assert.Null(compiler.Compile(definition).Workflow);
    }
}
