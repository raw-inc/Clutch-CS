namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

/// <summary>任意リポジトリスコープを公開せず内部状態だけを指定します。</summary>
public sealed record InternalDataTarget(string WorkflowId, EInternalDataScope Scope, string Key)
{
    /// <summary>内部運用領域を参照できない固定の範囲とキー長を検証します。</summary>
    /// <returns>共有契約として扱える対象かです。</returns>
    public bool IsValid() => !string.IsNullOrWhiteSpace(Key) && Key.Length <= 512 && Enum.IsDefined(Scope)
        && (Scope == EInternalDataScope.Cluster ? string.IsNullOrEmpty(WorkflowId)
                                                : !string.IsNullOrWhiteSpace(WorkflowId) && WorkflowId.Length <= 200);
}
