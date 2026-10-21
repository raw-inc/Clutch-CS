namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>ViewModelからwindowとOS操作を分離する対話境界です。</summary>
internal interface IInstallerInteraction
{
    /// <summary>フォルダーを選択します。</summary>
    /// <returns>選択した絶対pathまたはnullです。</returns>
    public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default);

    /// <summary>画像を選択します。</summary>
    /// <returns>選択した絶対pathまたはnullです。</returns>
    public Task<string?> PickImageAsync(CancellationToken cancellationToken = default);

    /// <summary>このPCの再起動を明示要求します。</summary>
    /// <returns>要求完了です。</returns>
    public Task RestartAsync(CancellationToken cancellationToken = default);

    /// <summary>導入したクライアントを開きます。</summary>
    public void OpenClient(string root);

    /// <summary>対話画面を閉じます。</summary>
    public void Close();
}
