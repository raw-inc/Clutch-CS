using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

/// <summary>PDH_FMT_COUNTERVALUE_ITEM_Wの受信配置です。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsPdhItem
{
    /// <summary>同じ受信バッファー内にあるUTF16 実体名です。</summary>
    internal IntPtr Name;

    /// <summary>取得状態とDOUBLE値です。</summary>
    internal WindowsPdhValue Counter;

    /// <summary>受信バッファーの寿命に依存せず取得状態を表示します。</summary>
    /// <returns>状態付きの値です。</returns>
    public override readonly string ToString() => Counter.ToString();
}
