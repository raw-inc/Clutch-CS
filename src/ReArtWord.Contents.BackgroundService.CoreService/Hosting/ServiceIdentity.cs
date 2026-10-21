using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting;

/// <summary>外部更新機構がPID再利用と別ビルドを識別する実体情報です。</summary>
/// <param name="ProcessId">このOS プロセスのPID。</param>
/// <param name="ProcessStartedAt">OSが報告した起動時刻。</param>
/// <param name="BuildVersion">実行中アセンブリの情報版。</param>
internal sealed record ServiceIdentity(int ProcessId, DateTimeOffset ProcessStartedAt, string BuildVersion)
{
    /// <summary>現在プロセスから一度取得した実体です。</summary>
    public static ServiceIdentity Current
    {
        get;
    } = Capture();

    /// <summary>PIDだけでは判定せずOSの起動時刻も取得します。</summary>
    /// <returns>この実行中プロセスの情報です。</returns>
    // 抑制理由: BCLのProcess.StartTimeはDateTime固定のため、取得直後にUTCのDateTimeOffsetへ変換します。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "OSが報告するPID開始時刻をDateTimeOffsetで保持するためのBCL変換境界です。")]
    private static ServiceIdentity Capture()
    {
        using Process process = Process.GetCurrentProcess();
        return new ServiceIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()),
            typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
    }
}
