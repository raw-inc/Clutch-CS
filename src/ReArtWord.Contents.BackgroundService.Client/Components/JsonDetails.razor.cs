using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Components;

/// <summary>操作の実行結果を整形したJSONとして表示します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class JsonDetails
{
    /// <summary>整形して表示する実際の値です。</summary>
    [Parameter]
    public object? Value
    {
        get;
        set;
    }

    /// <summary>画面に表示する見出しまたは翻訳キーです。</summary>
    [Parameter]
    public string Title
    {
        get;
        set;
    } = "Exact JSON";

    private static readonly JsonSerializerOptions options = new(ContractJson.Options)
    {
        WriteIndented = true
    };
}
