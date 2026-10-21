using System.Diagnostics.CodeAnalysis;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>handlerの副作用前に管理引数を解釈できなかったことだけを示します。</summary>
// 抑制理由: Coreの管理境界内だけで入力失敗を分類し、公開APIへ例外型を出しません。
[SuppressMessage("Design", "CA1064", Justification = "外部へ公開しない入力分類であり、管理応答のreasonへ変換します。")]
internal sealed class ManagementInputException : Exception
{
    /// <summary>入力の欠損を表します。</summary>
    public ManagementInputException() : base("管理引数の形式を確認してください。")
    {
    }

    /// <summary>一般化した入力理由を指定します。</summary>
    public ManagementInputException(string message) : base(message)
    {
    }

    /// <summary>元の例外を内部で保持し、外部応答へ本文を出しません。</summary>
    public ManagementInputException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
