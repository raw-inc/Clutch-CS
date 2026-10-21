using System.Globalization;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>同じPeer実体・候補版に固定した一覧の開始位置を確認します。</summary>
internal static class ManagementPageCursor
{
    /// <summary>別の候補集合へ継続位置を流用しません。</summary>
    /// <returns>確認した開始位置です。</returns>
    public static int Read(string? cursor, string fingerprint, int count)
    {
        if (cursor is null)
        {
            return 0;
        }
        string[] parts = cursor.Split(':');
        if (parts.Length != 2 || !string.Equals(parts[0], fingerprint, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int offset) || offset < 0 || count <= offset)
        {
            throw new ManagementInputException("候補一覧が変わりました。先頭から取得してください。");
        }
        return offset;
    }

    /// <summary>最後まで取得した一覧へ空の継続ページを作りません。</summary>
    /// <returns>次の位置、完了時はnullです。</returns>
    public static string? Next(string fingerprint, int next, int count)
    {
        if (next < count)
        {
            return fingerprint + ":" + next.ToString(CultureInfo.InvariantCulture);
        }
        return null;
    }
}
