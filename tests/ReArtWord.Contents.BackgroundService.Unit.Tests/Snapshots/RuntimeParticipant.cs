using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Snapshots;

/// <summary>副作用を伴う復元失敗と補償を観測します。</summary>
internal sealed class RuntimeParticipant(string id, int initial) : IRuntimeSnapshotParticipant
{
    /// <summary>状態識別子。</summary>
    public string Id => id;

    /// <summary>実際の値。</summary>
    public int Current
    {
        get; set;
    } = initial;

    /// <summary>復元を呼んだ回数。</summary>
    public int Restores
    {
        get; private set;
    }

    /// <summary>指定値にだけ失敗を注入します。</summary>
    public int? FailValue
    {
        get; set;
    }

    /// <summary>失敗時の周辺状態変更。</summary>
    public Action? OnFailure
    {
        get; set;
    }

    /// <summary>遠隔結果の確認不能を注入します。</summary>
    public Task? RestoreWait
    {
        get; set;
    }

    /// <summary>実値を取得します。</summary>
    /// <returns>現在値。</returns>
    public Task<NodeValue> CaptureAsync(CancellationToken cancellationToken = default) => Task.FromResult(NodeValue.FromObject(Current));

    /// <summary>この試験用の構成は整数状態を宣言します。</summary>
    /// <returns>検証結果。</returns>
    public Task<IReadOnlyList<ValidationIssue>> ValidateAsync(NodeValue snapshot, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ValidationIssue>>([]);

    /// <summary>実値を変えてから成否を返します。</summary>
    /// <returns>変更結果。</returns>
    public async Task<ConfigurationPhaseResult> RestoreAsync(NodeValue snapshot, CancellationToken cancellationToken = default)
    {
        Restores++;
        Current = snapshot.Json.GetInt32();
        if (RestoreWait is not null)
        {
            await RestoreWait.ConfigureAwait(false);
        }
        if (Current == FailValue)
        {
            OnFailure?.Invoke();
            return new ConfigurationPhaseResult(EOutcome.Failed, "test.restoreFailed");
        }
        return new ConfigurationPhaseResult(EOutcome.Succeeded);
    }

    /// <summary>実値一致を返します。</summary>
    /// <returns>一致有無。</returns>
    public Task<bool> MatchesAsync(NodeValue snapshot, CancellationToken cancellationToken = default) => Task.FromResult(Current == snapshot.Json.GetInt32());
}
