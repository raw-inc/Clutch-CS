using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Installation;

/// <summary>実サービスを変更せず、自動起動の登録・修復・解除を確認します。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsStartupTests
{
    /// <summary>サービス起動とサインイン時起動を導入権限に合わせます。</summary>
    /// <returns>登録の再作成と所有を保った削除の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrationRepairsMissingStartupAndRemovesOnlyItsOwnEntry(bool perUser)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        WebClientInstallation client = new(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "Clutch client"), "ClientHost.exe", 5182, perUser, true, RunAsService: !perUser);
        StartupRegistration registration = StartupRegistration.ForClient(client);
        Dictionary<string, string> commands = new(StringComparer.Ordinal);
        List<string[]> calls = [];
        bool running = false;
        WindowsStartup startup = new((user, name) =>
        {
            Assert.Equal(perUser, user);
            return commands.GetValueOrDefault(name);
        }, (name, command) =>
        {
            if (command is null)
            {
                commands.Remove(name);
            }
            else
            {
                commands[name] = command;
            }
        }, (arguments, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Add(arguments);
            switch (arguments[0])
            {
                case "create": commands[arguments[1]] = arguments[3]; break;
                case "delete": commands.Remove(arguments[1]); break;
                case "start": running = true; break;
                case "stop": running = false; break;
            }
            string state = "STOPPED";
            if (running)
            {
                state = "RUNNING";
            }
            return Task.FromResult(state);
        });
        await startup.RegisterAsync(registration);
        Assert.Equal(registration.Command, commands[registration.Name]);
        Assert.Contains(client.Root, commands[registration.Name], StringComparison.Ordinal);
        Assert.Contains("--state", commands[registration.Name], StringComparison.Ordinal);
        if (perUser)
        {
            Assert.Empty(calls);
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), WebClientStartup.StateDirectory(client), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains(calls, call => string.Equals(call[0], "create", StringComparison.Ordinal) && string.Equals(call[4], "start=", StringComparison.Ordinal) && string.Equals(call[5], "auto", StringComparison.Ordinal));
            Assert.Contains(calls, call => string.Equals(call[0], "failure", StringComparison.Ordinal) && string.Equals(call[^1], InstallationDefaults.WindowsRecoveryActions, StringComparison.Ordinal));
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), WebClientStartup.StateDirectory(client), StringComparison.OrdinalIgnoreCase);
            await startup.StartAsync(registration);
            await startup.StartAsync(registration);
            Assert.Single(calls, call => string.Equals(call[0], "start", StringComparison.Ordinal));
            await startup.StopAsync(registration);
            Assert.False(running);
        }
        commands.Remove(registration.Name);
        await startup.RegisterAsync(registration);
        Assert.True(startup.Owns(registration));
        string otherName = "Clutch-CS-other";
        commands[otherName] = "another.exe";
        await startup.UnregisterAsync(registration);
        Assert.False(commands.ContainsKey(registration.Name));
        Assert.Equal("another.exe", commands[otherName]);
        commands[registration.Name] = "another.exe";
        await Assert.ThrowsAsync<IOException>(() => startup.RegisterAsync(registration));
        await Assert.ThrowsAsync<IOException>(() => startup.UnregisterAsync(registration));
        Assert.Equal("another.exe", commands[registration.Name]);
    }

    /// <summary>更新で実行物が変わる場合も、元の所有確認を省略しません。</summary>
    /// <returns>更新と切り戻しの登録検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingExecutableRequiresThePreviousCommand(bool perUser)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        StartupRegistration previous = new("Clutch-CS-fixture", @"C:\fixture\old.exe", ["--config", @"C:\fixture\config.toml"], perUser, "Fixture");
        StartupRegistration replacement = previous with { Executable = @"C:\fixture\new.exe" };
        string? command = previous.Command;
        WindowsStartup startup = new((_, _) => command, (_, value) => command = value, (arguments, token) =>
        {
            token.ThrowIfCancellationRequested();
            command = arguments[^1];
            return Task.FromResult(string.Empty);
        });
        await startup.ReplaceAsync(previous, replacement);
        Assert.Equal(replacement.Command, command);
        await startup.ReplaceAsync(replacement, previous);
        Assert.Equal(previous.Command, command);
        command = "another.exe";
        await Assert.ThrowsAsync<IOException>(() => startup.ReplaceAsync(previous, replacement));
        Assert.Equal("another.exe", command);
    }
}
