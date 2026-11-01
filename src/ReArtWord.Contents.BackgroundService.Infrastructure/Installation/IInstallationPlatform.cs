using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>配置管理からOSサービス操作を分離します。</summary>
public interface IInstallationPlatform
{
    /// <summary>配置前に導入形態に合うアクセス権を設定します。</summary>
    public void PrepareDirectory(string path, bool perUser, bool privateData = false, bool recursive = false);

    /// <summary>この利用者が利用できる既存クライアントを照会します。</summary>
    /// <returns>既存導入またはnullです。</returns>
    public Task<WebClientInstallation?> FindWebClientAsync(CancellationToken cancellationToken = default);

    /// <summary>クラスターとは独立したクライアントの配置先です。</summary>
    /// <returns>現在の権限範囲に対応する絶対pathです。</returns>
    public string WebClientRoot(bool perUser);

    /// <summary>指定したクライアント実行物だけを停止します。</summary>
    /// <returns>停止確認の完了です。</returns>
    public Task StopWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default);

    /// <summary>導入記録と稼働実体が一致するクライアントを確認します。</summary>
    /// <returns>更新前後の実体と版です。</returns>
    public Task<WebClientUpdateInfo> InspectWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default);

    /// <summary>既存の起動方式を維持して登録と起動を確認します。</summary>
    /// <returns>Webクライアントの起動確認です。</returns>
    public Task StartWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default);

    /// <summary>Webクライアントの独立した導入を記録します。</summary>
    /// <returns>自動起動の登録と起動確認です。</returns>
    public Task RegisterWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default);

    /// <summary>Webクライアントの導入記録を解除します。</summary>
    /// <returns>起動登録と導入記録の解除です。</returns>
    public Task UnregisterWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default);

    /// <summary>同名登録を上書きせず自動起動を登録します。</summary>
    /// <returns>登録完了です。</returns>
    public Task RegisterAsync(InstallationRecord record, CancellationToken cancellationToken = default);

    /// <summary>登録済みの実行物を開始します。</summary>
    /// <returns>開始要求の完了です。</returns>
    public Task StartAsync(InstallationRecord record, CancellationToken cancellationToken = default);

    /// <summary>対象の終了を確認します。</summary>
    /// <returns>停止完了です。</returns>
    public Task StopAsync(InstallationRecord record, ProcessIdentity? process, CancellationToken cancellationToken = default);

    /// <summary>対象実体を確保してから停止要求を公開し、同じ実体の終了を確認します。</summary>
    /// <returns>要求の公開と停止確認の完了です。</returns>
    public Task StopAsync(InstallationRecord record, ProcessIdentity? process, Func<CancellationToken, Task> requestStop, CancellationToken cancellationToken = default);

    /// <summary>所有する自動起動登録を削除します。</summary>
    /// <returns>削除完了です。</returns>
    public Task UnregisterAsync(InstallationRecord record, CancellationToken cancellationToken = default);

    /// <summary>配置した実体の通常参加を確認します。</summary>
    /// <returns>Readyの確認完了です。</returns>
    public Task VerifyAsync(InstallationRecord record, CancellationToken cancellationToken = default);
}
