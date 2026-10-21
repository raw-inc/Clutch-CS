using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem;

/// <summary>保存先の存在・占有・親作成を共通化し、スキップを生成済み内容と取り違えません。</summary>
internal static class FileOutputPolicy
{
    /// <summary>新規ノードの既定値を揃え、保存済みの上書き禁止指定も受け入れます。</summary>
    internal static void Configure(JsonObject properties, bool legacy = false)
    {
        properties["existingFileMode"] = JsonNode.Parse("""{"type":"string","enum":["overwrite","skip","error"],"default":"overwrite"}""");
        properties["lockedFileMode"] = JsonNode.Parse("""{"type":"string","enum":["error","skip"],"default":"error"}""");
        if (legacy)
        {
            properties["overwrite"] = JsonNode.Parse("""{"type":"boolean","x-legacy":true,"description":"Legacy overwrite choice. An explicit existingFileMode takes precedence."}""");
        }
    }

    /// <summary>新しい明示指定を優先し、旧overwrite=falseは上書き禁止のまま扱います。</summary>
    /// <returns>上書き、スキップ、または既存先のエラーです。</returns>
    internal static string ExistingMode(NodeSettings settings)
    {
        string? mode = settings.ReadText("existingFileMode");
        if (mode is not null)
        {
            return mode;
        }
        JsonElement legacy = settings.Get("overwrite");
        if (legacy.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException("旧overwrite設定は真偽値で指定してください。");
        }
        return legacy.ValueKind == JsonValueKind.False ? "error"
                                                       : "overwrite";
    }

    /// <summary>保存先を変更する前に、種類・上書き・占有を確認します。</summary>
    /// <returns>正常スキップまたは拒否結果、操作を続けられる場合はnullです。</returns>
    internal static NodeResult? Inspect(NodeExecutionContext context, FileAccessPolicy policy, string path, bool? directory = false,
        EOutcome failure = EOutcome.NotExecuted, string prefix = "file")
    {
        policy.ValidatePath(path);
        bool isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            return null;
        }
        if (directory is bool expected && isDirectory != expected)
        {
            return NodeResult.Failure(prefix + (isDirectory ? ".outputIsDirectory"
                                                            : ".outputIsFile"), "保存先と同名の実体の種類が一致しません。", failure);
        }
        string mode = ExistingMode(new(context.Settings));
        string skip = "skip", errorMode = "error";
        if (string.Equals(mode, skip, StringComparison.Ordinal))
        {
            return Skipped(context.PeerId, path, "existing");
        }
        if (string.Equals(mode, errorMode, StringComparison.Ordinal))
        {
            return NodeResult.Failure(prefix + ".outputExists", "保存先が既に存在します。", failure);
        }
        try
        {
            Probe(path, policy);
        }
        catch (IOException error) when (IsLock(error))
        {
            return Locked(context, path, failure, prefix);
        }
        return null;
    }

    /// <summary>リンク境界を再検証し、許可範囲内の親だけを再帰的に作成します。</summary>
    internal static void EnsureParent(FileAccessPolicy policy, string path)
    {
        string parent = Path.GetDirectoryName(path) ?? throw new IOException("保存先の親がありません。");
        policy.ValidatePath(parent, true);
        Directory.CreateDirectory(parent);
        policy.ValidatePath(path);
    }

    /// <summary>既存内容を切り詰めず、ディレクトリでは配下のファイルも変更前に確認します。</summary>
    internal static void Probe(string path, FileAccessPolicy policy)
    {
        Queue<string> pending = new();
        pending.Enqueue(path);
        while (pending.TryDequeue(out string? current))
        {
            policy.ValidatePath(current);
            if (Directory.Exists(current))
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(current))
                {
                    pending.Enqueue(child);
                }
                continue;
            }
            try
            {
                using FileStream probe = new(current, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (FileNotFoundException)
            {
                // 検査中に消えた実体は、操作直前の存在・占有検査へ委ねます。
            }
        }
    }

    /// <summary>アクセス権や容量の不足を、スキップ可能なロックへ読み替えません。</summary>
    /// <returns>OSが共有違反・ロック競合を返したかです。</returns>
    internal static bool IsLock(IOException error)
    {
        int code = error.HResult & 0xffff;
        return code is 32 or 33 || !OperatingSystem.IsWindows() && code == 11;
    }

    /// <summary>ロック中でも存在を確認できる保存先だけをスキップ結果として返します。</summary>
    /// <returns>スキップまたはファイル占有エラーです。</returns>
    internal static NodeResult Locked(NodeExecutionContext context, string path, EOutcome failure, string prefix = "file")
    {
        bool skip = new NodeSettings(context.Settings).ReadText("lockedFileMode", "error") is "skip";
        if (skip && (File.Exists(path) || Directory.Exists(path)))
        {
            return Skipped(context.PeerId, path, "locked");
        }
        return NodeResult.Failure(prefix + ".outputLocked", "保存先がロックされています。", failure);
    }

    /// <summary>内容を今回作成したと偽らず、既存パスと理由を後続へ渡します。</summary>
    /// <returns>パス型を持つ正常スキップ結果です。</returns>
    internal static NodeResult Skipped(string peer, string path, string reason)
    {
        NodeValue value = NodeValue.FromObject(new { peerId = peer, path, skipped = true, skipReason = reason });
        return NodeResult.Success(PathTypeMetadata.Apply(value, new("object", Properties: new Dictionary<string, ValueTypeDefinition> { ["path"] = new("path") })));
    }
}
