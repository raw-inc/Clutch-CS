using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

/// <summary>応答本文や宛先を露出せず、ClientHostへ更新protocolの失敗箇所を渡します。</summary>
/// <param name="action">呼出実装が固定した操作名です。</param>
/// <param name="status">管理通信で確認した結果です。</param>
// 抑制理由: protocol境界専用であり任意メッセージやinner exceptionの持込を禁止します。
[SuppressMessage("Design", "CA1032", Justification = "安全な固定操作名と状態だけを受け取る内部例外です。")]
public sealed class UpdateTransportException(string action, EOutcome status) : IOException("更新管理要求を確認できません。")
{
    /// <summary>実装が固定した操作名と管理状態です。</summary>
    public string Code
    {
        get;
    } = action.Replace('/', '.') + "." + status.ToString();

    /// <summary>安全な固定診断コードだけを文字列表現にします。</summary>
    /// <returns>要求内容を含まない診断コードです。</returns>
    public override string ToString()
    {
        return Code;
    }
}
