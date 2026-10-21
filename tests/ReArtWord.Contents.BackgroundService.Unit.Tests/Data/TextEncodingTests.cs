using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>文字コードとBOM方針を、独立した既知バイト列と実ノードの入出力で確認します。</summary>
public sealed class TextEncodingTests
{
    /// <summary>Unicodeの各BOMを判別し、BOMだけを本文から除去します。</summary>
    /// <returns>バイト順とUTF-32優先の確認です。</returns>
    [Theory]
    [InlineData("EFBBBFE697A5E69CACE8AA9E")]
    [InlineData("FFFEE5652C679E8A")]
    [InlineData("FEFF65E5672C8A9E")]
    [InlineData("FFFE0000E56500002C6700009E8A0000")]
    [InlineData("0000FEFF000065E50000672C00008A9E")]
    public async Task DetectsUnicodeBomWithoutLeakingItIntoTextAsync(string hex)
    {
        NodeResult result = await DecodeAsync(hex).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("日本語", result.Outputs["value"].GetString());
    }

    /// <summary>各文字コードを推測やOS既定値なしで両方向へ変換します。</summary>
    /// <returns>既知バイト列と完全に一致する符号化と復号です。</returns>
    [Theory]
    [InlineData("utf-8", "日本語", "E697A5E69CACE8AA9E")]
    [InlineData("utf-16le", "日本語", "E5652C679E8A")]
    [InlineData("utf-16be", "日本語", "65E5672C8A9E")]
    [InlineData("utf-32le", "日本語", "E56500002C6700009E8A0000")]
    [InlineData("utf-32be", "日本語", "000065E50000672C00008A9E")]
    [InlineData("shift_jis", "日本語", "93FA967B8CEA")]
    [InlineData("euc-jp", "日本語", "C6FCCBDCB8EC")]
    [InlineData("iso-2022-jp", "日本語", "1B2442467C4B5C386C1B2842")]
    [InlineData("us-ascii", "A\r\n B\t", "410D0A204209")]
    [InlineData("iso-8859-1", "café", "636166E9")]
    [InlineData("windows-1252", "€", "80")]
    public async Task ExplicitEncodingsUseExactBytesAsync(string encoding, string text, string hex)
    {
        string settings = JsonSerializer.Serialize(new { encoding, bom = "none" });
        NodeResult decoded = await DecodeAsync(hex, settings).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, decoded.Outcome);
        Assert.Equal(text, decoded.Outputs["value"].GetString());
        NodeResult encoded = await new TextEncodeNode().ExecuteAsync(NodeTestContext.Create(settings, NodeValue.FromObject(text))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, encoded.Outcome);
        Assert.Equal(Convert.FromHexString(hex), encoded.Outputs["value"].Binary.ToArray());
    }

    /// <summary>UTF-8既定ではBOMを追加せず、UTF-16/32の自動指定では追加します。</summary>
    /// <returns>空文字列を含むBOM出力方針です。</returns>
    [Theory]
    [InlineData("{}", "A", "41")]
    [InlineData("{}", "", "")]
    [InlineData("{\"bom\":\"present\"}", "A", "EFBBBF41")]
    [InlineData("{\"bom\":\"present\"}", "", "EFBBBF")]
    [InlineData("{\"encoding\":\"utf-16le\"}", "A", "FFFE4100")]
    [InlineData("{\"encoding\":\"utf-32be\"}", "A", "0000FEFF00000041")]
    [InlineData("{\"encoding\":\"auto\"}", "A", "41")]
    [InlineData("{\"encoding\":\"shift_jis\"}", "A", "41")]
    public async Task EncodingBomPolicyHasDeterministicDefaultsAsync(string settings, string text, string hex)
    {
        NodeResult result = await new TextEncodeNode().ExecuteAsync(NodeTestContext.Create(settings, NodeValue.FromObject(text))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(Convert.FromHexString(hex), result.Outputs["value"].Binary.ToArray());
    }

    /// <summary>先頭除去を無効化した場合と、明示UTF-16の共通接頭辞を確認します。</summary>
    /// <returns>本文を誤って削らない復号です。</returns>
    [Theory]
    [InlineData("EFBBBF410AEFBBBF", "{}", "A\n\uFEFF")]
    [InlineData("EFBBBF41", "{\"bom\":\"none\"}", "\uFEFFA")]
    [InlineData("FFFE00004100", "{\"encoding\":\"utf-16le\"}", "\0A")]
    [InlineData("EFBBBF", "{}", "")]
    [InlineData("", "{}", "")]
    public async Task PreservesBodyAndExplicitByteOrderAsync(string hex, string settings, string expected)
    {
        NodeResult result = await DecodeAsync(hex, settings).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Outputs["value"].GetString());
    }

    /// <summary>不正列・途中切れ・BOM矛盾を成功や代替文字へ丸めません。</summary>
    /// <returns>原因ごとの明示失敗です。</returns>
    [Theory]
    [InlineData("41", "{\"bom\":\"present\"}", "text.decode.bom.missing")]
    [InlineData("FFFE4100", "{\"encoding\":\"utf-8\"}", "text.decode.bom.conflict")]
    [InlineData("EFBBBF41", "{\"encoding\":\"shift_jis\"}", "text.decode.bom.conflict")]
    [InlineData("93FA967B8CEA", "{}", "text.decode.bytes.invalid")]
    [InlineData("C080", "{}", "text.decode.bytes.invalid")]
    [InlineData("F09F98", "{}", "text.decode.bytes.invalid")]
    [InlineData("410042", "{\"encoding\":\"utf-16le\"}", "text.decode.bytes.invalid")]
    [InlineData("00D8", "{\"encoding\":\"utf-16le\"}", "text.decode.bytes.invalid")]
    [InlineData("00001100", "{\"encoding\":\"utf-32le\"}", "text.decode.bytes.invalid")]
    [InlineData("81", "{\"encoding\":\"shift_jis\"}", "text.decode.bytes.invalid")]
    [InlineData("A4", "{\"encoding\":\"euc-jp\"}", "text.decode.bytes.invalid")]
    [InlineData("FF", "{\"encoding\":\"us-ascii\"}", "text.decode.bytes.invalid")]
    public async Task InvalidBytesAndBomAreFailuresAsync(string hex, string settings, string reason)
    {
        NodeResult result = await DecodeAsync(hex, settings).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Empty(result.Outputs);
    }

    /// <summary>文字を表せない符号化とBOMを持たない形式へのBOM要求は失敗します。</summary>
    /// <returns>暗黙の代替文字が出力されない確認です。</returns>
    [Theory]
    [InlineData("shift_jis", "none", "😀", "text.encode.characters.invalid")]
    [InlineData("us-ascii", "auto", "日本語", "text.encode.characters.invalid")]
    [InlineData("shift_jis", "present", "A", "text.encode.bom.unsupported")]
    public async Task UnrepresentableEncodingDoesNotLoseTextAsync(string encoding, string bom, string text, string reason)
    {
        NodeResult result = await new TextEncodeNode().ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { encoding, bom }), NodeValue.FromObject(text))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Empty(result.Outputs);
    }

    /// <summary>欠落・型違い・不正設定・取消を変換成功として扱いません。</summary>
    /// <returns>実行境界の確認です。</returns>
    [Fact]
    public async Task InputAndCancellationBoundariesAreExplicitAsync()
    {
        TextDecodeNode decode = new();
        TextEncodeNode encode = new();
        Assert.Equal(EOutcome.NotExecuted, (await decode.ExecuteAsync(NodeTestContext.Create()).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await encode.ExecuteAsync(NodeTestContext.Create()).ConfigureAwait(true)).Outcome);
        Assert.Equal("text.decode.input.type", (await decode.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromObject("path.txt"))).ConfigureAwait(true)).ReasonCode);
        Assert.Equal("text.encode.input.type", (await encode.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromObject(42))).ConfigureAwait(true)).ReasonCode);
        Assert.Equal(EOutcome.NotExecuted, (await DecodeAsync("41", "{\"encoding\":\"unknown\"}").ConfigureAwait(true)).Outcome);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decode.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromBytes(new byte[] { 65 })), cancelled.Token).AsTask()).ConfigureAwait(true);
    }

    /// <summary>ファイル読込から文字列、再符号化後のバイト列まで型を保ちます。</summary>
    [Fact]
    public void CatalogAndInferenceExposeConcreteTypes()
    {
        TextDecodeNode decode = new();
        TextEncodeNode encode = new();
        WorkflowDefinition document = new("flow", "Text", [new("decode", decode.Descriptor.Type, JsonSerializer.SerializeToElement(new { })), new("encode", encode.Descriptor.Type, JsonSerializer.SerializeToElement(new { }))],
            [new("decode", "value", "encode", "value")]);
        WorkflowTypeInference inference = new(document, [decode.Descriptor, encode.Descriptor]);
        Assert.Equal("string", inference.PortType("decode", "value", true)!.Type);
        Assert.Equal("binary", inference.PortType("encode", "value", true)!.Type);
        Assert.True(decode.Descriptor.IsValueOperation);
        Assert.True(encode.Descriptor.IsValueOperation);
        Assert.Contains(DataNodeRegistration.Create(), node => node.Descriptor.Type is "text.decode");
        Assert.Contains(DataNodeRegistration.Create(), node => node.Descriptor.Type is "text.encode");
    }

    /// <summary>テスト側で決めたバイト列を通常のノード入力へ渡します。</summary>
    /// <returns>実ノードの復号結果です。</returns>
    private static ValueTask<NodeResult> DecodeAsync(string hex, string settings = "{}")
        => new TextDecodeNode().ExecuteAsync(NodeTestContext.Create(settings, NodeValue.FromBytes(Convert.FromHexString(hex))));
}
