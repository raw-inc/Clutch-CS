using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.State;

/// <summary>比較更新に使用する状態値と版です。</summary>
/// <param name="Version">値の版。</param>
/// <param name="Value">保存した値。</param>
/// <param name="LocalOnly">離脱側の未共有値です。</param>
public sealed record VersionedState(
    string Version,
    NodeValue Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LocalOnly = false);
