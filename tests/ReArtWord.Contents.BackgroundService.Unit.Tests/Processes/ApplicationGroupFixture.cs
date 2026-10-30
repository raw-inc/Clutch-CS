using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>時計とOS応答だけを制御し、グループの保存とプロセスは実物で検証します。</summary>
internal sealed class ApplicationGroupFixture : IDisposable, IProcessPlatform
{
    internal ProcessTestScope Storage
    {
        get;
    } = new();

    internal LogTestClock Clock
    {
        get;
    } = new();

    internal RecordingWorkflowLog Log
    {
        get;
    } = new();

    internal List<string> Started
    {
        get;
    } = [];

    internal List<string> Stopped
    {
        get;
    } = [];

    internal Dictionary<int, string> Ids
    {
        get;
    } = [];

    internal ProcessService Service
    {
        get; private set;
    }

    internal bool UnknownStart
    {
        get; set;
    }

    internal bool UserAvailable
    {
        get; set;
    } = true;

    internal SystemBootSession Boot
    {
        get; set;
    } = new("boot", TimeSpan.FromHours(1));

    internal ProcessGroupDefinition Definition
    {
        get; set;
    } = new("show", "Display system", [new("server"), new("player", 2000)],
        RecoveryMode: EProcessGroupRecoveryMode.EntireGroup, RecoveryDelayMilliseconds: 1000, StopTimeoutMilliseconds: 1);

    /// <summary>専用保存先と所有する実プロセスだけを使用します。</summary>
    internal ApplicationGroupFixture() => Service = CreateService();

    /// <summary>二つの管理対象を登録し、任意で起動してからグループ化します。</summary>
    /// <returns>確認済みの初期状態です。</returns>
    internal async Task RegisterAsync(bool start = true, CancellationToken cancellationToken = default)
    {
        foreach (ProcessGroupMember member in Definition.Members)
        {
            ProcessDefinition application = Storage.Definition with { Id = member.ApplicationId, AutomaticMonitoring = true, Recovery = new(DelayMilliseconds: 1000) };
            Assert.Equal(EOutcome.Succeeded, await Service.RegisterAsync(application, cancellationToken: cancellationToken).ConfigureAwait(false));
            if (start)
            {
                Assert.Equal(EOutcome.Succeeded, (await Service.StartAsync(member.ApplicationId, cancellationToken).ConfigureAwait(false)).Outcome);
            }
        }
        Assert.Equal(EOutcome.Succeeded, await Service.Groups.RegisterAsync(Definition, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>固定時計で指定した回数だけ状態遷移を進めます。</summary>
    /// <returns>実時間の再試行で隠さない進行結果です。</returns>
    internal async Task TickAsync(int steps = 1, CancellationToken cancellationToken = default)
    {
        for (int step = 0; step < steps; step++)
        {
            (IReadOnlyList<ProcessGroupSession> sessions, IReadOnlyList<RegisteredProcess> applications) = await Service.Groups.CaptureAsync(cancellationToken).ConfigureAwait(false);
            foreach (ProcessGroupSession session in sessions)
            {
                await session.TickAsync(applications, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>現在の一グループを公開一覧から読みます。</summary>
    /// <returns>画面と同じ操作状態です。</returns>
    internal async Task<RegisteredProcessGroup> GroupAsync(CancellationToken cancellationToken = default)
        => Assert.Single(await Service.Groups.ListAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>操作所有者だけを作り直し、保存済みの位置から再開します。</summary>
    internal void RestartCore()
    {
        Service.Dispose();
        Service = CreateService();
    }

    /// <summary>外部から一つのアプリケーションだけを強制終了します。</summary>
    /// <returns>OSが終了を確認した時点です。</returns>
    internal async Task CrashAsync(string id, CancellationToken cancellationToken = default)
    {
        ProcessOperationResult observed = await Service.ObserveAsync(id, cancellationToken).ConfigureAwait(false);
        await ProcessTestScope.CrashAsync(observed.Status!.Identity!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>起動回を追跡し、結果を失った場合にも試験プロセスを残しません。</summary>
    /// <returns>実プロセス、または起動後の応答喪失です。</returns>
    // 抑制理由: 試験で開始したOSプロセスの正確な時刻を取得し、PID再利用先をcleanupしないために使用します。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "BCL StartTimeを直ちにUTCのDateTimeOffsetへ変換します。")]
    public Process Start(ProcessDefinition definition)
    {
        Process process = new DefaultProcessPlatform().Start(definition);
        Started.Add(definition.Id);
        Ids.Add(process.Id, definition.Id);
        ProcessIdentity identity = new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
        Storage.Track(new(EOutcome.Succeeded, new(definition.Id, EProcessMode.Managed, Guid.NewGuid(), identity, true, null, null, false, false, 0, 0, false)));
        if (UnknownStart)
        {
            UnknownStart = false;
            process.Dispose();
            throw new IOException("fixture-lost-launch-response");
        }
        return process;
    }

    /// <summary>実停止をForce方針へ任せ、停止要求の順序を記録します。</summary>
    /// <returns>画面を持たない試験プロセスへの通常終了要求結果です。</returns>
    public ValueTask<bool> CloseWindowAsync(ProcessIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopped.Add(Ids[identity.ProcessId]);
        return ValueTask.FromResult(false);
    }

    /// <summary>ログイン前の全体停止を防ぐ条件を制御します。</summary>
    /// <returns>試験で指定した可否です。</returns>
    public bool IsLaunchUserAvailable(ProcessDefinition definition) => UserAvailable;

    /// <summary>監視を止めてから試験所有の全プロセスを片付けます。</summary>
    public void Dispose()
    {
        Service.Dispose();
        Storage.Dispose();
    }

    /// <summary>同じDBとOSプロセスを参照する監視所有者を作ります。</summary>
    /// <returns>fixtureが破棄するサービスです。</returns>
    private ProcessService CreateService() => new(Storage.Repository, "peer", Storage.Options, Clock, platform: this, bootSession: () => Boot, log: Log);
}
