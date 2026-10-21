using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>OSC引数の順序と型を、JSONを直接書かずに指定する編集欄です。</summary>
// 抑制理由: Razorが生成する公開コンポーネントと同じ宣言にします。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開コンポーネント型です。")]
public sealed partial class OscArgumentFields
{
    private static readonly string[] types = ["i", "f", "s", "b", "h", "d", "t", "boolean", "T", "F", "N"];
    private bool open;

    /// <summary>保存されている引数型です。未指定はパケット形式です。</summary>
    [Parameter]
    public JsonElement Value
    {
        get; set;
    }

    /// <summary>編集を禁止するかどうかです。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>引数型の変更を保存先へ通知します。</summary>
    [Parameter]
    public EventCallback<JsonElement> ValueChanged
    {
        get; set;
    }

    /// <summary>一つの引数型を変更します。</summary>
    /// <returns>編集内容の反映です。</returns>
    private Task ChangeAsync(int index, string? value, CancellationToken cancellationToken = default)
    {
        List<string> values = Read();
        values[index] = value ?? "i";
        return ValueChanged.InvokeAsync(JsonSerializer.SerializeToElement(values));
    }

    /// <summary>末尾へ整数引数を追加します。</summary>
    /// <returns>編集内容の反映です。</returns>
    private Task AddAsync(CancellationToken cancellationToken = default)
    {
        List<string> values = Read();
        values.Add("i");
        return ValueChanged.InvokeAsync(JsonSerializer.SerializeToElement(values));
    }

    /// <summary>指定位置の引数を取り除きます。</summary>
    /// <returns>編集内容の反映です。</returns>
    private Task RemoveAsync(int index, CancellationToken cancellationToken = default)
    {
        List<string> values = Read();
        values.RemoveAt(index);
        return ValueChanged.InvokeAsync(JsonSerializer.SerializeToElement(values));
    }

    /// <summary>編集中の配列を変更可能な一覧にします。</summary>
    /// <returns>未指定の場合は空の一覧です。</returns>
    private List<string> Read() => Contracts.Nodes.OscPorts.ArgumentTypes(Value)?.ToList() ?? [];
}
