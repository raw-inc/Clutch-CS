using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;

/// <summary>生の設定値を重複保存せず、開始済み操作の同一性と結果を保持します。</summary>
/// <param name="Fingerprint">固定要求の内容識別子。</param>
/// <param name="Result">確認済み結果または開始後の不明状態。</param>
internal sealed record DeviceOperationRecord(string Fingerprint, DeviceChangeResult Result);
