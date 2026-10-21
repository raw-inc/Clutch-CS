namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>書込み中の成果物を、排他を解除せずに観測します。</summary>
internal static class FileObservation
{
    /// <summary>共有違反だけを次の状態確認へ持ち越します。</summary>
    /// <returns>読み取った内容。書込み中で開けない場合はnullです。</returns>
    internal static async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            return null;
        }
    }
}
