using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Terminal;

/// <summary>実体内のセッションと入力所有権・連番を明示した端末操作です。</summary>
/// <param name="SessionId">作成前に固定するセッションIDです。</param>
/// <param name="Owner">入力を所有する画面の識別子です。</param>
/// <param name="Sequence">入力の重複を防ぐ連番です。</param>
/// <param name="Offset">読取り開始バイト位置です。</param>
/// <param name="Data">UTF-8入力のBase64です。</param>
/// <param name="Columns">端末の幅です。</param>
/// <param name="Rows">端末の高さです。</param>
/// <param name="Elevated">管理者権限で起動するかです。未指定時は通常ユーザーです。</param>
/// <param name="Shell">起動時一覧の絶対パスです。未指定時は一覧先頭を使います。</param>
public sealed record TerminalRequest(Guid SessionId, Guid Owner = default, long Sequence = 0, long Offset = 0, string? Data = null, int Columns = 100, int Rows = 30, bool Elevated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Shell = null);
