namespace ReArtWord.Contents.BackgroundService.Contracts.Files;

/// <summary>許可範囲とクラスタ共通ファイル処理方針です。</summary>
/// <param name="AllowedRoots">AllowedRootsの指定値。</param>
/// <param name="ConflictPolicy">ConflictPolicyの指定値。</param>
/// <param name="InternalFailurePolicy">InternalFailurePolicyの指定値。</param>
/// <param name="RecoveryDirectory">補償退避の保存先。未指定は利用者のLocalApplicationData配下。</param>
/// <param name="UseOperatingSystemAccess">Coreのワークフロー・管理操作ではOS権限を使用します。内部stage等の限定領域には使用しません。</param>
public sealed record FileSystemOptions(IReadOnlyList<string> AllowedRoots, EFileConflictPolicy ConflictPolicy = EFileConflictPolicy.Fail, EFileInternalFailurePolicy InternalFailurePolicy = EFileInternalFailurePolicy.Keep, string? RecoveryDirectory = null, bool UseOperatingSystemAccess = false);
