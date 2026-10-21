using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;

/// <summary>中断後に明示再要求で照合する段階状態です。</summary>
/// <param name="Token">転送識別子。</param>
/// <param name="Request">同一性と宛先。</param>
/// <param name="Directory">内部段階保存先。</param>
/// <param name="Entries">確認済みentry定義。</param>
/// <param name="Committed">宛先への確定済みか。</param>
/// <param name="ConflictPolicy">段階開始時の共通方針。再要求でも変更しません。</param>
/// <param name="InternalFailurePolicy">段階開始時の共通内部失敗方針です。</param>
internal sealed record FileStageState(FileStageToken Token, FileStageRequest Request, string Directory, Dictionary<string, FileManifestEntry> Entries, bool Committed = false, EFileConflictPolicy? ConflictPolicy = null, EFileInternalFailurePolicy? InternalFailurePolicy = null)
{
    /// <summary>同じ段階で確定した相対パスの内容指定を変更しません。</summary>
    internal void Register(FileManifestEntry entry)
    {
        if (Entries.TryGetValue(entry.RelativePath, out FileManifestEntry? existing) && existing != entry)
        {
            throw new IOException("同じ相対pathの内容指定が異なります。");
        }
        Entries[entry.RelativePath] = entry;
    }
}
