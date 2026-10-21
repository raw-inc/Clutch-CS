using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Tcp;

/// <summary>受信結果の確定と返信後の接続寿命を分けます。</summary>
/// <param name="Result">一件の入力結果。EOFはnull。</param>
/// <param name="Emitted">入力結果を既に通知したか。</param>
/// <param name="CloseConnection">次の接続へ移るか。</param>
internal sealed record TcpItemResult(NodeResult? Result, bool Emitted = false, bool CloseConnection = false);
