using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Hosts;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Hosts;

/// <summary>固定されたOSのhostsファイルを退避し、同じディレクトリ内で原子的に置換します。</summary>
internal sealed class DeviceHostsFile(string path, string backupDirectory)
{
    /// <summary>OSごとの通常hostsだけを対象とします。</summary>
    internal static string SystemPath => OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts")
                                                                     : OperatingSystem.IsMacOS() ? "/private/etc/hosts"
                                                                                                 : "/etc/hosts";

    /// <summary>内容・文字コードを有限の読取りで確認します。</summary>
    /// <returns>実際に読み取れた設定です。</returns>
    internal async Task<DeviceSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ReadBytesAsync(cancellationToken).ConfigureAwait(false);
        return DeviceSnapshots.Create(new DeviceHostsState(Decode(bytes).Text));
    }

    /// <summary>別アプリによる変更も直前に確認し、元内容を保持してから置換します。</summary>
    /// <returns>置換を確認できた結果です。</returns>
    internal async Task<DeviceChangeResult> ApplyAsync(DeviceChangeRequest request, DeviceSnapshot before, CancellationToken cancellationToken = default)
    {
        string content = DeviceSnapshots.Read<DeviceHostsChange>(request.Change).Content;
        DeviceHostsRules.Validate(content);
        byte[] original = await ReadBytesAsync(cancellationToken).ConfigureAwait(false);
        (string current, Encoding encoding, byte[] preamble) = Decode(original);
        if (!string.Equals(before.Revision, DeviceSnapshots.Create(new DeviceHostsState(current)).Revision, StringComparison.Ordinal))
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.SettingsChanged");
        }
        string windowsNewline = "\r\n";
        string newline = current.Contains(windowsNewline, StringComparison.Ordinal) ? windowsNewline
                                                                                    : "\n";
        content = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
        string staged = path + ".contents-" + request.OperationId.ToString("N") + ".tmp";
        Directory.CreateDirectory(backupDirectory);
        string backup = Path.Combine(backupDirectory, request.OperationId.ToString("N") + ".hosts");
        await WriteAsync(backup, original, cancellationToken).ConfigureAwait(false);
        byte[] next = [.. preamble, .. encoding.GetBytes(content)];
        try
        {
            await WriteAsync(staged, next, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(staged, File.GetUnixFileMode(path));
            }
            byte[] latest = await ReadBytesAsync(cancellationToken).ConfigureAwait(false);
            if (!original.AsSpan().SequenceEqual(latest))
            {
                return new(request.OperationId, EOutcome.NotExecuted, "Device.SettingsChanged");
            }
            File.Replace(staged, path, null);
            return new(request.OperationId, EOutcome.Succeeded, CompletedSteps: ["hosts.backup", "hosts.replace"]);
        }
        finally
        {
            if (File.Exists(staged))
            {
                File.Delete(staged);
            }
        }
    }

    /// <summary>OSの名前解決ファイルから巨大な値やリンク先を取り込みません。</summary>
    /// <returns>取得時点の有限バイト列です。</returns>
    private async Task<byte[]> ReadBytesAsync(CancellationToken cancellationToken = default)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Device.HostsLinkUnsupported");
        }
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = stream.ConfigureAwait(false);
        if (262144 < stream.Length)
        {
            throw new IOException("Device.HostsTooLarge");
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.Length != bytes.Length)
        {
            throw new IOException("Device.SettingsChanged");
        }
        return bytes;
    }

    /// <summary>UTF-8とBOM付きUTF-16を区別し、変換不能を置換文字へ変えません。</summary>
    /// <returns>本文と元の文字コード・BOMです。</returns>
    private static (string Text, Encoding Encoding, byte[] Preamble) Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 255, 254, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 254, 255 }))
        {
            throw new IOException("Device.HostsEncodingUnsupported");
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 255, 254 }))
        {
            Encoding little = new UnicodeEncoding(false, false, true);
            return (little.GetString(bytes.AsSpan(2)), little, bytes[..2]);
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 254, 255 }))
        {
            Encoding big = new UnicodeEncoding(true, false, true);
            return (big.GetString(bytes.AsSpan(2)), big, bytes[..2]);
        }
        Encoding encoding = new UTF8Encoding(false, true);
        int length = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3
                                                                             : 0;
        return (encoding.GetString(bytes.AsSpan(length)), encoding, bytes[..length]);
    }

    /// <summary>既存の同名退避を上書きせず、内容を永続flushしてから閉じます。</summary>
    /// <returns>ファイルの書込み完了です。</returns>
    private static async Task WriteAsync(string destination, byte[] bytes, CancellationToken cancellationToken = default)
    {
        FileStream stream = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = stream.ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
