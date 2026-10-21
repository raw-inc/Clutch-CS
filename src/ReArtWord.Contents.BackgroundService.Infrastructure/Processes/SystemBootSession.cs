namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>Coreの開始や壁時計の変更と区別した、一回のOS起動と経過時間です。</summary>
/// <param name="Id">同じOS起動中は変化しない識別子です。</param>
/// <param name="Uptime">スリープ中も含むOS起動後の経過時間です。</param>
public readonly record struct SystemBootSession(string Id, TimeSpan Uptime);
