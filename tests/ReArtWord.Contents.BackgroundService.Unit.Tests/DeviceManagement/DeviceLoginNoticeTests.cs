using System.Runtime.Versioning;
using Microsoft.Win32;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>実PCのポリシーを変更せず、メッセージ検出と削除の範囲を検証します。</summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceLoginNoticeTests
{
    /// <summary>新旧いずれかのタイトル・本文だけでも検出し、無関係の設定を削除しません。</summary>
    [Theory]
    [InlineData("System", "legalnoticecaption", "Title")]
    [InlineData("System", "legalnoticetext", "Message")]
    [InlineData("Winlogon", "legalnoticecaption", "Title")]
    [InlineData("Winlogon", "legalnoticetext", "Message")]
    [InlineData("System", "legalnoticetext", " ")]
    public void ClearsOnlyNoticesInAnIsolatedRegistryRoot(string source, string name, string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string path = @"SOFTWARE\ReArtWord\ContentsTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using RegistryKey root = Registry.CurrentUser.CreateSubKey(path);
            using RegistryKey system = root.CreateSubKey("System");
            using RegistryKey winlogon = root.CreateSubKey("Winlogon");
            system.SetValue("scforceoption", 1, RegistryValueKind.DWord);
            winlogon.SetValue("DefaultUserName", "retained", RegistryValueKind.String);
            Assert.False(WindowsLoginNotice.Exists(system, winlogon));
            using RegistryKey selected = root.OpenSubKey(source, true)!;
            selected.SetValue(name, value, RegistryValueKind.String);
            Assert.True(WindowsLoginNotice.Exists(system, winlogon));
            WindowsLoginNotice.Clear(system, winlogon);
            Assert.False(WindowsLoginNotice.Exists(system, winlogon));
            Assert.Null(selected.GetValue(name));
            WindowsLoginNotice.Clear(system, winlogon);
            Assert.Equal(1, system.GetValue("scforceoption"));
            Assert.Equal("retained", winlogon.GetValue("DefaultUserName"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
    }

    /// <summary>新旧の保存先がどちらもない環境でも、不要なキーを作成しません。</summary>
    [Fact]
    public void AcceptsMissingNoticeLocations()
    {
        Assert.False(WindowsLoginNotice.Exists(null, null));
        WindowsLoginNotice.Clear(null, null);
    }
}
