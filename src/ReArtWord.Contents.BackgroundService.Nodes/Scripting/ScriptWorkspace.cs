using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>一回の解析・実行が所有する入力とemit結果だけを保持します。</summary>
internal sealed class ScriptWorkspace : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "contents-script", Guid.NewGuid().ToString("N"));

    /// <summary>実行ごとに衝突しない一時領域を作成します。</summary>
    internal ScriptWorkspace()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(root);
        }
        else
        {
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>今回だけの作業ディレクトリーです。</summary>
    internal string DirectoryPath => root;

    /// <summary>コードのファイル名としてユーザー値を使いません。</summary>
    /// <returns>固定ファイル名への入力保存です。</returns>
    internal async Task<string> WriteAsync<T>(T value, int maximumBytes, CancellationToken cancellationToken = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        if (maximumBytes < bytes.Length)
        {
            throw new InvalidDataException("script.inputLimit");
        }
        string path = Path.Combine(root, "request.json");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>ユーザーコードのコンパイル結果を一回分保存します。</summary>
    /// <returns>今回だけ読み込む絶対パスです。</returns>
    internal async Task<string> WriteAssemblyAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(root, "script.dll");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>製品が同梱する静的解析・実行ホストを展開します。</summary>
    /// <returns>ユーザーコードとは別のホストパスです。</returns>
    internal async Task<string> WritePythonHostAsync(CancellationToken cancellationToken = default)
    {
        using Stream resource = typeof(ScriptWorkspace).Assembly.GetManifestResourceStream("Contents.PythonScriptHost.py") ?? throw new InvalidOperationException("script.hostMissing");
        using StreamReader reader = new(resource, Encoding.UTF8);
        string path = Path.Combine(root, "host.py");
        await File.WriteAllTextAsync(path, await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>実行時のnpm環境へ解析器を混入させず、一回専用のホストを展開します。</summary>
    /// <returns>Node.jsで起動するES Moduleの絶対パスです。</returns>
    internal async Task<string> WriteJavaScriptHostAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.Combine(root, "vendor"));
        foreach (string name in new[] { "javascript-host.mjs", "javascript-contract.mjs", "javascript-types.mjs", "javascript-annotations.mjs", "acorn.mjs" })
        {
            using Stream resource = typeof(ScriptWorkspace).Assembly.GetManifestResourceStream("Contents." + name) ?? throw new InvalidOperationException("script.hostMissing");
            string directory = root;
            string vendor = "acorn.mjs";
            if (string.Equals(name, vendor, StringComparison.Ordinal))
            {
                directory = Path.Combine(root, "vendor");
            }
            using FileStream target = new(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await resource.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        return Path.Combine(root, "javascript-host.mjs");
    }

    /// <summary>所有パスだけを削除し、外部コードが保持したファイルで元結果を隠しません。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 停止不明の子プロセスが保持する一時領域は、誤って再利用せず残します。
        }
    }
}
