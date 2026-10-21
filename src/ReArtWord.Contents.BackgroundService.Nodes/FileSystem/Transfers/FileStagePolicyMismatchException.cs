using System.Diagnostics.CodeAnalysis;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;

/// <summary>保持段階の共通方針を新規要求で暗黙変更しようとしたことを表します。</summary>
// 抑制理由: 内部の一つの拒否理由専用であり任意例外本文を受け取りません。
[SuppressMessage("Design", "CA1032", Justification = "stage方針不一致だけをNodeのNotExecutedへ分類する内部例外です。")]
internal sealed class FileStagePolicyMismatchException : IOException
{
    /// <summary>新requestIdで新規転送を選ぶ必要があることを示します。</summary>
    public FileStagePolicyMismatchException() : base("保持stageのファイル方針が異なります。新しいrequestIdで新規転送を指定してください。")
    {
    }
}
