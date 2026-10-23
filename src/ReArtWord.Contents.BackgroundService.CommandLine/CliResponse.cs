using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.CommandLine;

/// <summary>HTTP受付と業務完了を区別した終了コードです。</summary>
internal static class CliResponse
{
    /// <summary>実在DTOの結果を成功・非成功・未確定へ分類します。</summary>
    /// <returns>0は確認済、4は非成功または一部成功、5は未確定です。</returns>
    internal static int Classify(string command, JsonElement response, int httpStatus)
    {
        if (response.ValueKind == JsonValueKind.Null || response.ValueKind == JsonValueKind.Undefined)
        {
            return 5;
        }
        if (response.ValueKind != JsonValueKind.Object)
        {
            string keyList = "list";
            string keyCatalog = "catalog";
            string resourcesHistory = "resources history";
            return string.Equals(command, resourcesHistory, StringComparison.Ordinal) || command.EndsWith(keyList, StringComparison.Ordinal) || string.Equals(command, keyCatalog, StringComparison.Ordinal) ? 0
                                                                                                                                                                                                             : 5;
        }
        string fleetPreview = "fleet preview";
        if (string.Equals(command, fleetPreview, StringComparison.Ordinal))
        {
            return FleetPreview(response);
        }
        string yamlParseCommand = "workflow parse";
        string idKey = "id";
        string nodesKey = "nodes";
        string wiresKey = "wires";
        if (string.Equals(command, yamlParseCommand, StringComparison.Ordinal))
        {
            bool completeDefinition = response.TryGetProperty(idKey, out JsonElement id) && id.ValueKind == JsonValueKind.String
                && response.TryGetProperty(nodesKey, out JsonElement nodes) && nodes.ValueKind == JsonValueKind.Array
                && response.TryGetProperty(wiresKey, out JsonElement wires) && wires.ValueKind == JsonValueKind.Array;
            return completeDefinition ? 0
                                      : 5;
        }
        string conflictResolveCommand = "conflict resolve";
        string conflictStatusCommand = "conflict status";
        string operationKey = "operationId";
        if ((string.Equals(command, conflictResolveCommand, StringComparison.Ordinal) || string.Equals(command, conflictStatusCommand, StringComparison.Ordinal))
            && (!response.TryGetProperty(operationKey, out JsonElement operationIdentity) || operationIdentity.ValueKind != JsonValueKind.String
                || !Guid.TryParse(operationIdentity.GetString(), out _)))
        {
            return 5;
        }
        string? expectedState = command switch
        {
            "cluster detach" => "detached",
            "cluster rejoin" => "ready",
            _ => null,
        };
        string workflowGet = "workflow get";
        string compatibility = "workflow compatibility";
        if (string.Equals(command, compatibility, StringComparison.Ordinal))
        {
            string issuesKey = "issues";
            string omittedKey = "issuesOmitted";
            string nextKey = "nextSnapshot";
            if (response.TryGetProperty(issuesKey, out JsonElement issues) && issues.ValueKind == JsonValueKind.Array)
            {
                if (issues.GetArrayLength() != 0)
                {
                    return 4;
                }
                if (response.TryGetProperty(omittedKey, out JsonElement omittedIssues) && omittedIssues.ValueKind == JsonValueKind.True
                    || response.TryGetProperty(nextKey, out JsonElement next) && next.ValueKind == JsonValueKind.String)
                {
                    return 5;
                }
                return 0;
            }
            return 5;
        }
        string detailReason = "detailReason";
        if (string.Equals(command, workflowGet, StringComparison.Ordinal) && response.TryGetProperty(detailReason, out JsonElement omitted) && omitted.ValueKind == JsonValueKind.String)
        {
            return 4;
        }
        string[] pages = ["workflow detached", "workflow detached-query", "conflict list", "conflict query", "workflow query", "execution list", "execution query", "snapshot list", "snapshot query"];
        string[] activeRuns = ["workflow running", "workflow running-query"];
        string itemsKey = "items";
        if (activeRuns.Contains(command, StringComparer.Ordinal) && response.TryGetProperty(itemsKey, out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            return 0;
        }
        string entriesKey = "entries";
        if (pages.Contains(command, StringComparer.Ordinal) && response.TryGetProperty(entriesKey, out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
        {
            return 0;
        }
        string conflictDetail = "conflict detail";
        string conflictKey = "conflict";
        if (string.Equals(command, conflictDetail, StringComparison.Ordinal))
        {
            if (response.TryGetProperty(conflictKey, out JsonElement detail) && detail.ValueKind == JsonValueKind.Object)
            {
                return 0;
            }
            return 4;
        }
        string detachedDetail = "workflow detached-detail";
        string stateKey = "state";
        if (string.Equals(command, detachedDetail, StringComparison.Ordinal))
        {
            if (response.TryGetProperty(stateKey, out JsonElement detailState) && detailState.ValueKind == JsonValueKind.Object)
            {
                return 0;
            }
            return 4;
        }
        string saveKey = "save";
        if (response.TryGetProperty(saveKey, out JsonElement savedResult) && savedResult.ValueKind == JsonValueKind.Object)
        {
            return Classify(command, savedResult, httpStatus);
        }
        string chunkCommand = "workflow definition-chunk";
        string dataKey = "dataBase64";
        if (string.Equals(command, chunkCommand, StringComparison.Ordinal) && response.TryGetProperty(dataKey, out JsonElement data) && data.ValueKind == JsonValueKind.String)
        {
            return 0;
        }
        string networkSet = "network set";
        if (string.Equals(command, networkSet, StringComparison.Ordinal))
        {
            string listeningKey = "listening";
            if (response.TryGetProperty(listeningKey, out JsonElement listening) && listening.ValueKind == JsonValueKind.True)
            {
                return 0;
            }
            return 4;
        }
        string requestStatus = "request status";
        if (string.Equals(command, requestStatus, StringComparison.Ordinal))
        {
            return ClassifyReceipt(response, httpStatus);
        }
        string statusKey = "status";
        if (expectedState is not null)
        {
            bool confirmed = response.TryGetProperty(statusKey, out JsonElement state) && state.ValueKind == JsonValueKind.String && string.Equals(state.GetString(), expectedState, StringComparison.Ordinal);
            return confirmed ? 0
                             : 5;
        }
        string keyRunning = "running";
        if (response.TryGetProperty(keyRunning, out JsonElement running) && running.ValueKind == JsonValueKind.True)
        {
            return 5;
        }
        string keyCompleted = "completed";
        if (response.TryGetProperty(keyCompleted, out JsonElement completed) && completed.ValueKind == JsonValueKind.False)
        {
            return 5;
        }
        string keyLoaded = "loaded";
        if (response.TryGetProperty(keyLoaded, out JsonElement loaded))
        {
            return loaded.ValueKind == JsonValueKind.True ? 0
                                                          : 5;
        }
        string keyOutcome = "outcome";
        if (response.TryGetProperty(keyOutcome, out JsonElement outcome))
        {
            return Outcome(outcome);
        }
        string keyValid = "valid";
        if (response.TryGetProperty(keyValid, out JsonElement valid))
        {
            return valid.ValueKind == JsonValueKind.True ? 0
                                                         : 4;
        }
        string keyComplete = "complete";
        if (response.TryGetProperty(keyComplete, out JsonElement complete))
        {
            return complete.ValueKind == JsonValueKind.True ? 0
                                                            : 4;
        }
        string keyResult = "result";
        if (response.TryGetProperty(keyResult, out JsonElement result))
        {
            return Classify(command, result, httpStatus);
        }
        string keySaved = "saved";
        if (response.TryGetProperty(keySaved, out JsonElement saved))
        {
            if (saved.ValueKind == JsonValueKind.True)
            {
                return 0;
            }
            string keyShared = "shared";
            if (response.TryGetProperty(keyShared, out _))
            {
                int code = 0;
                string[] phases = ["saved", "shared", "snapshot", "activation"];
                foreach (string phase in phases)
                {
                    int current = response.TryGetProperty(phase, out JsonElement part) && part.TryGetProperty(keyOutcome, out JsonElement state) ? Outcome(state)
                                                                                                                                                 : 5;
                    code = Math.Max(code, current);
                }
                return code;
            }
        }
        if (httpStatus == 202)
        {
            return 5;
        }
        return command switch
        {
            "status" or "catalog" or "workflow list" or "workflow get" or "network get" or "configuration get" or "resources get" or "log statistics" => 0,
            _ => 5,
        };
    }

    /// <summary>プレビューの計画を開始済み結果と区別します。</summary>
    /// <returns>対象と配布元が揃った計画だけを確認済みとします。</returns>
    private static int FleetPreview(JsonElement response)
    {
        string operationKey = "operationId", targetsKey = "targets", releaseKey = "release";
        if (response.TryGetProperty(operationKey, out JsonElement operation) && operation.ValueKind == JsonValueKind.String && operation.TryGetGuid(out Guid id) && id != Guid.Empty
            && response.TryGetProperty(targetsKey, out JsonElement targets) && targets.ValueKind == JsonValueKind.Array && targets.GetArrayLength() != 0
            && response.TryGetProperty(releaseKey, out JsonElement release) && release.ValueKind == JsonValueKind.Object)
        {
            return 0;
        }
        return 5;
    }

    /// <summary>bridgeの受付完了と内側の業務結果を別々に確認します。</summary>
    /// <returns>確認できた内側結果か未確定を返します。</returns>
    private static int ClassifyReceipt(JsonElement response, int httpStatus)
    {
        string runningKey = "running";
        string replyKey = "reply";
        string outcomeKey = "outcome";
        string valueKey = "value";
        string jsonKey = "json";
        if (!response.TryGetProperty(runningKey, out JsonElement running) || running.ValueKind != JsonValueKind.False
            || !response.TryGetProperty(replyKey, out JsonElement reply) || reply.ValueKind != JsonValueKind.Object
            || !reply.TryGetProperty(outcomeKey, out JsonElement outcome))
        {
            return 5;
        }
        int code = Outcome(outcome);
        if (code != 0)
        {
            return code;
        }
        if (reply.TryGetProperty(valueKey, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(jsonKey, out JsonElement json))
        {
            return Classify(string.Empty, json, httpStatus);
        }
        return 5;
    }

    /// <summary>照会と応答の操作IDを、更新要求を包むデータも含め照合します。</summary>
    /// <returns>含まれる操作IDが同じ場合だけtrueです。</returns>
    internal static bool MatchesOperation(JsonElement response, Guid expected)
    {
        if (response.ValueKind != JsonValueKind.Object)
        {
            return true;
        }
        string key = "operationId";
        if (response.TryGetProperty(key, out JsonElement identity) && (identity.ValueKind != JsonValueKind.String || !Guid.TryParse(identity.GetString(), out Guid actual) || actual != expected))
        {
            return false;
        }
        string saveKey = "save";
        if (response.TryGetProperty(saveKey, out JsonElement save) && !MatchesOperation(save, expected))
        {
            return false;
        }
        string requestKey = "request";
        if (response.TryGetProperty(requestKey, out JsonElement request) && !MatchesOperation(request, expected))
        {
            return false;
        }
        string replyKey = "reply";
        string valueKey = "value";
        string jsonKey = "json";
        if (response.TryGetProperty(replyKey, out JsonElement reply) && reply.ValueKind == JsonValueKind.Object
            && reply.TryGetProperty(valueKey, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(jsonKey, out JsonElement json))
        {
            return MatchesOperation(json, expected);
        }
        return true;
    }

    /// <summary>HTTPで文字列化されたEOutcomeを確認します。</summary>
    /// <returns>確認済成功以外はnonzeroです。</returns>
    private static int Outcome(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return 5;
        }
        return value.GetString() switch
        {
            "succeeded" => 0,
            "failed" or "notExecuted" or "cancelled" => 4,
            _ => 5,
        };
    }
}
