namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>空の出力先への一回の追記を、書込みを妨げずに観測します。</summary>
internal static class FileObservation
{
    /// <summary>追記が始まるまではファイルを開かず、書込み中の共有違反は次の確認へ持ち越します。</summary>
    /// <returns>追記された内容。未作成、空、または書込み中の場合はnullです。</returns>
    internal static async Task<string?> ReadFirstAppendAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileInfo file = new(path);
        if (!file.Exists || file.Length == 0)
        {
            // 空ファイルを先に読み取ると、書込み側の排他オープンを妨げます。
            return null;
        }
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
