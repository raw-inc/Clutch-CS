using System.Runtime.ExceptionServices;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>実ファイルの共有違反を観測してから、解放やキャンセルを行います。</summary>
internal static class InstallationFileLock
{
    /// <summary>この非同期操作の共有違反だけを受け取り、処理速度や例外文言に依存させません。</summary>
    /// <returns>占有を受けた操作の結果です。</returns>
    internal static async Task<T> RunAsync<T>(string directory, string name, Action<FileStream> onBlocked, Func<Task<T>> operation)
    {
        using FileStream reader = new(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.Read);
        AsyncLocal<bool> observing = new();
        int failures = 0;
        void Observe(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (observing.Value && args.Exception is IOException exception && (exception.HResult & 0xffff) is 5 or 32 or 33
                && Interlocked.Increment(ref failures) == 1)
            {
                onBlocked(reader);
            }
        }
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        observing.Value = true;
        try
        {
            T result = await operation().ConfigureAwait(false);
            Assert.True(0 < failures, "The operation must encounter the real Windows file lock.");
            return result;
        }
        finally
        {
            observing.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= Observe;
        }
    }
}
