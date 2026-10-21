using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem;

/// <summary>コピー元の読取りが終わるまで既存先を保持し、未変更の場合だけスキップします。</summary>
internal static class FileOutputCommit
{
    /// <summary>コピーを同じ保存先の隣へ準備し、移動は元の実体を確定します。</summary>
    /// <returns>スキップ結果、通常確定の場合はnullです。</returns>
    internal static async Task<NodeValue?> RelocateAsync(FileOperationPlan plan, NodeExecutionContext context, FileAccessPolicy policy, CancellationToken cancellationToken = default)
    {
        string destination = plan.Destination!;
        bool copy = plan.Operation is "file.copy" or "directory.copy";
        bool directory = plan.Operation.StartsWith("directory.", StringComparison.Ordinal);
        string? temporary = null;
        bool changed = false;
        try
        {
            string source = plan.Path;
            if (copy)
            {
                temporary = policy.ResolvePath(destination + "." + Guid.NewGuid().ToString("N") + ".partial");
                await FileTree.CopyAsync(source, temporary, policy, policy, cancellationToken).ConfigureAwait(false);
                source = temporary;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (FileOutputPolicy.Inspect(context, policy, destination, directory, EOutcome.Failed) is { } decision)
            {
                return Value(decision);
            }
            policy.ValidatePath(source);
            policy.ValidatePath(destination);
            bool overwrite = FileOutputPolicy.ExistingMode(new(context.Settings)) is "overwrite";
            if (directory)
            {
                if (Directory.Exists(destination))
                {
                    changed = true;
                    FileTree.Delete(destination, policy, new NodeSettings(context.Settings).ReadBoolean("recursive"));
                }
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination, overwrite);
            }
            return null;
        }
        catch (IOException error) when (!changed && FileOutputPolicy.IsLock(error))
        {
            return Value(FileOutputPolicy.Locked(context, destination, EOutcome.Failed));
        }
        catch (IOException) when (!changed && FileOutputPolicy.ExistingMode(new(context.Settings)) is "skip" && (File.Exists(destination) || Directory.Exists(destination)))
        {
            policy.ValidatePath(destination);
            return Value(FileOutputPolicy.Skipped(context.PeerId, destination, "existing"));
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    FileTree.Delete(temporary, policy, true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // 自分の未確定コピーだけを清掃し、外部の変更や占有は強制解除しません。
                }
            }
        }
    }

    /// <summary>正常なスキップだけを値へ変換し、失敗は通常の補償境界へ返します。</summary>
    /// <returns>既存パスを指すスキップ値です。</returns>
    internal static NodeValue Value(NodeResult result)
    {
        if (result.Outcome != EOutcome.Succeeded)
        {
            throw new IOException(result.Message);
        }
        return result.Outputs["value"];
    }
}
