using System.Text.Json.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;

/// <summary>JSONのnull値と編集による項目削除を区別します。</summary>
/// <param name="Exists">項目が存在するか。</param>
/// <param name="Data">項目値。</param>
internal sealed record EditValue(bool Exists, JsonNode? Data)
{
    /// <summary>別の文書へ取り込める値を作ります。</summary>
    /// <returns>所有権を分けた項目。</returns>
    public EditValue Copy() => new(Exists, Data?.DeepClone());
}
