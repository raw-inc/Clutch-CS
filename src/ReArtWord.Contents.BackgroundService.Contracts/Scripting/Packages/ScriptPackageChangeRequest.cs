namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>一つのPeerへ一度だけ送る、追加・更新・構成からの削除です。</summary>
/// <param name="OperationId">再送でも維持する操作識別子です。</param>
/// <param name="Kind">対象の配布方式です。</param>
/// <param name="Package">対象名と、追加・更新時の固定版です。</param>
/// <param name="Remove">次の解析用構成から外す場合です。</param>
/// <param name="ExpectedRevision">画面で確認した現在の構成版です。</param>
/// <param name="PythonPath">仮想環境の作成元となるPeer上のPython実行ファイルです。</param>
/// <param name="NodePath">Peer上のNode.js実行ファイルです。</param>
/// <param name="NpmPath">npm-cli.jsの所在です。未指定時はNode.jsに隣接する標準配置を確認します。</param>
public sealed record ScriptPackageChangeRequest(Guid OperationId, EScriptPackageKind Kind, ScriptPackageReference Package,
    bool Remove = false, string? ExpectedRevision = null, string? PythonPath = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? NodePath = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? NpmPath = null);
