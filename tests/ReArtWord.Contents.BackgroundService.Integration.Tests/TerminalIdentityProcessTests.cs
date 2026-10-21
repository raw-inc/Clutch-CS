using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実CoreとPTYで、表示モードではなく子シェルの実トークンを検査します。</summary>
public sealed class TerminalIdentityProcessTests
{
    /// <summary>候補収集後にShellが消えても、補助プロセスだけを成功したTerminalとして公開しません。</summary>
    /// <returns>作成拒否と一覧の整合性を実通信で確認します。</returns>
    [Fact]
    public async Task MissingShellDoesNotPublishASuccessfulSessionAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        CorePeer core = new(Guid.NewGuid().ToString("N"), "terminal-missing-shell", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = core.ConfigureAwait(true);
        string directory = Path.Combine(core.DirectoryPath, "shell");
        Directory.CreateDirectory(directory);
        string shell = Path.Combine(directory, "nu.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), shell);
        core.StartEnvironment["PATH"] = directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await core.StartAsync(timeout.Token).ConfigureAwait(true);
        Assert.Contains(await core.GetAsync<TerminalShell[]>("terminal.shells", timeout.Token).ConfigureAwait(true), item => string.Equals(item.Path, shell, StringComparison.OrdinalIgnoreCase));
        TerminalCapabilities available = await core.GetAsync<TerminalCapabilities>("terminal.capabilities", timeout.Token).ConfigureAwait(true);
        _ = await core.PostAsync<TerminalSettings>("terminal.settings.save", available.Settings! with { StrictUserToken = true }, timeout.Token).ConfigureAwait(true);
        File.Delete(shell);
        TerminalRequest request = new(Guid.NewGuid(), Guid.NewGuid(), Shell: shell);
        await Assert.ThrowsAsync<InvalidOperationException>(() => core.PostAsync<TerminalView>("terminal.create", request, timeout.Token)).ConfigureAwait(true);
        Assert.Empty(await core.GetAsync<TerminalView[]>("terminal.list", timeout.Token).ConfigureAwait(true));
    }

    /// <summary>SYSTEM起動時も本人のプロファイルで通常・管理者を区別し、非管理者Coreは昇格を拒否します。</summary>
    /// <returns>実通信とシェル内の権限確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShellUsesAUserTokenWithTheRequestedElevationAsync(bool strict)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        bool privileged = new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator);
        CorePeer core = new(Guid.NewGuid().ToString("N"), "terminal-identity", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable coreLifetime = core.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await core.StartAsync(timeout.Token).ConfigureAwait(true);
        TerminalCapabilities capabilities = await core.GetAsync<TerminalCapabilities>("terminal.capabilities", timeout.Token).ConfigureAwait(true);
        Assert.NotNull(capabilities.Settings);
        Assert.False(capabilities.Settings.StrictUserToken);
        _ = await core.PostAsync<TerminalSettings>("terminal.settings.save", capabilities.Settings with { StrictUserToken = strict }, timeout.Token).ConfigureAwait(true);
        capabilities = await core.GetAsync<TerminalCapabilities>("terminal.capabilities", timeout.Token).ConfigureAwait(true);
        if (strict || !privileged || current.IsSystem)
        {
            Assert.True(capabilities.UserAvailable);
        }
        Assert.Equal(privileged, capabilities.AdministratorAvailable);
        TerminalShell[] shells = await core.GetAsync<TerminalShell[]>("terminal.shells", timeout.Token).ConfigureAwait(true);
        TerminalShell shell = shells.First(item => item.Name is "pwsh" or "powershell");
        string? sid = null;
        foreach (bool elevated in new[] { false, true })
        {
            TerminalRequest request = new(Guid.NewGuid(), Guid.NewGuid(), Elevated: elevated, Shell: shell.Path);
            if (elevated && !capabilities.AdministratorAvailable || !elevated && !capabilities.UserAvailable)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => core.PostAsync<TerminalView>("terminal.create", request, timeout.Token)).ConfigureAwait(true);
                Assert.Empty(await core.GetAsync<TerminalView[]>("terminal.list", timeout.Token).ConfigureAwait(true));
                continue;
            }
            TerminalView created = await core.PostAsync<TerminalView>("terminal.create", request, timeout.Token).ConfigureAwait(true);
            Assert.Equal(elevated, created.Elevated);
            int shellProcessId = 0;
            try
            {
                string command = "$i=[Security.Principal.WindowsIdentity]::GetCurrent(); [pscustomobject]@{name=$i.Name;sid=$i.User.Value;system=$i.IsSystem;admin=([Security.Principal.WindowsPrincipal]::new($i)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);profile=$env:USERPROFILE;pid=$PID;integrity=(whoami.exe /groups /fo csv /nh | Select-String 'S-1-16-').Line}|ConvertTo-Json -Compress; 'IDENTITY'+'DONE'\r";
                _ = await core.NotifyAsync<TerminalView>("terminal.input", request with { Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(command)) }, timeout.Token).ConfigureAwait(true);
                string output = string.Empty;
                await CorePeer.WaitAsync(async token =>
                {
                    TerminalOutput reply = await core.GetAsync<TerminalOutput>("terminal.read", request, token).ConfigureAwait(true);
                    output = Encoding.UTF8.GetString(Convert.FromBase64String(reply.Data));
                    return output.Contains("IDENTITYDONE", StringComparison.Ordinal);
                }, timeout.Token).ConfigureAwait(true);
                int start = output.IndexOf("{\"name\":", StringComparison.Ordinal);
                Assert.True(0 <= start, output);
                int end = output.IndexOf('}', start);
                using JsonDocument identity = JsonDocument.Parse(output[start..(end + 1)]);
                JsonElement user = identity.RootElement;
                shellProcessId = user.GetProperty("pid").GetInt32();
                Assert.False(user.GetProperty("system").GetBoolean());
                Assert.Equal(elevated, user.GetProperty("admin").GetBoolean());
                string level = "S-1-16-8192";
                if (elevated)
                {
                    level = "S-1-16-12288";
                }
                Assert.Contains(level, user.GetProperty("integrity").GetString(), StringComparison.Ordinal);
                Assert.DoesNotContain("systemprofile", user.GetProperty("profile").GetString()!, StringComparison.OrdinalIgnoreCase);
                sid ??= user.GetProperty("sid").GetString();
                Assert.Equal(sid, user.GetProperty("sid").GetString());
                if (!current.IsSystem)
                {
                    Assert.Equal(current.User!.Value, sid);
                }
                await File.WriteAllTextAsync(Path.Combine(core.DirectoryPath, "identity-" + elevated + ".json"), user.GetRawText(), timeout.Token).ConfigureAwait(true);
            }
            finally
            {
                _ = await core.PostAsync<JsonElement>("terminal.close", request, timeout.Token).ConfigureAwait(true);
            }
            await CorePeer.WaitAsync(_ => Task.FromResult(HasExited(shellProcessId)), timeout.Token).ConfigureAwait(true);
            Assert.Empty(await core.GetAsync<TerminalView[]>("terminal.list", timeout.Token).ConfigureAwait(true));
        }
    }

    /// <summary>明示終了後にPTY内の実シェルが残っていないか確認します。</summary>
    /// <returns>OSがプロセス終了を確認したかです。</returns>
    private static bool HasExited(int id)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
