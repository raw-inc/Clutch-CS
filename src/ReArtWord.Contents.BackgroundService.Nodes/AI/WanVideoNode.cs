using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>Wan2.6の受理済みジョブを照会し、最終MP4の取得まで実行します。</summary>
internal sealed class WanVideoNode : AiOperationNode
{
    /// <summary>APIリージョンを選択できる動画生成ノードを作ります。</summary>
    internal WanVideoNode(ISecretStore secrets, AiOptions options, MediaOptions media) : base(secrets, options, media, "ai.video.generate", """{"model":{"type":"string","minLength":1,"examples":["wan2.6-t2v"],"default":"wan2.6-t2v"},"region":{"type":"string","enum":["singapore","beijing"],"default":"singapore"},"outputPath":{"type":"string","minLength":1},"duration":{"x-visibleWhen":{"property":"jobId","values":[""],"default":""},"type":"integer","minimum":2,"maximum":15,"x-requiredWhen":{"property":"jobId","values":[""],"default":""}},"size":{"x-visibleWhen":{"property":"jobId","values":[""],"default":""},"type":"string","enum":["1280*720","720*1280","960*960","1088*832","832*1088","1920*1080","1080*1920","1440*1440","1632*1248","1248*1632"],"x-requiredWhen":{"property":"jobId","values":[""],"default":""}},"pollIntervalMs":{"default":15000,"type":"integer","minimum":10,"maximum":60000},"downloadTimeoutMs":{"default":300000,"type":"integer","minimum":1,"maximum":3600000},"maximumBytes":{"default":1073741824,"type":"integer","minimum":1,"maximum":2147483647},"jobId":{"type":"string","minLength":1,"maxLength":128}}""", ["outputPath"], true, outputs: [new("value"), new("job")], inputRequired: false)
    {
    }

    /// <summary>既存ジョブの照会には、新規生成だけで使う条件を要求しません。</summary>
    /// <returns>新規生成に不足する設定です。</returns>
    protected override IReadOnlyList<ValidationIssue> ValidateSettings(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. base.ValidateSettings(definition)];
        NodeSettings settings = new(definition.Settings);
        string jobId = "jobId";
        if (settings.ReadText(jobId) is null)
        {
            foreach (string required in new[] { "duration", "size" })
            {
                if (settings.Get(required).ValueKind == JsonValueKind.Undefined)
                {
                    issues.Add(new("ai.generationSetting.required", "新規生成に必要な設定を指定してください。", definition.Id, required));
                }
            }
        }
        return issues;
    }

    /// <summary>新規要求と既存ジョブの照会には、同じリージョンの接続先を使います。</summary>
    /// <returns>設定された公式APIの接続先です。</returns>
    protected override Uri ProviderBaseUri(NodeSettings settings) => Options.AlibabaBaseUri(settings.ReadText("region", "singapore")!);

    /// <summary>生成要求または明示された既存ジョブを一度だけ処理します。</summary>
    /// <returns>完成MP4またはサービス提供元が確定した失敗です。</returns>
    protected override async Task<NodeResult> PerformAsync(NodeExecutionContext context, AiRequestSession session, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        string output = settings.ReadText("outputPath")!;
        AiMediaOutput.RequireProbe(Media);
        string temporary = MediaArtifact.TemporaryPath(Media, output, allowExisting: true);
        try
        {
            string? job = settings.ReadText("jobId");
            if (job is null)
            {
                string prompt = Prompt(context);
                if (1500 < prompt.Length)
                {
                    throw new ArgumentException("Wan2.6の生成指示は1500文字以内にしてください。");
                }
                JsonElement accepted = await session.JsonAsync("api/v1/services/aigc/video-generation/video-synthesis", new { model = settings.ReadText("model"), input = new { prompt }, parameters = new { size = settings.ReadText("size"), duration = settings.ReadInt32("duration"), prompt_extend = false, watermark = false } }, true, cancellationToken).ConfigureAwait(false);
                job = accepted.GetProperty("output").GetProperty("task_id").GetString();
            }
            if (string.IsNullOrWhiteSpace(job) || 128 < job.Length || !job.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            {
                throw new InvalidDataException("providerのjob識別子が不正です。");
            }
            session.JobId = job;
            while (!cancellationToken.IsCancellationRequested)
            {
                JsonElement response = await session.JsonAsync("api/v1/tasks/" + job, null, cancellationToken: cancellationToken).ConfigureAwait(false);
                JsonElement result = response.GetProperty("output");
                string? status = result.GetProperty("task_status").GetString();
                string succeeded = "SUCCEEDED";
                string failed = "FAILED";
                string cancelled = "CANCELED";
                string pending = "PENDING";
                string running = "RUNNING";
                if (string.Equals(status, failed, StringComparison.Ordinal) || string.Equals(status, cancelled, StringComparison.Ordinal))
                {
                    session.TerminalKnown = true;
                    EOutcome outcome = EOutcome.Failed;
                    if (string.Equals(status, cancelled, StringComparison.Ordinal))
                    {
                        outcome = EOutcome.Cancelled;
                    }
                    return NodeResult.Failure("ai.videoTerminalFailure", "動画providerが生成失敗または取消を確定しました。", outcome);
                }
                if (string.Equals(status, succeeded, StringComparison.Ordinal))
                {
                    session.TerminalKnown = true;
                    Uri downloadUri = new(result.GetProperty("video_url").GetString()!);
                    downloadUri = AiMediaOutput.AlibabaDownloadUri(Options, downloadUri);
                    using CancellationTokenSource download = new(TimeSpan.FromMilliseconds(settings.ReadInt32("downloadTimeoutMs", 300000)));
                    using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(download.Token, cancellationToken);
                    _ = await session.DownloadAsync(downloadUri, null, temporary, new FileAccessPolicy(Media.Files), settings.ReadInt32("maximumBytes", 1073741824), false, linked.Token).ConfigureAwait(false);
                    NodeResult saved = await MediaOutputFile.CommitAsync(Media, context, temporary, output, "video/mp4", "video", cancellationToken: linked.Token).ConfigureAwait(false);
                    Dictionary<string, NodeValue> outputs = saved.Outputs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    outputs["job"] = NodeValue.FromObject(new { id = job, status });
                    return saved with { Outputs = outputs };
                }
                if (!string.Equals(status, pending, StringComparison.Ordinal) && !string.Equals(status, running, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("providerのjob状態が不明です。");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(settings.ReadInt32("pollIntervalMs", 15000)), context.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("動画の最終状態を確認できません。");
        }
        finally
        {
            MediaArtifact.Cleanup(Media, temporary);
        }
    }
}
