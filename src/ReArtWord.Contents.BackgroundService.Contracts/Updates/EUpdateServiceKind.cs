namespace ReArtWord.Contents.BackgroundService.Contracts.Updates;

/// <summary>更新前後に操作するサービスの明示種別です。</summary>
public enum EUpdateServiceKind
{
    /// <summary>OS登録を使わず指定プロセスを停止・起動します。</summary>
    DirectProcess,

    /// <summary>Windows Service Controllerを使用します。</summary>
    WindowsService,

    /// <summary>systemdの明示unitを使用します。</summary>
    Systemd,

    /// <summary>launchdの明示サービスを使用します。</summary>
    Launchd,
}
