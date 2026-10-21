namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

/// <summary>一つのワークフローの詳細追跡で値を残す境界です。</summary>
/// <param name="Inputs">ノードへ渡す入力を含めます。</param>
/// <param name="Outputs">ノードの解析・変換・通信結果を含めます。</param>
/// <param name="RawCommunication">通信body/datagram/frameを含めます。認証ヘッダーは対象外です。</param>
/// <param name="RawByteLimit">伏字後の先頭バイト数。未指定時は全内容です。</param>
public sealed record TraceSelection(bool Inputs = true, bool Outputs = true, bool RawCommunication = true, int? RawByteLimit = null);
