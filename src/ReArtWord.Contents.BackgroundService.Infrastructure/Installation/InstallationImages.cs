using ReArtWord.Contents.BackgroundService.Contracts.Installation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>表示に使う画像の検証とコピーを行います。</summary>
public static class InstallationImages
{
    /// <summary>指定された画像を設定フォルダーへ保存します。</summary>
    /// <returns>保存先を参照する表示設定です。</returns>
    public static async Task<ClientAppearance> CopyAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        string directory = Path.Combine(record.Settings.Root, "config");
        string? icon = await CopyImageAsync(record.Settings.Appearance.IconPath, directory, "icon", cancellationToken).ConfigureAwait(false);
        string? favicon = await CopyImageAsync(record.Settings.Appearance.FaviconPath, directory, "favicon", cancellationToken).ConfigureAwait(false);
        return record.Settings.Appearance with { IconPath = icon, FaviconPath = favicon };
    }

    /// <summary>プレビュー用に、サイズと形式を検証した画像を読み込みます。</summary>
    /// <returns>PNGまたはICOの画像データです。</returns>
    public static byte[] Read(string path)
    {
        using FileStream stream = Open(path);
        byte[] bytes = new byte[Length(stream)];
        stream.ReadExactly(bytes);
        _ = Extension(bytes);
        return bytes;
    }

    /// <summary>未指定の画像はコピーせず、指定された画像だけを保存します。</summary>
    /// <returns>保存先、またはnullです。</returns>
    private static async Task<string?> CopyImageAsync(string? source, string directory, string name, CancellationToken cancellationToken = default)
    {
        if (source is null)
        {
            return null;
        }
        byte[] bytes;
        using (FileStream stream = Open(source))
        {
            bytes = new byte[Length(stream)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        string target = Path.Combine(directory, name + Extension(bytes));
        await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        return target;
    }

    /// <summary>絶対パスで指定された画像を開きます。</summary>
    /// <returns>呼び出し元で閉じる読み取り専用ストリームです。</returns>
    private static FileStream Open(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Choose a local PNG or ICO image smaller than 5 MiB.");
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }

    /// <summary>読み込む長さを固定し、上限を超える画像を拒否します。</summary>
    /// <returns>上限内の読み込みサイズです。</returns>
    private static int Length(FileStream stream)
    {
        long length = stream.Length;
        if (InstallationDefaults.MaximumImageBytes < length)
        {
            throw new InvalidDataException("Choose a local PNG or ICO image smaller than 5 MiB.");
        }
        return (int)length;
    }

    /// <summary>拡張子に依存せず画像の形式を確認します。</summary>
    /// <returns>確認した形式の拡張子です。</returns>
    private static string Extension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return ".png";
        }
        if (bytes.StartsWith(new byte[] { 0, 0, 1, 0 }))
        {
            return ".ico";
        }
        throw new InvalidDataException("The image is not PNG or ICO.");
    }
}
