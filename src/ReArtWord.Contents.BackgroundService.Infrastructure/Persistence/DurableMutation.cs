using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>版が一致する場合だけ保存する一項目です。</summary>
/// <param name="Scope">保存用途。</param>
/// <param name="Key">用途内の識別子。</param>
/// <param name="ExpectedVersion">期待する版。未作成はnull。</param>
/// <param name="Value">新値。nullは削除。</param>
/// <param name="ResolveVersions">明示解消で確認した全候補版。通常CASは指定しません。</param>
/// <param name="CausalVersion">複製元が固定した因果情報です。呼出元の変更内容指紋には含めません。</param>
public sealed record DurableMutation(string Scope, string Key, string? ExpectedVersion, NodeValue? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ResolveVersions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CausalVersion = null);
