using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

/// <summary>更新進捗の確定保存で、一時的なWindowsのファイル置換競合を吸収します。</summary>
public static class UpdateProgressFile
{
    /// <summary>置換直前に開く読取りで共有違反が生じた場合だけ、同じ確定記録を有限に再照会します。</summary>
    /// <returns>保存された本文です。破損JSONや消失を成功に置き換えません。</returns>
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        for (int attempt = 0; attempt < 39; attempt++)
        {
            try
            {
                return await ReadValueAsync<T>(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException
                && (exception.HResult & 0xffff) is 5 or 32 or 33)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        return await ReadValueAsync<T>(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>本文を一度書き終えてから置換だけを有限に再試行し、元の確定記録を維持します。</summary>
    /// <returns>保存の完了です。競合が解消しなければ元のOS例外を伝播します。</returns>
    public static async Task WriteAsync<T>(string path, T progress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = path + ".tmp";
        using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(output, progress, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        for (int attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(path))
                {
                    // Windowsの上書き移動はShare.Deleteを許す読取りとも競合するため、置換APIを使います。
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
                return;
            }
            catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException
                && (exception.HResult & 0xffff) is 5 or 32 or 33 or 1175 && attempt < 39 && File.Exists(temporary))
            {
                // 1175も元・準備ファイルの名前が保持される未適用エラーです。削除や権限変更で迂回しません。
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>置換を妨げない共有権限で、一つのファイル実体を最後まで読みます。</summary>
    /// <returns>同じ版から読み切った進捗です。</returns>
    private static async Task<T?> ReadValueAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<T>(input, ContractJson.Options, cancellationToken).ConfigureAwait(false);
    }
}
