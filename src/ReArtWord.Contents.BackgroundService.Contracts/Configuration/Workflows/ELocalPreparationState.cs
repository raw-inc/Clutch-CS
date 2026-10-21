namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>共有済み設定をローカル利用するための準備状態です。</summary>
public enum ELocalPreparationState
{
    /// <summary>確認済みの内容をローカル採用しました。</summary>
    Prepared,

    /// <summary>該当する共有設定がまだ一度も作成されていません。</summary>
    Empty,

    /// <summary>存在する状態の検証またはローカル採用を確認できません。</summary>
    Unavailable,
}
