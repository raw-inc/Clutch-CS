using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>OS起動とログインだけを制御し、保存と起動は実SQLite・実プロセスを使います。</summary>
internal sealed class ApplicationBootFixture : IDisposable, IProcessPlatform
{
    private readonly IWorkflowLog? log;

    internal ApplicationProcessFixture App
    {
        get;
    }

    internal ProcessTestScope Storage
    {
        get;
    } = new();

    internal LogTestClock Clock
    {
        get;
    } = new();

    internal SystemBootSession? Boot
    {
        get;
        set;
    } = new("registration-boot", TimeSpan.FromHours(1));

    internal bool UserAvailable
    {
        get;
        set;
    } = true;

    internal bool RejectNextStart
    {
        get;
        set;
    }

    internal bool UnknownStart
    {
        get;
        set;
    }

    internal int StartAttempts
    {
        get;
        private set;
    }

    internal ProcessService Service
    {
        get;
        private set;
    }

    internal ProcessAutomation Automation
    {
        get;
        private set;
    }

    internal ProcessDefinition Definition
    {
        get;
        private set;
    }

    /// <summary>引数あり・なしの専用コピーを使用します。</summary>
    internal ApplicationBootFixture(bool arguments = false, IWorkflowLog? log = null)
    {
        this.log = log;
        App = new(delayMilliseconds: -1, withArguments: arguments);
        Definition = App.Definition with { AutomaticMonitoring = false };
        Service = CreateService();
        Automation = new(Service, "peer", timeProvider: Clock);
    }

    /// <summary>新規・編集とも公開登録と同じ比較保存を使用します。</summary>
    /// <returns>現在版の保存完了です。</returns>
    internal async Task RegisterAsync(ProcessSchedule schedule, string? name = null, bool automatic = false, CancellationToken cancellationToken = default)
    {
        RegisteredProcess? previous = (await Service.ListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        Definition = Definition with { Schedules = [schedule], LogicalName = name ?? Definition.LogicalName, AutomaticMonitoring = automatic };
        Assert.Equal(EOutcome.Succeeded, await Service.RegisterAsync(Definition, previous?.Version, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>アプリケーションを残したまま、Core内の所有者だけを作り直します。</summary>
    internal void RestartCore()
    {
        Service.Dispose();
        Service = CreateService();
        Automation = new(Service, "peer", timeProvider: Clock);
    }

    /// <summary>OSの起動識別子と経過時間を切り替えます。</summary>
    internal void Reboot(int elapsedMilliseconds = 0)
    {
        Boot = new(Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(elapsedMilliseconds));
        RestartCore();
    }

    /// <summary>確認用画面と同じ予定状態を取得します。</summary>
    /// <returns>この登録にある一つの予定です。</returns>
    internal async Task<ProcessScheduleStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        List<ProcessScheduleStatus> statuses = [];
        await foreach (ProcessScheduleStatus status in Service.ReadSchedulesAsync(Definition, cancellationToken).ConfigureAwait(false))
        {
            statuses.Add(status);
        }
        return Assert.Single(statuses);
    }

    /// <summary>実アプリケーションが起動記録を書き終えるまで有限時間待機します。</summary>
    /// <returns>後片付け時に未登録の子プロセスを残さない確認です。</returns>
    internal async Task WaitForStartsAsync(int count, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (App.Starts().Length < count)
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        }
        Assert.Equal(count, App.Starts().Length);
    }

    /// <summary>起動前のログイン有無だけを切り替えます。</summary>
    /// <returns>試験で指定した利用可否です。</returns>
    public bool IsLaunchUserAvailable(ProcessDefinition definition) => UserAvailable;

    /// <summary>副作用未実施の拒否と、結果不明の境界を実起動から分けます。</summary>
    /// <returns>通常時は専用コピーの実プロセスです。</returns>
    public Process Start(ProcessDefinition definition)
    {
        StartAttempts++;
        if (RejectNextStart)
        {
            RejectNextStart = false;
            throw new ProcessLaunchRejectedException("process.interactiveUserUnavailable");
        }
        if (UnknownStart)
        {
            throw new IOException("fixture-unconfirmed-launch");
        }
        return new DefaultProcessPlatform().Start(definition);
    }

    /// <summary>終了要求は通常の実プロセス境界を通します。</summary>
    /// <returns>通常終了要求の結果です。</returns>
    public ValueTask<bool> CloseWindowAsync(ProcessIdentity identity, CancellationToken cancellationToken = default)
        => new DefaultProcessPlatform().CloseWindowAsync(identity, cancellationToken);

    /// <summary>この試験の監視、実プロセス、保存先だけを順に解放します。</summary>
    public void Dispose()
    {
        Service.Dispose();
        App.Dispose();
        Storage.Dispose();
    }

    /// <summary>同じ保存先とOS観測を引き継いだ監視所有者です。</summary>
    /// <returns>このfixtureが破棄するサービスです。</returns>
    private ProcessService CreateService() => new(Storage.Repository, "peer", Storage.Options, Clock, platform: this, bootSession: () => Boot, log: log);
}
