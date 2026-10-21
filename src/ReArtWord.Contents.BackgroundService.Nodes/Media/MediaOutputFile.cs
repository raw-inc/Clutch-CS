using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Media;

/// <summary>生成・変換した媒体に共通の保存方針を適用し、検証後だけ出力を確定します。</summary>
internal static class MediaOutputFile
{
    /// <summary>出力パスを持つノードだけに保存方針を追加します。</summary>
    internal static void Configure(JsonObject schema)
    {
        JsonObject properties = schema["properties"]!.AsObject();
        string output = "outputPath";
        if (properties.ContainsKey(output))
        {
            FileOutputPolicy.Configure(properties);
        }
    }

    /// <summary>既存先を先に判定し、処理を始める場合だけ親を再帰作成します。</summary>
    /// <returns>未実行・スキップ、または処理可能なnullです。</returns>
    internal static NodeResult? Prepare(MediaOptions media, NodeExecutionContext context, string prefix = "ai")
    {
        string? path = new NodeSettings(context.Settings).ReadText("outputPath");
        if (path is null)
        {
            return null;
        }
        try
        {
            FileAccessPolicy policy = new(media.Files);
            string output = policy.ResolvePath(path);
            if (FileOutputPolicy.Inspect(context, policy, output, prefix: prefix) is { } decision)
            {
                return decision;
            }
            if (!Path.IsPathFullyQualified(media.FfprobePath) || !File.Exists(media.FfprobePath))
            {
                return NodeResult.Failure(prefix + ".ffprobeUnavailable", "実行PCに設定されたffprobeがありません。媒体検証用の実行ファイルを確認してください。", EOutcome.NotExecuted);
            }
            FileOutputPolicy.EnsureParent(policy, output);
            return null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return NodeResult.Failure(prefix + ".outputPathInvalid", "出力先の絶対パス・OSアクセス権・リンク境界を確認してください。", EOutcome.NotExecuted);
        }
    }

    /// <summary>媒体検証後の競合にも同じ方針を適用し、未変更の場合だけスキップします。</summary>
    /// <returns>確定済み媒体情報、既存先のスキップ、またはロック失敗です。</returns>
    internal static async Task<NodeResult> CommitAsync(MediaOptions media, NodeExecutionContext context, string temporary, string outputPath,
        string mediaType, string kind, string prefix = "ai", CancellationToken cancellationToken = default)
    {
        FileAccessPolicy policy = new(media.Files);
        string output = policy.ResolvePath(outputPath);
        NodeValue artifact = await MediaArtifact.InspectAsync(media, temporary, output, context.PeerId, mediaType, kind, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        policy.ValidatePath(temporary);
        if (FileOutputPolicy.Inspect(context, policy, output, failure: EOutcome.Failed, prefix: prefix) is { } existing)
        {
            return existing;
        }
        string mode = FileOutputPolicy.ExistingMode(new(context.Settings));
        bool overwrite = mode is "overwrite", skip = mode is "skip";
        try
        {
            File.Move(temporary, output, overwrite);
            return NodeResult.Success(artifact);
        }
        catch (IOException error) when (FileOutputPolicy.IsLock(error))
        {
            return FileOutputPolicy.Locked(context, output, EOutcome.Failed, prefix);
        }
        catch (IOException) when (skip && File.Exists(output) && !Directory.Exists(output))
        {
            policy.ValidatePath(output);
            return FileOutputPolicy.Skipped(context.PeerId, output, "existing");
        }
    }
}
