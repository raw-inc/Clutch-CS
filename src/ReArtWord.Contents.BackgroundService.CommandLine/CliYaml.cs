using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ReArtWord.Contents.BackgroundService.CommandLine;

/// <summary>YAML構文は既存Core parserへ委譲しCLI内に再実装しません。</summary>
internal static class CliYaml
{
    /// <summary>選択した同じPeerで完全定義を得て後続の意味検証へ渡します。</summary>
    /// <returns>Coreが解釈したワークフロー定義です。</returns>
    internal static async Task<JsonObject> ParseAsync(HttpClient client, CliCommand command, JsonObject source, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/workflows/parse") { Content = JsonContent.Create(source) };
        request.Headers.Add("X-Contents-Cluster", command.Options["--cluster"]);
        request.Headers.Add("X-Contents-Peer", command.Options["--peer"]);
        request.Headers.Add("X-Contents-Request", Guid.NewGuid().ToString("D"));
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonObject definition = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("YAML definition unavailable");
        string idKey = "id";
        string nodesKey = "nodes";
        string wiresKey = "wires";
        if (definition[idKey] is null || definition[nodesKey] is not JsonArray || definition[wiresKey] is not JsonArray)
        {
            throw new InvalidOperationException("YAML definition unavailable");
        }
        return definition;
    }
}
