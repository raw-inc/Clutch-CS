using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>隔離Coreの自動監視で生成されたログを、実通信と再起動後の検索で確認します。</summary>
public sealed class ApplicationMonitoringLogProcessTests
{
    private static readonly string[] lifecycleMessages = ["Application.Started", "Application.Stopped", "Application.Restarted", "Application.Stopped"];
    private static readonly string[] lifecycleLevels = ["INFO", "WARN", "INFO", "WARN"];

    /// <summary>自動起動・復旧・終了・起動失敗を永続ログへ記録し、Core再起動で再送しません。</summary>
    /// <returns>実アプリケーションと管理APIを通したレベル・内容・保持の確認です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task AutomaticLifecycleLogsAreSearchableAndSurviveCoreRestartAsync(int exitCode)
    {
        using ApplicationProcessFixture app = new(exitCode, 100, withArguments: true);
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "a", CorePeer.AvailablePort(), true, redirectErrors: false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(75));
        await ReadyAsync(peer, timeout.Token);
        ProcessSchedule schedule = new("once", "once", At: DateTimeOffset.UtcNow.AddSeconds(3).ToString("O", CultureInfo.InvariantCulture));
        ProcessDefinition definition = app.Definition with { Schedules = [schedule], Recovery = new(MaximumAttempts: 1, DelayMilliseconds: 200) };
        ProcessDefinition missing = definition with { Id = "missing-application", LogicalName = "Missing executable", ExecutablePath = Path.Combine(app.Root, "missing.exe") };
        foreach (ProcessDefinition registration in new[] { definition, missing })
        {
            JsonElement saved = await peer.PostAsync<JsonElement>("processes.register", new ProcessRegistrationRequest(registration, null), timeout.Token);
            Assert.Equal("succeeded", saved.GetProperty("outcome").GetString());
        }
        LogQuery query = new(Text: "Application.", Limit: 100, UncompressedOnly: true);
        LogQueryResult? logs = null;
        await CorePeer.WaitAsync(async token =>
        {
            logs = await peer.GetAsync<LogQueryResult>("logs.query", query, token).ConfigureAwait(false);
            return logs.Entries.Count == 5;
        }, timeout.Token);
        WorkflowLogEntry[] captured = logs!.Entries.ToArray();
        Assert.True(logs.Complete);
        Assert.Equal(2, app.Starts().Length);
        WorkflowLogEntry[] normal = captured.Where(entry => string.Equals(entry.Data!.Json.GetProperty("applicationId").GetString(), definition.Id, StringComparison.Ordinal)).OrderBy(entry => entry.Time).ToArray();
        Assert.Equal(lifecycleMessages, normal.Select(entry => entry.Message));
        Assert.Equal(lifecycleLevels, normal.Select(entry => entry.Level));
        Assert.Equal(exitCode, normal[1].Data!.Json.GetProperty("exitCode").GetInt32());
        Assert.Equal(exitCode, normal[3].Data!.Json.GetProperty("exitCode").GetInt32());
        Assert.All(normal, entry => Assert.Equal(definition.DisplayName, entry.Data!.Json.GetProperty("applicationName").GetString()));
        Assert.Equal(normal[0].Data!.Json.GetProperty("pid").GetInt32(), normal[1].Data!.Json.GetProperty("pid").GetInt32());
        Assert.Equal(normal[2].Data!.Json.GetProperty("pid").GetInt32(), normal[3].Data!.Json.GetProperty("pid").GetInt32());
        WorkflowLogEntry failed = Assert.Single(captured, entry => entry.Message is "Application.StartFailed");
        Assert.Equal("ERROR", failed.Level);
        Assert.Equal("process.executableUnavailable", failed.Data!.Json.GetProperty("reason").GetString());
        Assert.Equal("notExecuted", failed.Data.Json.GetProperty("outcome").GetString());
        await peer.StopHostOnlyAsync(timeout.Token);
        await ReadyAsync(peer, timeout.Token);
        LogQueryResult restored = await peer.GetAsync<LogQueryResult>("logs.query", query, timeout.Token);
        Assert.Equal(captured.Select(entry => entry.Id).Order(), restored.Entries.Select(entry => entry.Id).Order());
        Assert.Equal(2, app.Starts().Length);
    }

    /// <summary>起動した実Coreが運用可能になるまで待機します。</summary>
    /// <returns>自動監視が稼働可能な準備完了です。</returns>
    private static async Task ReadyAsync(CorePeer peer, CancellationToken cancellationToken = default)
    {
        await peer.StartAsync(cancellationToken).ConfigureAwait(false);
        await CorePeer.WaitAsync(async token => (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(false)).GetProperty("status").GetString() is "ready"
            && (await peer.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(false)).Effective is not null, cancellationToken).ConfigureAwait(false);
    }
}
