namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>設定領域内の固定対象を参照します。</summary>
/// <param name="Area">設定領域。</param>
/// <param name="ResourceId">領域内の識別子。未指定は一覧。</param>
/// <param name="Scope">環境変数のMachineまたはUser。</param>
/// <param name="UserSid">Userで明示する利用者。</param>
/// <param name="Offset">Firewallの一覧開始位置。</param>
/// <param name="Filter">Firewallの表示名フィルター。</param>
public sealed record DeviceReadRequest(EDeviceArea Area, string? ResourceId = null, string Scope = "Machine", string? UserSid = null, int Offset = 0, string? Filter = null);
