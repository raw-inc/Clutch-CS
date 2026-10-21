using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

/// <summary>PDH_FMT_COUNTERVALUEのDOUBLE受信配置です。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsPdhValue
{
    /// <summary>各実体自身の取得状態です。</summary>
    internal uint Status;

    /// <summary>unionのDOUBLE値です。</summary>
    internal double Value;

    /// <summary>値とその取得状態を表示します。</summary>
    /// <returns>診断用の状態付き値です。</returns>
    public override readonly string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Status:X8}:{Value}");
}
