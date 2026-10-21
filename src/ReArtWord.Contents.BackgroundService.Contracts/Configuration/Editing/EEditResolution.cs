namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;

/// <summary>同じ項目の編集競合で人が選ぶ結果です。</summary>
public enum EEditResolution
{
    /// <summary>共有済み値を選びます。</summary>
    KeepCurrent,

    /// <summary>今回の編集値で上書きします。</summary>
    UseEdited,

    /// <summary>今回の編集保存を取り消します。</summary>
    Cancel,
}
