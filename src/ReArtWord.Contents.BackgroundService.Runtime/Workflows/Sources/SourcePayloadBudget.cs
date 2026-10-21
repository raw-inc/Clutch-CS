using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

/// <summary>入力数だけでなく、保持するJSON・バイナリの転送サイズにも上限を設けます。</summary>
internal sealed class SourcePayloadBudget
{
    private readonly Dictionary<NodeEmission, long> retained = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(string Workflow, string Peer), long> scopes = [];
    private long bytes;
    internal static long MaximumBytes => 128L * 1024 * 1024;
    private static long MaximumScopeBytes => 32L * 1024 * 1024;
    private static long TerminalReserveBytes => 4L * 1024 * 1024;

    /// <summary>実行中を含め保持中の値のサイズです。managed heapの使用量とは別です。</summary>
    internal long Bytes => bytes;

    /// <summary>同じemissionの保持先移動では二重加算しません。</summary>
    /// <returns>保持する空きがあるかを示します。</returns>
    internal bool TryRetain(NodeEmission emission)
    {
        if (retained.ContainsKey(emission))
        {
            return true;
        }
        long size = 512;
        foreach (NodeValue value in emission.Result.Outputs.Values.Concat(emission.Context.Inputs.Values))
        {
            size += Size(value);
        }
        (string Workflow, string Peer) scope = (emission.Context.WorkflowId, emission.Context.PeerId);
        long reserve = 0;
        if (emission.IsTerminal)
        {
            reserve = TerminalReserveBytes;
        }
        long previous = scopes.GetValueOrDefault(scope);
        if (MaximumBytes + reserve < bytes + size || MaximumScopeBytes + reserve < previous + size)
        {
            return false;
        }
        retained.Add(emission, size);
        scopes[scope] = previous + size;
        bytes += size;
        return true;
    }

    /// <summary>拒否・置換・実行終了のいずれでも保持した値を一度だけ解放します。</summary>
    internal void Release(NodeEmission emission)
    {
        if (!retained.Remove(emission, out long size))
        {
            return;
        }
        bytes -= size;
        (string Workflow, string Peer) scope = (emission.Context.WorkflowId, emission.Context.PeerId);
        scopes[scope] -= size;
        if (scopes[scope] == 0)
        {
            scopes.Remove(scope);
        }
    }

    /// <summary>JSONのUTF-8サイズとバイナリの実バイト数を数えます。</summary>
    /// <returns>保留領域へ計上するサイズです。</returns>
    private static long Size(NodeValue value)
    {
        return value.PayloadSize();
    }
}
