using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.CommandLineFixture;

/// <summary>専用コピーの設定から、引数の記録・自明終了・外部終了待ちを行う実アプリケーションです。</summary>
internal static class ApplicationFixture
{
    /// <summary>各起動回を別ファイルへ記録し、終了コードまたは停止待ちを実現します。</summary>
    /// <returns>設定した実終了コードです。</returns>
    // 抑制理由: 実OSプロセスの開始時刻をUTCへ直ちに正規化して、cleanup時のPID再利用を防ぎます。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "OSの開始時刻はDateTimeでのみ提供されるため、取得直後にUTCへ変換します。")]
    internal static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        using JsonDocument settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "application-fixture.json"), cancellationToken).ConfigureAwait(false));
        using Process self = Process.GetCurrentProcess();
        string marker = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N") + ".started");
        string[] record = [Environment.ProcessId.ToString(CultureInfo.InvariantCulture), new DateTimeOffset(self.StartTime.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture), .. arguments];
        await File.WriteAllLinesAsync(marker + ".tmp", record, cancellationToken).ConfigureAwait(false);
        File.Move(marker + ".tmp", marker);
        int delay = settings.RootElement.GetProperty("delayMs").GetInt32();
        string windowKey = "window";
        if (OperatingSystem.IsWindows() && settings.RootElement.TryGetProperty(windowKey, out JsonElement window) && window.GetBoolean())
        {
            ApplicationFixtureWindow.Run(delay);
            return settings.RootElement.GetProperty("exitCode").GetInt32();
        }
        if (delay < 0)
        {
            string release = Path.Combine(AppContext.BaseDirectory, "release");
            while (!File.Exists(release))
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        return settings.RootElement.GetProperty("exitCode").GetInt32();
    }
}
