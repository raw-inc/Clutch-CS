using System.Globalization;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Resources;

/// <summary>アプリグループの編集中の順序と間隔を、保存済み設定から分離します。</summary>
internal sealed class ApplicationGroupDraft
{
    /// <summary>既存のグループIDを維持して編集を開始します。</summary>
    internal ApplicationGroupDraft(ProcessGroupDefinition? definition)
    {
        Id = definition?.Id ?? "group-" + Guid.NewGuid().ToString("N");
        Name = definition?.Name ?? string.Empty;
        Automatic = definition?.AutomaticMonitoring ?? false;
        Mode = definition?.RecoveryMode ?? EProcessGroupRecoveryMode.FailedApplications;
        RecoveryDelay = (definition?.RecoveryDelayMilliseconds ?? 1000).ToString(CultureInfo.InvariantCulture);
        StopTimeout = (definition?.StopTimeoutMilliseconds ?? 30000).ToString(CultureInfo.InvariantCulture);
        StopPolicy = definition?.StopPolicy ?? EProcessStopPolicy.WaitForManual;
        Members.AddRange((definition?.Members ?? []).Select(member => new ApplicationGroupMemberDraft(member)));
    }

    internal string Id
    {
        get;
    }

    internal string Name
    {
        get; set;
    }

    internal bool Automatic
    {
        get; set;
    }

    internal EProcessGroupRecoveryMode Mode
    {
        get; set;
    }

    internal string RecoveryDelay
    {
        get; set;
    }

    internal string StopTimeout
    {
        get; set;
    }

    internal EProcessStopPolicy StopPolicy
    {
        get; set;
    }

    internal List<ApplicationGroupMemberDraft> Members
    {
        get;
    } = [];

    /// <summary>選択済みの一項目を上下へ移動します。</summary>
    internal void Move(ApplicationGroupMemberDraft member, int offset)
    {
        int source = Members.IndexOf(member);
        int destination = source + offset;
        if (source < 0 || destination < 0 || Members.Count <= destination)
        {
            return;
        }
        Members.RemoveAt(source);
        Members.Insert(destination, member);
    }

    /// <summary>名前、対象の重複、起動間隔を確認して保存定義へ変換します。</summary>
    /// <returns>表示順を維持した新しい定義です。</returns>
    internal ProcessGroupDefinition Build()
    {
        if (string.IsNullOrWhiteSpace(Name) || 160 < Name.Trim().Length || Members.Count is < 2 or > 32
            || Members.Select(member => member.Id).Distinct(StringComparer.Ordinal).Count() != Members.Count)
        {
            throw UiError.InvalidOperation("Enter a group name and select between 2 and 32 distinct applications.");
        }
        return new(Id, Name.Trim(), Members.Select(member => new ProcessGroupMember(member.Id,
                FormInput.Integer(member.Delay, "Delay before this application starts (ms)", 0, 3600000))).ToArray(),
            Automatic, Mode, FormInput.Integer(RecoveryDelay, "Group recovery delay (ms)", 0, 3600000),
            FormInput.Integer(StopTimeout, "Group graceful stop timeout (ms)", 1, 3600000), StopPolicy);
    }
}
