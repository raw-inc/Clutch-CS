namespace ReArtWord.Contents.BackgroundService.Contracts.Desktop;

/// <summary>画像やOS handleを送らず共有する、デスクトップのカーソル種類です。</summary>
public enum EDesktopCursorShape
{
    /// <summary>通常または識別できないアプリ固有のカーソルです。</summary>
    Default = 0,

    /// <summary>文字入力です。</summary>
    Text = 1,

    /// <summary>処理完了待ちです。</summary>
    Wait = 2,

    /// <summary>背景で処理中です。</summary>
    Progress = 3,

    /// <summary>十字です。</summary>
    Crosshair = 4,

    /// <summary>左上・右下方向のサイズ変更です。</summary>
    NorthWestSouthEast = 5,

    /// <summary>右上・左下方向のサイズ変更です。</summary>
    NorthEastSouthWest = 6,

    /// <summary>水平方向のサイズ変更です。</summary>
    EastWest = 7,

    /// <summary>垂直方向のサイズ変更です。</summary>
    NorthSouth = 8,

    /// <summary>移動です。</summary>
    Move = 9,

    /// <summary>操作不可です。</summary>
    NotAllowed = 10,

    /// <summary>リンク等の選択です。</summary>
    Link = 11,

    /// <summary>ヘルプ選択です。</summary>
    Help = 12,

    /// <summary>ホスト側で非表示です。</summary>
    Hidden = 13,
}
