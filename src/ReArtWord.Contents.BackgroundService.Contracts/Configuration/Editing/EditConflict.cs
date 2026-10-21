using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;

/// <summary>値の追加・削除も区別する三者比較です。</summary>
/// <param name="Path">項目のJSON Pointer。</param>
/// <param name="Base">編集元の値。項目の存在はBaseExistsで区別します。</param>
/// <param name="Current">共有済み値。</param>
/// <param name="Edited">今回編集値。</param>
/// <param name="BaseExists">編集元項目の存在。値nullと削除を区別します。</param>
/// <param name="CurrentExists">現在項目の存在。</param>
/// <param name="EditedExists">今回編集後の項目の存在。</param>
public sealed record EditConflict(string Path, JsonElement? Base, JsonElement? Current, JsonElement? Edited, bool? BaseExists = null, bool? CurrentExists = null, bool? EditedExists = null);
