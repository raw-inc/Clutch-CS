using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Resources;

/// <summary>登録アプリを参照する一項目の起動間隔です。</summary>
internal sealed record ApplicationGroupMemberDraft(ProcessGroupMember member)
{
    internal string Id
    {
        get;
    } = member.ApplicationId;

    internal string Delay
    {
        get; set;
    } = member.DelayMilliseconds.ToString(CultureInfo.InvariantCulture);
}
