using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;

/// <summary>同じCore内で、グループと個別操作が競合しないための登録所有情報です。</summary>
internal sealed class ProcessGroupOwnership
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, string> members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> automatic = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> active = new(StringComparer.Ordinal);

    /// <summary>保存済みの定義と操作状態を反映します。</summary>
    internal void Set(ProcessGroupDefinition definition, bool operating)
    {
        using Lock.Scope scope = gate.EnterScope();
        Remove(definition.Id);
        foreach (ProcessGroupMember member in definition.Members)
        {
            members[member.ApplicationId] = definition.Id;
        }
        automatic[definition.Id] = definition.AutomaticMonitoring;
        active[definition.Id] = operating;
    }

    /// <summary>状態を先に保護し、その後に実プロセスを確認します。</summary>
    internal void SetActive(string id, bool operating)
    {
        using Lock.Scope scope = gate.EnterScope();
        active[id] = operating;
    }

    /// <summary>グループ自動監視または進行中の操作が、このアプリを所有しているかです。</summary>
    /// <returns>単体の自動復旧に渡さない場合はtrueです。</returns>
    internal bool Managed(string application)
    {
        using Lock.Scope scope = gate.EnterScope();
        return members.TryGetValue(application, out string? group) && (automatic.GetValueOrDefault(group) || active.GetValueOrDefault(group));
    }

    /// <summary>グループを解放します。実プロセスは操作しません。</summary>
    internal void Remove(string id)
    {
        using Lock.Scope scope = gate.EnterScope();
        foreach ((string application, string owner) in members.ToArray())
        {
            if (string.Equals(owner, id, StringComparison.Ordinal))
            {
                members.Remove(application);
            }
        }
        automatic.Remove(id);
        active.Remove(id);
    }

    /// <summary>読み取りと実体確認は保ち、競合する起動・停止・編集だけを拒否します。</summary>
    /// <returns>拒否理由、または許可を表すnullです。</returns>
    internal string? Check(string application, EProcessGroupAccess access, string? group)
    {
        using Lock.Scope scope = gate.EnterScope();
        bool owned = members.TryGetValue(application, out string? owner);
        if (group is not null)
        {
            return owned && string.Equals(owner, group, StringComparison.Ordinal) ? null
                                                                                  : "process.groupMembershipChanged";
        }
        if (access == EProcessGroupAccess.Observe || !owned)
        {
            return null;
        }
        if (access == EProcessGroupAccess.Remove)
        {
            return "process.groupMembershipRequired";
        }
        if (active.GetValueOrDefault(owner!))
        {
            return "process.groupOperationInProgress";
        }
        return access == EProcessGroupAccess.Recover && automatic.GetValueOrDefault(owner!) ? "process.groupManaged"
                                                                                            : null;
    }
}
