using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>起動IDを一度取得し、待機中の時刻判定にはOSの経過時間を使います。</summary>
internal static partial class SystemBootObservation
{
    private static readonly Lazy<string?> identity = new(ReadIdentity);

    /// <summary>起動を確認できない場合はCore起動や現在日時で代用しません。</summary>
    /// <returns>取得できたOS起動回です。未対応・取得不能はnullです。</returns>
    internal static SystemBootSession? Read()
    {
        try
        {
            if (identity.Value is not { } id)
            {
                return null;
            }
            double milliseconds;
            if (OperatingSystem.IsWindows())
            {
                milliseconds = GetTickCount64();
            }
            else
            {
                string[] uptime = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                milliseconds = double.Parse(uptime[0], CultureInfo.InvariantCulture) * 1000;
            }
            if (!double.IsFinite(milliseconds) || milliseconds < 0 || TimeSpan.MaxValue.TotalMilliseconds <= milliseconds)
            {
                return null;
            }
            return new(id, TimeSpan.FromMilliseconds(milliseconds));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or OverflowException or EventLogException)
        {
            return null;
        }
    }

    /// <summary>同じOS起動に属する永続識別子だけを採用します。</summary>
    /// <returns>起動ログまたはカーネルが提供する識別子です。</returns>
    private static string? ReadIdentity()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsIdentity();
            }
            if (OperatingSystem.IsLinux())
            {
                string id = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
                return Guid.TryParse(id, out Guid parsed) ? parsed.ToString("D")
                                                          : null;
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EventLogException)
        {
            return null;
        }
    }

    /// <summary>OS自身の起動イベントを読み、時計補正やCore再起動でIDを変えません。</summary>
    /// <returns>同じ起動イベントを示すキーです。</returns>
    // 抑制理由: EventRecord.TimeCreatedの固定DateTime署名を、その場でUTCのDateTimeOffsetへ変換します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW171", Justification = "OS起動ログの固定署名をUTCへ変換する境界です。")]
    [SupportedOSPlatform("windows")]
    private static string? WindowsIdentity()
    {
        EventLogQuery query = new("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]") { ReverseDirection = true };
        using EventLogReader reader = new(query);
        using EventRecord? entry = reader.ReadEvent();
        if (entry?.RecordId is not { } record || entry.TimeCreated is not { } created)
        {
            return null;
        }
        DateTimeOffset timestamp = new(created.ToUniversalTime());
        return "windows:" + record.ToString(CultureInfo.InvariantCulture) + ":" + timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>.NET側のawake-time実装ではなく、休止期間も含むWindowsの経過時間を取得します。</summary>
    /// <returns>OS起動からのミリ秒です。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    private static partial ulong GetTickCount64();
}
