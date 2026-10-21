using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Windows;

/// <summary>固定OSコマンドの開始有無と、確認できた出力だけを返します。</summary>
/// <param name="Success">コマンドを最後まで確認したか。</param>
/// <param name="Started">OS変更の呼出しを開始したか。</param>
/// <param name="Data">読取り結果。</param>
/// <param name="Steps">完了を確認した変更段階。</param>
/// <param name="Reason">生の例外本文を含まない理由コード。</param>
internal sealed record DevicePowerShellReply(bool Success, bool Started, JsonElement Data, IReadOnlyList<string> Steps, string? Reason);
