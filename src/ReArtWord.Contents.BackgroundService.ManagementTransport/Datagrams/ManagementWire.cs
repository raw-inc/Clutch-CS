using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>管理操作と既存操作記録要求の変換を一箇所へ揃えます。</summary>
public static class ManagementWire
{
    /// <summary>不正応答を成功扱いせず、元処理が不明であることを返します。</summary>
    /// <returns>検証できた管理応答です。</returns>
    public static ManagementReply Response(WireMessage? message)
    {
        try
        {
            ClusterResponse? response = message?.Body.Deserialize<ClusterResponse>(ContractJson.Options);
            if (response is not null)
            {
                return Reply(response);
            }
        }
        catch (JsonException)
        {
            return new(EOutcome.Unknown, Reason: "Observer.ResponseMalformed");
        }
        return new(EOutcome.Unknown, Reason: "Observer.ResponseUnconfirmed");
    }

    /// <summary>照会応答を読めない時に再実行を選びません。</summary>
    /// <returns>元操作記録の実行中・結果・確認不能です。</returns>
    public static ManagementQueryResult QueryReply(WireMessage? message)
    {
        try
        {
            ClusterRequestStatus? status = message?.Body.Deserialize<ClusterRequestStatus>(ContractJson.Options);
            if (status is not null)
            {
                ManagementReply? reply = null;
                if (status.Response is not null)
                {
                    reply = Reply(status.Response);
                }
                return new(status.Running, reply, status.Reason);
            }
        }
        catch (JsonException)
        {
            return new(false, Reason: "Observer.ReceiptMalformed");
        }
        return new(false, Reason: "Observer.ReceiptUnconfirmed");
    }

    /// <summary>履歴を作成しない照会の登録名です。</summary>
    public static string ReadKind => "management.read";

    /// <summary>開始・完了操作記録を持つ操作の登録名です。</summary>
    public static string ExecuteKind => "management.execute";

    /// <summary>短命なUI状態の通知に限定する非永続登録名です。</summary>
    public static string TransientKind => "management.transient";

    /// <summary>元のID・実体・期限・ペイロードをそのまま共通要求へ入れます。</summary>
    /// <returns>内容指紋を共通化した要求です。</returns>
    public static ClusterRequest Request(ManagementCommand command, bool readOnly, bool transient = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        string kind = ExecuteKind;
        if (readOnly)
        {
            kind = ReadKind;
        }
        if (transient)
        {
            kind = TransientKind;
        }
        return new(command.RequestId, kind, command.TargetPeerId, command.TargetInstanceId, command.ExpiresAt, NodeValue.FromObject(command));
    }

    /// <summary>処理中の共通応答もドメイン結果へ作り替えず返します。</summary>
    /// <returns>元の管理結果または共通受付結果です。</returns>
    public static ManagementReply Reply(ClusterResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Value?.Json.Deserialize<ManagementReply>(ContractJson.Options) ?? new ManagementReply(response.Outcome, Reason: response.Reason);
    }
}
