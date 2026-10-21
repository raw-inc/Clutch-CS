namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>アプリケーションの明示管理境界です。</summary>
public enum EProcessMode
{
    /// <summary>監視だけを許可し、人契機でもプロセスを操作しません。</summary>
    ObserveOnly,

    /// <summary>登録された範囲で起動・停止・復旧を許可します。</summary>
    Managed,
}
