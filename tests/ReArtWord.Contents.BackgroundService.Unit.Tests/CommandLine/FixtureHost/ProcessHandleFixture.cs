namespace ReArtWord.Contents.BackgroundService.CommandLineFixture;

/// <summary>プロセス終了時のOSによるファイル解放を、専用配置内で確認します。</summary>
internal static class ProcessHandleFixture
{
    /// <summary>全ファイルを開いてから起動完了を通知し、解放要求または強制終了を待ちます。</summary>
    /// <returns>通常終了時は0です。</returns>
    internal static async Task<int> RunAsync(string configuration, CancellationToken cancellationToken = default)
    {
        List<FileStream> files = [];
        try
        {
            // 強制終了時にもOSの後片付けを観測できるよう、少量のファイルを多数保持します。
            for (int index = 0; index < 2048; index++)
            {
                string path = configuration + "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".lock";
                files.Add(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
            }
            await File.WriteAllTextAsync(configuration + ".ready", "ready", cancellationToken).ConfigureAwait(false);
            string released = configuration + ".release";
            while (!File.Exists(released))
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
            return 0;
        }
        finally
        {
            foreach (FileStream file in files)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
