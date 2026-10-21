using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields.Types;

/// <summary>既存の型入力を、未指定・JSONスキーマ・手動型編集で共用します。</summary>
// 抑制理由: Razor生成コードの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせます。")]
public sealed partial class TypeInputField
{
    private bool open;
    private string? pendingSchema, problem;
    private static string SchemaKey => "schema";
    private string Mode => Value?.Json.ValueKind != JsonValueKind.Object ? "auto"
                                                                         : Value.Json.TryGetProperty(SchemaKey, out _) ? "schema"
                                                                                                                       : "manual";
    private string SchemaText => pendingSchema ?? (Value?.Json.ValueKind == JsonValueKind.Object && Value.Json.TryGetProperty(SchemaKey, out JsonElement schema) ? schema.GetRawText()
                                                                                                                                                                 : string.Empty);

    /// <summary>接続または直接入力された型です。</summary>
    [Parameter]
    public NodeValue? Value
    {
        get; set;
    }

    /// <summary>接続済み値を編集しません。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>新しい型、または自動に戻す未指定値です。</summary>
    [Parameter]
    public EventCallback<JsonElement> ValueChanged
    {
        get; set;
    }

    /// <summary>無効なスキーマ編集を保存禁止へ通知します。</summary>
    [Parameter]
    public EventCallback<string?> InvalidChanged
    {
        get; set;
    }

    /// <summary>変更先の方式に対応する明示値を作ります。</summary>
    /// <returns>型入力の変更です。</returns>
    private async Task ChangeModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        pendingSchema = problem = null;
        await InvalidChanged.InvokeAsync(null).ConfigureAwait(true);
        JsonElement value = mode switch
        {
            "manual" => JsonSerializer.SerializeToElement(new { type = "any" }),
            "schema" => JsonSerializer.SerializeToElement(JsonSchemaTypes.Read(JsonSerializer.SerializeToElement(true)), ContractJson.Options),
            _ => default,
        };
        await ValueChanged.InvokeAsync(value).ConfigureAwait(true);
        open = mode is "manual" or "schema";
    }

    /// <summary>元のJSONスキーマを検証用に保存し、型ヒントだけを別に抽出します。</summary>
    /// <returns>有効なJSONまたは未確定入力の通知です。</returns>
    private async Task SchemaAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        pendingSchema = text;
        try
        {
            JsonElement schema = JsonSerializer.Deserialize<JsonElement>(text);
            ValueTypeDefinition type = JsonSchemaTypes.Read(schema);
            await InvalidChanged.InvokeAsync(null).ConfigureAwait(true);
            await ValueChanged.InvokeAsync(JsonSerializer.SerializeToElement(type, ContractJson.Options)).ConfigureAwait(true);
            problem = null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            problem = "Enter a valid JSON Schema";
            await InvalidChanged.InvokeAsync(text).ConfigureAwait(true);
        }
    }
}
