using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Tests.Shared;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>実プロセスとローカル保存を試験ごとに分離します。</summary>
internal sealed class ProcessTestScope : IDisposable
{
    private readonly List<ProcessIdentity> owned = [];
    private ApplicationProcessFixture? application;

    /// <summary>試験用の構成の許可対象実行物と保存先を固定します。</summary>
    internal ProcessTestScope()
    {
        Root = Path.Combine(Path.GetTempPath(), "contents-process-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Repository = new SqliteRepository(Path.Combine(Root, "state.db"));
        Options = new ProcessOptions(1, 100, 1, 10);
    }

    /// <summary>この試験専用の保存先のルートです。</summary>
    internal string Root
    {
        get;
    }

    /// <summary>実際に再読取する永続リポジトリです。</summary>
    internal IDurableRepository Repository
    {
        get;
    }

    /// <summary>短い有限条件の共通設定です。</summary>
    internal ProcessOptions Options
    {
        get;
    }

    /// <summary>別試験の後継候補に入らない、専用実行ファイルの待機プロセス定義です。</summary>
    internal ProcessDefinition Definition
    {
        get
        {
            // 保存先だけを使う試験では実行物を用意せず、必要になったときに分離します。
            application ??= new(delayMilliseconds: 60000);
            return application.Definition with { Id = "test-app", LogicalName = null, AutomaticMonitoring = false };
        }
    }

    /// <summary>同じ永続状態からサービスを作り直します。</summary>
    /// <returns>呼出側が破棄するサービスです。</returns>
    internal ProcessService CreateService()
    {
        return new ProcessService(Repository, "peer", Options);
    }

    /// <summary>試験で起動した実プロセスだけをcleanup対象へ登録します。</summary>
    internal void Track(ProcessOperationResult result)
    {
        if (result.Status?.Identity is not null)
        {
            owned.Add(result.Status.Identity);
        }
    }

    /// <summary>サービス外からの異常終了を実プロセスで起こします。</summary>
    /// <returns>実終了確認の完了です。</returns>
    // 抑制理由: 起動時刻をUTCへ変換して照合し、PID再利用先を停止しません。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "試験で指定した実体だけを停止するため、OSの起動時刻を照合します。")]
    internal static async Task CrashAsync(ProcessIdentity identity, CancellationToken cancellationToken = default)
    {
        using Process process = Process.GetProcessById(identity.ProcessId);
        if (new DateTimeOffset(process.StartTime.ToUniversalTime()) != identity.StartedAt)
        {
            throw new InvalidOperationException("試験対象のプロセスの起動時刻が一致しません。");
        }
        process.Kill(true);
        await ProcessCompletion.WaitAsync(process, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>試験が作成したプロセスと保存先のルートだけを取り除きます。</summary>
    // 抑制理由: PID再利用先を終了しないためBCLのStartTimeをUTCのDateTimeOffsetへ即時変換して比較します。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "試験所有processだけをcleanupするOS識別子の照合に必要です。")]
    public void Dispose()
    {
        foreach (ProcessIdentity identity in owned)
        {
            try
            {
                using Process process = Process.GetProcessById(identity.ProcessId);
                if (!ProcessCompletion.HasExited(process) && new DateTimeOffset(process.StartTime.ToUniversalTime()) == identity.StartedAt)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                    process.WaitForExit();
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 試験途中で終了したプロセスは既にcleanup済みです。
            }
        }
        application?.Dispose();
        Directory.Delete(Root, true);
    }
}
