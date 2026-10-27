using System.Runtime.Versioning;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.CoreService.Windows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>OS実体の表示・アカウント・引数と、起動拒否時に再操作できる状態を検証します。</summary>
public sealed class ApplicationLaunchTests(ITestOutputHelper output)
{
    /// <summary>存在する不正な実行物をWindowsが拒否した場合は、起動試行済みとして通知します。</summary>
    /// <returns>ログイン条件の不足とOSによる起動失敗を区別する確認です。</returns>
    [SupportedOSPlatform("windows")]
    [ExternalRuntimeFact("CONTENTS_TEST_APPLICATION_LAUNCH")]
    public void InvalidExecutableReportsAnAttemptedWindowsLaunch()
    {
        using ProcessTestScope scope = new();
        string executable = Path.Combine(scope.Root, "invalid.exe");
        File.WriteAllText(executable, "This is not a Windows executable.");
        ProcessDefinition definition = scope.Definition with { ExecutablePath = executable, WorkingDirectory = scope.Root, Launch = new() };
        WindowsApplicationPlatform platform = new();
        Assert.True(platform.IsLaunchUserAvailable(definition));
        ProcessLaunchRejectedException rejected = Assert.Throws<ProcessLaunchRejectedException>(() => platform.Start(definition));
        Assert.True(rejected.LaunchAttempted);
        Assert.Equal("process.interactiveLaunchRejected", rejected.Message);
    }

    /// <summary>対話起動のOS境界を直接通し、低レベルの失敗も内側の診断を保持します。</summary>
    /// <returns>実ランチャーと一回だけのアプリケーション起動です。</returns>
    [SupportedOSPlatform("windows")]
    [ExternalRuntimeFact("CONTENTS_TEST_APPLICATION_LAUNCH")]
    public async Task InteractiveLauncherKeepsOwnedProcessHandle()
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1);
        WindowsApplicationPlatform platform = new();
        using System.Security.Principal.WindowsIdentity owner = System.Security.Principal.WindowsIdentity.GetCurrent();
        using System.Diagnostics.Process current = System.Diagnostics.Process.GetCurrentProcess();
        using Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token = WindowsInteractiveIdentity.UserToken(strictUserToken: true);
        IntPtr information = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Assert.True(WindowsProcessNative.GetTokenInformation(token, 12, information, sizeof(int), out _));
            (bool inJob, int limits) = ApplicationJobProbe.Read();
            output.WriteLine(JsonSerializer.Serialize(new { owner.Name, owner.IsSystem, current.SessionId, ActiveSession = WindowsProcessNative.WTSGetActiveConsoleSessionId(), TokenSession = System.Runtime.InteropServices.Marshal.ReadInt32(information), InJob = inJob, Limits = limits }));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(information);
        }
        using System.Diagnostics.Process process = platform.Start(app.Definition with { Launch = new() });
        await ApplicationRecoveryTests.WaitForStartAsync(app).ConfigureAwait(true);
        Assert.False(process.HasExited);
        app.Release();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>アプリ配置先と切り分け、OS付属の有限コマンドでもユーザー起動を検査します。</summary>
    /// <returns>固定コマンドの正常終了です。</returns>
    [SupportedOSPlatform("windows")]
    [ExternalRuntimeFact("CONTENTS_TEST_APPLICATION_LAUNCH")]
    public async Task InteractiveLauncherRunsSystemExecutable()
    {
        ProcessDefinition definition = new("fixed-command", EProcessMode.Managed, Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "exit", "0"], Environment.SystemDirectory, Launch: new(Window: EProcessWindowMode.NoWindow));
        using System.Diagnostics.Process process = new WindowsApplicationPlatform().Start(definition);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>起動前に拒否された操作では、未確認の起動意図を残して操作不能にしません。</summary>
    /// <returns>旧状態の復元と重複プロセスがないことの確認です。</returns>
    [Fact]
    public async Task RejectedLaunchDoesNotLeavePendingIntent()
    {
        using ProcessTestScope scope = new();
        using ApplicationProcessFixture app = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, platform: new RejectedPlatform());
        await service.RegisterAsync(app.Definition).ConfigureAwait(true);
        for (int index = 0; index < 2; index++)
        {
            ProcessOperationResult result = await service.StartAsync(app.Definition.Id).ConfigureAwait(true);
            Assert.Equal(EOutcome.NotExecuted, result.Outcome);
            Assert.Equal("process.interactiveUserUnavailable", result.Reason);
            Assert.Equal(EOutcome.Succeeded, (await service.ObserveAsync(app.Definition.Id).ConfigureAwait(true)).Outcome);
            Assert.Empty(app.Starts());
        }
    }

    /// <summary>実GUIで通常表示と非表示を切り替え、引用符・空引数も変えずに起動します。</summary>
    /// <returns>ログインユーザーのセッション、実ウィンドウ表示、通常終了の確認です。</returns>
    [SupportedOSPlatform("windows")]
    [ExternalRuntimeFact("CONTENTS_TEST_APPLICATION_LAUNCH")]
    public async Task LoggedInUserOwnsNormalAndHiddenWindows()
    {
        await CheckWindowAsync(EProcessWindowMode.Default).ConfigureAwait(true);
        await CheckWindowAsync(EProcessWindowMode.NoWindow).ConfigureAwait(true);
    }

    /// <summary>一回の起動とウィンドウ資源を一つの所有範囲で検査します。</summary>
    /// <returns>指定した表示方法の実機確認です。</returns>
    [SupportedOSPlatform("windows")]
    private static async Task CheckWindowAsync(EProcessWindowMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Contracts.Scripting.Runtimes.ScriptRuntimeUser user = ScriptRuntimeUserProfile.Read();
        using ApplicationProcessFixture app = new(delayMilliseconds: -1, withArguments: true);
        await File.WriteAllTextAsync(Path.Combine(app.Root, "application-fixture.json"), "{\"exitCode\":0,\"delayMs\":-1,\"window\":true}", cancellationToken).ConfigureAwait(true);
        using ProcessTestScope scope = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, platform: new WindowsApplicationPlatform());
        ProcessDefinition definition = app.Definition with { Launch = new(Window: mode), Arguments = [.. app.Definition.Arguments, "C:\\trailing\\", "line\nvalue"] };
        Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(definition, cancellationToken: cancellationToken).ConfigureAwait(true));
        ProcessOperationResult result = await service.StartAsync(definition.Id, cancellationToken).ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Succeeded, result.Reason);
        JsonElement window = await WindowAsync(app, result.Status!.Identity!, cancellationToken).ConfigureAwait(true);
        Assert.Equal(user.Id, window.GetProperty("UserId").GetString());
        Assert.NotEqual(0, window.GetProperty("SessionId").GetInt32());
        Assert.Equal(mode == EProcessWindowMode.Default, window.GetProperty("Visible").GetBoolean());
        Assert.Equal(app.Root, window.GetProperty("Directory").GetString());
        Assert.Equal(definition.Arguments, window.GetProperty("Arguments").EnumerateArray().Select(argument => argument.GetString()));
        if (mode == EProcessWindowMode.NoWindow)
        {
            Assert.False(window.GetProperty("HasConsole").GetBoolean());
            app.Release();
        }
        ProcessOperationResult stopped = await service.StopAsync(definition.Id, EProcessStopPolicy.WaitForManual, 3000, cancellationToken).ConfigureAwait(true);
        Assert.True(stopped.Outcome == EOutcome.Succeeded, stopped.Reason);
    }

    /// <summary>Core側の監視所有者を再作成してもユーザーセッションの同じ実体へ再接続します。</summary>
    /// <returns>再起動時に新しいアプリを追加起動しない確認です。</returns>
    [SupportedOSPlatform("windows")]
    [ExternalRuntimeFact("CONTENTS_TEST_APPLICATION_LAUNCH")]
    public async Task MonitoringReattachesInteractiveApplicationAfterCoreRestart()
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1);
        using ProcessTestScope scope = new();
        ProcessIdentity identity;
        ProcessDefinition definition = app.Definition with { Launch = new(Window: EProcessWindowMode.NoWindow) };
        using (ProcessService first = new(scope.Repository, "peer", scope.Options, platform: new WindowsApplicationPlatform()))
        {
            await first.RegisterAsync(definition).ConfigureAwait(true);
            ProcessOperationResult started = await first.StartAsync(definition.Id).ConfigureAwait(true);
            Assert.True(started.Outcome == EOutcome.Succeeded, started.Reason);
            identity = started.Status!.Identity!;
            await ApplicationRecoveryTests.WaitForStartAsync(app).ConfigureAwait(true);
        }
        using ProcessService restarted = new(scope.Repository, "peer", scope.Options, platform: new WindowsApplicationPlatform());
        ProcessOperationResult observed = await restarted.ObserveAsync(definition.Id).ConfigureAwait(true);
        Assert.True(observed.Status!.Running);
        Assert.Equal(identity, observed.Status.Identity);
        Assert.Single(app.Starts());
        app.Release();
        Assert.Equal(EOutcome.Succeeded, (await restarted.StopAsync(definition.Id, EProcessStopPolicy.Force, 3000).ConfigureAwait(true)).Outcome);
    }

    /// <summary>一回のウィンドウ作成結果を有限時間で待ちます。</summary>
    /// <returns>対象プロセス自身が書いた観測値です。</returns>
    internal static async Task<JsonElement> WindowAsync(ApplicationProcessFixture app, ProcessIdentity identity, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(app.Root, identity.ProcessId + ".window.json");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        }
        return JsonSerializer.Deserialize<JsonElement>(await PublishedFixtureFile.ReadAllTextAsync(path, timeout.Token).ConfigureAwait(false));
    }

    /// <summary>プロセス作成前の未ログインを模擬します。</summary>
    private sealed class RejectedPlatform : IProcessPlatform
    {
        /// <summary>アプリケーションに副作用を起こさず拒否します。</summary>
        /// <returns>この境界では生成しません。</returns>
        public System.Diagnostics.Process Start(ProcessDefinition definition) => throw new ProcessLaunchRejectedException("process.interactiveUserUnavailable");

        /// <summary>未起動なのでウィンドウ終了も発生しません。</summary>
        /// <returns>未送信です。</returns>
        public ValueTask<bool> CloseWindowAsync(ProcessIdentity identity, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }
}
