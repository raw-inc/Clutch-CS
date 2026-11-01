namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>終了を確認した試験プロセスの専用配置を回収します。</summary>
internal static class TestProcessDirectory
{
    /// <summary>試験プロセス終了後のファイル保持に備え、待機を挟んで削除します。</summary>
    internal static void Delete(string directory)
    {
        Exception? pending = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(directory, true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 50ms間隔で最大20回試し、解放されなければ最後の失敗を返します。
                pending = exception;
                Thread.Sleep(50);
            }
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(pending!);
    }
}
