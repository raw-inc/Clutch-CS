using System.Buffers.Binary;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>同じPCにある複数Core・クラスタでも、同時刻の予定停止を一度だけ受け付けます。</summary>
internal static class HostPowerScheduleLatch
{
    /// <summary>単調な最新時刻だけをOS操作前に保存し、PC再起動後にも再送しません。</summary>
    /// <returns>今回の予定を新しく受け付けたかです。</returns>
    internal static bool Claim(DateTimeOffset due) => Claim(DeviceHostStorage.DirectoryPath, due);

    /// <summary>PC設定へ触れず、分離した保存先でも同じ受付境界を使用します。</summary>
    /// <returns>実ファイル排他と耐久保存を確認できたかです。</returns>
    internal static bool Claim(string directory, DateTimeOffset due)
    {
        Directory.CreateDirectory(directory);
        using FileStream file = new(Path.Combine(directory, "scheduled-shutdown.bin"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 8, FileOptions.WriteThrough);
        Span<byte> bytes = stackalloc byte[8];
        if (file.Length != 0)
        {
            if (file.Length != bytes.Length)
            {
                throw new InvalidDataException("電源予定の受付位置が不正です。");
            }
            file.ReadExactly(bytes);
            long previous = BinaryPrimitives.ReadInt64BigEndian(bytes);
            if (previous < 0 || DateTimeOffset.MaxValue.UtcTicks < previous)
            {
                throw new InvalidDataException("電源予定の受付時刻が不正です。");
            }
            if (due.UtcTicks <= previous)
            {
                return false;
            }
        }
        BinaryPrimitives.WriteInt64BigEndian(bytes, due.UtcTicks);
        file.Position = 0;
        file.Write(bytes);
        file.Flush(true);
        return true;
    }
}
