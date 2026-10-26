using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Porta.Pty;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.CoreService.Terminal;
using ReArtWord.Contents.BackgroundService.CoreService.Windows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>表示モードと実権限を一致させ、黙示的な権限置換を防ぎます。</summary>
public sealed class TerminalIdentityTests
{
    /// <summary>Windowsの非管理者Coreは管理者セッションを偽って起動しません。</summary>
    [WindowsPrivilegeFact(false)]
    public void NonElevatedCoreRejectsAdministratorSessions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        PtyOptions options = new() { App = "shell.exe", Cwd = AppContext.BaseDirectory };
        Assert.False(WindowsInteractiveIdentity.IsAdministrator());
        Assert.Throws<InvalidOperationException>(() => TerminalIdentity.Configure(options, true));
        TerminalIdentity.Configure(options, false);
        Assert.Equal("shell.exe", options.App);
        Assert.False(TerminalIdentity.Capabilities().AdministratorAvailable);
        Assert.True(TerminalIdentity.Capabilities().UserAvailable);
    }

    /// <summary>昇格したCoreは取得できた管理者IDだけを使用し、利用できない場合は起動を拒否します。</summary>
    [WindowsPrivilegeFact(true)]
    public void ElevatedCoreUsesAvailableAdministratorIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.True(WindowsInteractiveIdentity.IsAdministrator());
        PtyOptions options = new() { App = "shell.exe", Cwd = AppContext.BaseDirectory };
        if (TerminalIdentity.Capabilities().AdministratorAvailable)
        {
            TerminalIdentity.Configure(options, true, "readiness");
            Assert.Contains(TerminalIdentity.AdministratorShellArgument, options.CommandLine);
            Assert.Contains("readiness", options.CommandLine);
            Assert.NotEqual("shell.exe", options.App);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => TerminalIdentity.Configure(options, true));
        }
    }

    /// <summary>通常モードのトークンがSYSTEMや昇格管理者へ置き換わりません。</summary>
    [Fact]
    public void UserTokenHasTheRequestedIdentityAndElevation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        foreach (bool elevated in new[] { false, true })
        {
            TerminalCapabilities capabilities = TerminalIdentity.Capabilities();
            if (elevated && !capabilities.AdministratorAvailable || !elevated && !capabilities.UserAvailable)
            {
                continue;
            }
            using SafeAccessTokenHandle token = WindowsInteractiveIdentity.UserToken(elevated);
            using WindowsIdentity user = new(token.DangerousGetHandle());
            Assert.Equal(elevated && capabilities.AdministratorUsesSystem, user.IsSystem);
            Assert.Equal(elevated, WindowsInteractiveIdentity.IsElevated(token));
            Assert.Equal(elevated, new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator));
        }
    }

    /// <summary>未ログオンだけをSYSTEM起動の対象とし、通常モードや権限エラーでは代替しません。</summary>
    [Theory]
    [InlineData(true, 1U, 1008, true)]
    [InlineData(true, 1U, 1312, true)]
    [InlineData(true, uint.MaxValue, 87, true)]
    [InlineData(false, uint.MaxValue, 1008, false)]
    [InlineData(false, 1U, 1008, false)]
    [InlineData(true, 1U, 5, false)]
    [InlineData(true, uint.MaxValue, 5, false)]
    [InlineData(true, 1U, 1314, false)]
    public void SystemFallbackRequiresNoLoggedOnUser(bool elevated, uint session, int error, bool expected)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(expected, WindowsInteractiveIdentity.AllowsSystemFallback(elevated, session, error));
        }
    }
}
