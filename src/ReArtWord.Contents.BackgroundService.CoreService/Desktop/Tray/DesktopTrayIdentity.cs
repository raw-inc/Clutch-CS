using System.Security.Cryptography;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;

/// <summary>接続ごとのHWNDではなく、同じ配置のアイコンをWindowsが記憶する識別子です。</summary>
internal static class DesktopTrayIdentity
{
    /// <summary>未署名の異なる実行パスでGUIDを共有できないShell仕様に合わせます。</summary>
    /// <returns>配置と製品の表示グループが同じなら一定のGUIDです。</returns>
    internal static Guid Create(string executable, string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        string identity = "ReArtWord.Contents.RemoteDesktop.Tray.v1\n" + Path.GetFullPath(executable).ToUpperInvariant() + "\n" + group;
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }
}
