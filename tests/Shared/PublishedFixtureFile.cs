namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>一時ファイルから公開された試験記録を読み取ります。</summary>
internal static class PublishedFixtureFile
{
    /// <summary>末尾の空の引数を含め、記録した各行を読み取ります。</summary>
    /// <returns>公開済みファイルの全行です。</returns>
    internal static string[] ReadAllLines(string path)
    {
        using StreamReader reader = OpenText(path);
        List<string> lines = [];
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return [.. lines];
    }

    /// <summary>公開済みのPIDやウィンドウ情報を読み取ります。</summary>
    /// <returns>記録の本文です。</returns>
    internal static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        using StreamReader reader = OpenText(path);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>書込みは共有せず、公開時のリネームとは競合させません。</summary>
    /// <returns>呼出側で破棄する読取り専用ストリームです。</returns>
    private static StreamReader OpenText(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read | FileShare.Delete,
    });
}
