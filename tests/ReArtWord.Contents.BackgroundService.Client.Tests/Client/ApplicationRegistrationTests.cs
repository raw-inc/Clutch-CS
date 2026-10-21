using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>アプリ登録のフォームが既存の識別子・引数・運用意図を保持することを確認します。</summary>
public sealed class ApplicationRegistrationTests
{
    /// <summary>PC起動の既定は即時・ログイン待ちで、任意の遅延と見送り方針を保存できます。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BootSchedulePreservesDelayLoginPolicyAndIdentity(bool wait)
    {
        ProcessSchedule original = new("boot-id", "boot");
        ApplicationScheduleDraft draft = new(original);
        Assert.Equal("0", draft.BootDelay);
        Assert.True(draft.WaitForLogin);
        draft.BootDelay = "30000";
        draft.WaitForLogin = wait;
        ProcessSchedule saved = draft.Build();
        Assert.Equal("boot-id", saved.Id);
        Assert.Equal("boot", saved.Mode);
        Assert.Equal(30000, saved.BootDelayMilliseconds);
        Assert.Equal(!wait, saved.SkipWhenLoggedOut);
        ApplicationScheduleDraft restored = new(saved);
        Assert.Equal("30000", restored.BootDelay);
        Assert.Equal(wait, restored.WaitForLogin);
    }

    /// <summary>範囲外のPC起動遅延を拒否し、日時指定では非表示の遅延欄を検証しません。</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("86400001")]
    [InlineData("invalid")]
    public void BootDelayValidationOnlyAppliesToBootMode(string delay)
    {
        ApplicationScheduleDraft draft = new(new("boot", "boot")) { BootDelay = delay };
        Assert.Throws<InvalidOperationException>(() => draft.Build());
        draft.Mode = "daily";
        Assert.Equal(0, draft.Build().BootDelayMilliseconds);
    }

    /// <summary>PC起動では時刻欄を隠し、遅延とログイン方針を表示します。</summary>
    /// <returns>実Razorを用いた表示契約の確認です。</returns>
    [Fact]
    public async Task BootScheduleFormShowsStartupFieldsInsteadOfCalendarFieldsAsync()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        ProcessDefinition definition = new("boot-app", EProcessMode.Managed, "C:\\App.exe", [], "C:\\", Schedules: [new("boot", "boot")]);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ApplicationRegistrationEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Definition"] = definition })).ConfigureAwait(true)).ToHtmlString());
        Assert.Contains("At PC startup", html, StringComparison.Ordinal);
        Assert.Contains("Delay after PC startup (ms)", html, StringComparison.Ordinal);
        Assert.Contains("Wait for login", html, StringComparison.Ordinal);
        Assert.Matches("""<option(?=[^>]*\bvalue="wait")(?=[^>]*\bselected\b)[^>]*>""", html);
        Assert.Contains("Skip this startup", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Time zone", html, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"time\"", html, StringComparison.Ordinal);
    }

    /// <summary>新規は対話表示、既存は従来の起動セッションを維持し、両設定を独立して保存します。</summary>
    [Fact]
    public void LaunchOptionsKeepLegacyBehaviorAndSupportExplicitDesktopModes()
    {
        ProcessDefinition legacy = new("app", EProcessMode.Managed, "C:\\App\\app.exe", [], "C:\\App");
        ApplicationRegistrationDraft unchanged = new(legacy) { Name = "Renamed" };
        Assert.Null(unchanged.Build().Launch);
        ApplicationRegistrationDraft created = new(null) { Name = "New", Executable = legacy.ExecutablePath, Directory = legacy.WorkingDirectory };
        Assert.Equal(new ProcessLaunchOptions(), created.Build().Launch);
        created.WindowMode = EProcessWindowMode.NoWindow;
        Assert.Equal(new ProcessLaunchOptions(Window: EProcessWindowMode.NoWindow), created.Build().Launch);
        created.LaunchSession = EProcessLaunchSession.CoreAccount;
        Assert.Equal(new ProcessLaunchOptions(EProcessLaunchSession.CoreAccount, EProcessWindowMode.NoWindow), created.Build().Launch);
        unchanged.LaunchSession = EProcessLaunchSession.Automatic;
        unchanged.WindowMode = EProcessWindowMode.Default;
        Assert.Equal(new ProcessLaunchOptions(), unchanged.Build().Launch);
    }

    /// <summary>論理名を変更しても参照IDと空白・空引数をそのまま保存します。</summary>
    [Fact]
    public void RenamePreservesIdentityArgumentsAndLegacyRecoveryDefaults()
    {
        ProcessDefinition original = new("registered-id", EProcessMode.Managed, "C:\\Apps\\Player.exe", ["one two", string.Empty, "one two"], "C:\\Apps");
        ApplicationRegistrationDraft draft = new(original) { Name = "Main screen" };
        ProcessDefinition saved = draft.Build();
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal("Main screen", saved.DisplayName);
        Assert.Equal(original.Arguments, saved.Arguments);
        Assert.True(saved.Recovery!.RestartOnNormalExit);
        Assert.True(saved.Recovery.RestartOnAbnormalExit);
        Assert.True(saved.Recovery.RestartOnUnknownExit);
        Assert.False(saved.AutomaticMonitoring);
        Assert.Equal(3, draft.Arguments.Select(item => item.EditorId).Distinct().Count());
        draft.Arguments[0].Value = "changed";
        Assert.Equal("one two", saved.Arguments[0]);
    }

    /// <summary>新規登録は監視・起動を暗黙に有効化せず、空の引数リストを受け付けます。</summary>
    [Fact]
    public void NewRegistrationDoesNotStartImplicitlyAndPreservesScheduleIds()
    {
        ApplicationRegistrationDraft draft = new(null) { Name = "Player", Executable = "/opt/player", Directory = "/opt" };
        draft.Schedules.Add(new(new("existing-schedule", "daily", "Asia/Tokyo", Time: "10:00", AlreadyRunning: EProcessRunningPolicy.Skip)));
        ProcessDefinition saved = draft.Build();
        Assert.False(saved.AutomaticMonitoring);
        Assert.False(saved.Recovery!.RestartOnNormalExit);
        Assert.True(saved.Recovery.RestartOnAbnormalExit);
        Assert.False(saved.Recovery.RestartOnUnknownExit);
        Assert.Empty(saved.Arguments);
        Assert.Equal("existing-schedule", Assert.Single(saved.Schedules!).Id);
        Assert.Equal("Asia/Tokyo", saved.Schedules![0].TimeZoneId);
        Assert.Equal(EProcessRunningPolicy.Skip, saved.Schedules[0].AlreadyRunning);
    }

    /// <summary>未知の起動時刻やPIDを推測して監視対象へ結び付けません。</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("100", "2026-10-12T12:30:00")]
    [InlineData("not-a-pid", "2026-10-12T12:30:00Z")]
    public void ObservationOnlyRequiresExactIdentity(string pid, string startedAt)
    {
        ApplicationRegistrationDraft draft = new(null) { Name = "Existing", Executable = "/opt/player", Directory = "/opt", Mode = EProcessMode.ObserveOnly, Pid = pid, StartedAt = startedAt };
        Assert.Throws<InvalidOperationException>(() => draft.Build());
    }

    /// <summary>フォームを開くだけでは操作せず、重複引数も別入力欄として描画します。</summary>
    /// <returns>実Razorと所有ホストの表示確認です。</returns>
    [Fact]
    public async Task FormShowsOwnerAndStructuredFieldsWithoutOperatingApplicationsAsync()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        ProcessDefinition definition = new("app", EProcessMode.Managed, "C:\\App.exe", [string.Empty, string.Empty], "C:\\", LogicalName: "Projector player");
        int saves = 0;
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
            {
                ["Definition"] = definition, ["Owner"] = "Test-host / isolated-cluster / a",
                ["Save"] = EventCallback.Factory.Create<ProcessDefinition>(this, _ => saves++),
            };
            return (await renderer.RenderComponentAsync<ApplicationRegistrationEditor>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
        });
        Assert.Contains("Test-host / isolated-cluster / a", html, StringComparison.Ordinal);
        Assert.Contains("Projector player", html, StringComparison.Ordinal);
        Assert.Contains("Maximum restart attempts", html, StringComparison.Ordinal);
        Assert.Contains("Scheduled starts", html, StringComparison.Ordinal);
        Assert.DoesNotContain("textarea", html, StringComparison.Ordinal);
        Assert.Equal(0, saves);
    }
}
