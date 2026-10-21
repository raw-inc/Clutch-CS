using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;

/// <summary>確定済み履歴の存在と元本文の検査値を先に共有し、未取得を未実行・未保存と区別します。</summary>
/// <param name="OperationId">元の操作識別子です。</param>
/// <param name="Fingerprint">元要求の指紋です。</param>
/// <param name="Outcome">保存された確定結果です。</param>
/// <param name="Reason">元結果の理由です。</param>
/// <param name="Hash">保存JSON本文のSHA-256です。</param>
/// <param name="RecordedAt">元の履歴観測日時です。取得時刻で直近履歴へ戻しません。</param>
/// <param name="SourcePeer">本文を持つ元のPeerです。存在証明の再転送でも取得元を失いません。</param>
/// <param name="Entry">操作結果以外の確定済み履歴を指す場合の元の識別です。</param>
public sealed record RecoveryRecord(Guid OperationId, string Fingerprint, EOutcome Outcome, string? Reason, string Hash, long RecordedAt = 0, string? SourcePeer = null, RecoveryEntryIdentity? Entry = null)
{
    /// <summary>現在値と操作結果を混同しない保存行の種類です。</summary>
    internal string Kind => Entry is null ? "receipt"
                                          : "entry";

    /// <summary>同じ種類の保存行を曖昧にしない識別です。</summary>
    internal string Key => Entry is null ? OperationId.ToString("N")
                                         : DurableRepository.GetAddress(Entry.Scope, Entry.Key);
}
