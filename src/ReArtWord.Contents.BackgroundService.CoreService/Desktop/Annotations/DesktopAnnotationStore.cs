using System.IO.MemoryMappedFiles;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Annotations;

/// <summary>認証済みworkerの指示表示を、Core境界を越えて有限の揮発領域で共有します。</summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopAnnotationStore : IDisposable
{
    private static int Clients => 32;
    private static int Capacity => 512;
    private static int Header => 64;
    private static int SlotSize => 64;
    private static int PointStart => Header + Clients * SlotSize;
    private static int PointSize => 48;
    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor data;
    private readonly Mutex gate;
    private readonly long identity = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) | 1;
    private int slot = -1;
    private long lastPoint;
    private long stroke;
    private bool drawing;
    private bool disposed;

    /// <summary>共有名はsessionで分離され、最後の接続が終わるとOSが全領域を回収します。</summary>
    // 抑制理由: constructor失敗時の資源回収後、元のOS例外を維持します。
    [SuppressMessage("SourceQuality", "RAW330", Justification = "部分的に取得した共有資源を回収して同じ例外を維持します。")]
    internal DesktopAnnotationStore(string? group = null)
    {
        (map, gate) = WindowsAnnotationMemory.Open(group ?? "Local\\ReArtWord.Contents.Desktop.Annotations.v1", PointStart + Capacity * PointSize);
        try
        {
            data = map.CreateViewAccessor();
        }
        catch
        {
            map.Dispose();
            gate.Dispose();
            throw;
        }
    }

    /// <summary>入力操作とは独立に位置を更新します。phase=0は退出、1はhover、2はdragです。</summary>
    internal void Publish(int x, int y, int phase, int line, long now)
    {
        if (!Enter())
        {
            return;
        }
        try
        {
            int address = Register(now);
            if (address < 0)
            {
                return;
            }
            data.Write(address + 16, now);
            data.Write(address + 24, x);
            data.Write(address + 28, y);
            data.Write(address + 32, phase != 0);
            if (phase != 2)
            {
                drawing = false;
            }
            if (phase == 2 && (!drawing || stroke != line || 40 <= now - lastPoint))
            {
                long sequence = Math.Max(0, data.ReadInt64(8)) + 1;
                int index = (int)(sequence % Capacity);
                long[] point = [sequence, data.ReadInt64(address + 8), line, x, y, now];

                // 別viewerの同じline番号を結合しないよう、色(生存中は固有)も描画側のkeyにします。
                data.WriteArray(PointStart + index * PointSize, point, 0, point.Length);
                data.Write(8, sequence);
                stroke = line;
                drawing = true;
                lastPoint = now;
            }
            data.Write(0, data.ReadInt64(0) + 1);
        }
        finally
        {
            gate.ReleaseMutex();
        }
    }

    /// <summary>生存を更新し、最新cursorとまだ消えていない点だけを読む短いtransactionです。</summary>
    /// <returns>競合時は次回へ延期します。</returns>
    internal DesktopAnnotationSnapshot? Read(long now)
    {
        if (!Enter())
        {
            return null;
        }
        try
        {
            int address = Register(now);
            if (address < 0)
            {
                return null;
            }
            data.Write(address + 16, now);
            int owner = -1;
            List<long[]> cursors = [];
            for (int index = 0; index < Clients; index++)
            {
                int current = Header + index * SlotSize;
                if (data.ReadInt64(current) == 0 || 2000 < now - data.ReadInt64(current + 16))
                {
                    if (data.ReadBoolean(current + 32))
                    {
                        data.Write(current + 32, false);
                        data.Write(0, data.ReadInt64(0) + 1);
                    }
                    continue;
                }
                if (owner < 0)
                {
                    owner = index;
                }
                if (data.ReadBoolean(current + 32))
                {
                    cursors.Add([data.ReadInt64(current + 8), data.ReadInt32(current + 24), data.ReadInt32(current + 28)]);
                }
            }
            List<long[]> points = [];
            long latest = data.ReadInt64(8);
            for (long sequence = Math.Max(1, latest - Capacity + 1); sequence <= latest; sequence++)
            {
                long[] point = new long[6];
                data.ReadArray(PointStart + sequence % Capacity * PointSize, point, 0, point.Length);
                if (point[0] == sequence && 0 <= now - point[5] && now - point[5] < DesktopAnnotationSnapshot.Duration)
                {
                    points.Add(point);
                }
            }
            return new(data.ReadInt64(0), now, owner == slot, cursors.ToArray(), points.ToArray());
        }
        finally
        {
            gate.ReleaseMutex();
        }
    }

    /// <summary>期限切れslotだけを再利用し、旧workerが復帰した場合も所有IDを再確認します。</summary>
    /// <returns>共有slotの位置、満員なら-1です。</returns>
    private int Register(long now)
    {
        if (0 <= slot && data.ReadInt64(Header + slot * SlotSize) == identity)
        {
            return Header + slot * SlotSize;
        }
        for (int index = 0; index < Clients; index++)
        {
            int address = Header + index * SlotSize;
            if (data.ReadInt64(address) != 0 && now - data.ReadInt64(address + 16) <= 2000)
            {
                continue;
            }
            slot = index;
            data.Write(address, identity);

            // Golden-angle hue gives consecutive connections visibly different colors; 24bit value is stable for this connection.
            int color = AnnotationColor((int)(data.ReadInt64(16) % 360));
            data.Write(16, (data.ReadInt64(16) + 137) % 360);
            data.Write(address + 8, (long)color);
            data.Write(address + 16, now);
            data.Write(address + 32, false);
            drawing = false;
            return address;
        }
        return -1;
    }

    /// <summary>明るさと彩度を揃えたRGB色を生成します。</summary>
    /// <returns>24bit RGBです。</returns>
    private static int AnnotationColor(int hue)
    {
        double part = hue / 60.0;
        int secondary = (int)(180 * (1 - Math.Abs(part % 2 - 1))) + 60;
        return ((int)part) switch
        {
            0 => 240 << 16 | secondary << 8 | 60,
            1 => secondary << 16 | 240 << 8 | 60,
            2 => 60 << 16 | 240 << 8 | secondary,
            3 => 60 << 16 | secondary << 8 | 240,
            4 => secondary << 16 | 60 << 8 | 240,
            _ => 240 << 16 | 60 << 8 | secondary,
        };
    }

    /// <summary>表示競合で実操作を待たせません。異常終了したmutexは回収します。</summary>
    /// <returns>このthreadが排他を所有したかです。</returns>
    private bool Enter()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            return gate.WaitOne(5);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    /// <summary>cursorだけを即時回収し、線は残る接続で時間どおりに消えます。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        if (Enter())
        {
            try
            {
                int address = Header + slot * SlotSize;
                if (0 <= slot && data.ReadInt64(address) == identity)
                {
                    data.Write(address, 0L);
                    data.Write(address + 32, false);
                    data.Write(0, data.ReadInt64(0) + 1);
                }
            }
            finally
            {
                gate.ReleaseMutex();
            }
        }
        disposed = true;
        data.Dispose();
        map.Dispose();
        gate.Dispose();
    }
}
