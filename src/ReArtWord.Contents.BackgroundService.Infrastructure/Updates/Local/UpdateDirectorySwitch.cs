using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;

/// <summary>停止直後のOS ハンドル解放待ちと実ディレクトリ切替を分けます。</summary>
internal static class UpdateDirectorySwitch
{
    /// <summary>未適用を確認したWindowsのアクセス拒否・共有違反を有限時間待ちます。</summary>
    /// <returns>一度の成功したディレクトリ切替です。</returns>
    internal static Task MoveAsync(string source, string destination, UpdateLayout layout, CancellationToken cancellationToken = default)
        => MoveAsync(source, destination, path => layout.Validate(path, true), cancellationToken);

    /// <summary>CoreとClientHostで同じ有限のファイル占有待機を使います。</summary>
    /// <returns>境界確認後の一度のディレクトリ切替です。</returns>
    internal static async Task MoveAsync(string source, string destination, Action<string> validate, CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            validate(source);
            validate(destination);
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) is 5 or 32 or 33 && attempt < 19 && Directory.Exists(source) && !Directory.Exists(destination))
            {
                // 配下ファイルの占有もアクセス拒否(5)になります。元と先が未適用の状態の間だけ待ちます。
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
