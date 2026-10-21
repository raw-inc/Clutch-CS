using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;

namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>頻繁な診断保存だけを可逆圧縮し、実行結果・保持期間・受付記録には影響させません。</summary>
internal static class WorkflowHistoryCodec
{
    private static string Format => "br1";
    private static int MaximumBytes => 65536;
    internal static string CompressedReason => "execution.details.compressed";
    internal static string BlockReason => "execution.details.blockCompressed";
    private static string BlockFormat => "br2";
    internal static int MaximumBlockRecords => 32;
    private static int MaximumBlockBytes => MaximumBytes * MaximumBlockRecords + 4096;
    private static readonly IReadOnlyList<ExecutionTargetSummary> noTargets = Array.Empty<ExecutionTargetSummary>();
    private static readonly ArrayPool<byte> blockBuffers = ArrayPool<byte>.Create(65536, 2);
    private static readonly ArrayPool<byte> jsonBuffers = ArrayPool<byte>.Create(4194304, 1);

    /// <summary>圧縮で小さくなる場合だけ詳細を分離し、旧readerには明示した要約を残します。</summary>
    /// <returns>旧形式ヘッダーと、別用途へ原子的に保存する圧縮詳細です。</returns>
    internal static WorkflowHistoryRecord Encode(WorkflowHistoryRecord record)
    {
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(record.View, ContractJson.Options);
        if (raw.Length < 2048 || MaximumBytes < raw.Length)
        {
            return record;
        }
        byte[] buffer = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        if (!BrotliEncoder.TryCompress(raw, buffer, out int written, quality: 1, window: 20))
        {
            return record;
        }
        ExecutionReportSummary summary = Header(record.View);
        ExecutionResultView compatible = new(null, summary, CompressedReason);
        if (summary.Outcome != EOutcome.Succeeded)
        {
            compatible = ExecutionReportPresentation.Bound(record.View, 8192) with { DetailReason = CompressedReason };
        }
        WorkflowHistoryRecord stored = new(record.Sequence, compatible, new(record.Sequence, summary.Id, summary.CompletedAt, Format, raw.Length, Convert.ToHexString(SHA256.HashData(raw)), buffer[..written]));
        return ContractJson.ToValue(stored).PayloadSize() + ContractJson.ToValue(stored.Payload).PayloadSize() < raw.Length ? stored
                                                                                                                            : record;
    }

    /// <summary>展開容量・末尾・ハッシュ・実行識別を検査し、元の詳細を復元します。</summary>
    /// <returns>圧縮を意識せず参照できる同一の実行結果です。</returns>
    internal static WorkflowHistoryRecord Decode(WorkflowHistoryRecord record)
    {
        if (!string.Equals(record.View.DetailReason, CompressedReason, StringComparison.Ordinal))
        {
            return record with { Payload = null };
        }
        WorkflowHistoryPayload payload = record.Payload ?? throw new InvalidDataException("実行履歴の圧縮詳細がありません。");
        if (payload.Sequence != record.Sequence || payload.ExecutionId != WorkflowExecutionJournal.Identity(record.View) || payload.CompletedAt != Header(record.View).CompletedAt
            || !string.Equals(payload.Format, Format, StringComparison.Ordinal) || payload.Length < 1 || MaximumBytes < payload.Length
            || payload.Data is null || payload.Data.Length < 1 || MaximumBytes < payload.Data.Length)
        {
            throw new InvalidDataException("実行履歴の圧縮形式または容量が不正です。");
        }
        byte[] raw = Inflate(payload.Data, payload.Length, payload.Hash);
        ExecutionResultView view = JsonSerializer.Deserialize<ExecutionResultView>(raw, ContractJson.Options) ?? throw new InvalidDataException("実行履歴の詳細がありません。");
        if (Header(record.View) != Header(view))
        {
            throw new InvalidDataException("実行履歴の識別情報と詳細が一致しません。");
        }
        return record with { View = view, Payload = null };
    }

    /// <summary>記録間の共通部分も圧縮し、0.5秒の保存周期や個々の実行結果は変えません。</summary>
    /// <returns>十分な大きさの有限グループだけを圧縮した本文です。</returns>
    internal static WorkflowHistoryBlock? Pack(IReadOnlyList<WorkflowHistoryRecord> records)
    {
        if (records.Count is < 2 || MaximumBlockRecords < records.Count)
        {
            return null;
        }
        ExecutionResultView[] views = records.Select(record => record.View).ToArray();
        if (views.Any(view => State(view).Outcome != EOutcome.Succeeded))
        {
            return null;
        }
        DateTimeOffset completed = views.Max(view => State(view).CompletedAt);
        if (records[0].Sequence < 1 || long.MaxValue - records.Count + 1 < records[0].Sequence
            || records.Where((record, index) => record.Sequence != records[0].Sequence + index
            || State(record.View).CompletedAt.ToUnixTimeSeconds() != completed.ToUnixTimeSeconds()).Any())
        {
            throw new InvalidDataException("保持境界または記録順序の異なる結果をまとめられません。");
        }
        byte[] rawBuffer = jsonBuffers.Rent(records.Count * MaximumBytes + 4096);
        try
        {
            using MemoryStream stream = new(rawBuffer, 0, rawBuffer.Length, writable: true, publiclyVisible: false);
            stream.SetLength(0);
            JsonSerializer.Serialize(stream, views, ContractJson.Options);
            ReadOnlySpan<byte> raw = rawBuffer.AsSpan(0, checked((int)stream.Length));
            if (raw.Length < 2048 || MaximumBlockBytes < raw.Length)
            {
                return null;
            }
            byte[] buffer = blockBuffers.Rent(MaximumBytes);
            try
            {
                if (!BrotliEncoder.TryCompress(raw, buffer.AsSpan(0, MaximumBytes), out int written, quality: 1, window: 20) || raw.Length <= written + 1024)
                {
                    return null;
                }
                return new(records[0].Sequence, records.Count, completed, BlockFormat, raw.Length, Convert.ToHexString(SHA256.HashData(raw)), buffer[..written]);
            }
            finally
            {
                blockBuffers.Return(buffer, clearArray: true);
            }
        }
        finally
        {
            jsonBuffers.Return(rawBuffer, clearArray: true);
        }
    }

    /// <summary>成功グループの末尾を旧readerにも識別できる要約として残します。</summary>
    /// <returns>同じ循環キーの圧縮詳細と原子的に保存するヘッダーです。</returns>
    internal static WorkflowHistoryRecord BlockHeader(WorkflowHistoryRecord record)
    {
        ExecutionReportSummary summary = Header(record.View);
        if (summary.Outcome != EOutcome.Succeeded)
        {
            throw new InvalidDataException("成功以外の診断は個別に保存してください。");
        }
        return new(record.Sequence, new(null, summary, BlockReason));
    }

    /// <summary>容量・形式・件数・保持境界を確認し、同じブロックは起動時に一度だけ展開します。</summary>
    /// <returns>保存した順序の全結果です。</returns>
    internal static ExecutionResultView[] Unpack(WorkflowHistoryBlock block)
    {
        if (!string.Equals(block.Format, BlockFormat, StringComparison.Ordinal) || block.FirstSequence < 1
            || block.Count is < 2 || MaximumBlockRecords < block.Count || long.MaxValue - block.Count + 1 < block.FirstSequence
            || block.Length < 1 || MaximumBlockBytes < block.Length
            || block.Data is null || block.Data.Length < 1 || MaximumBytes < block.Data.Length)
        {
            throw new InvalidDataException("実行履歴ブロックの形式または容量が不正です。");
        }
        byte[] raw = Inflate(block.Data, block.Length, block.Hash);
        ExecutionResultView[] views = JsonSerializer.Deserialize<ExecutionResultView[]>(raw, ContractJson.Options) ?? throw new InvalidDataException("実行履歴ブロックが欠損しています。");
        if (views.Length != block.Count || views.Any(view => view is null || State(view).Outcome != EOutcome.Succeeded || WorkflowExecutionJournal.Identity(view) == Guid.Empty
            || State(view).CompletedAt.ToUnixTimeSeconds() != block.CompletedAt.ToUnixTimeSeconds())
            || views.Max(view => State(view).CompletedAt) != block.CompletedAt
            || views.Select(WorkflowExecutionJournal.Identity).Distinct().Count() != views.Length)
        {
            throw new InvalidDataException("実行履歴ブロックの件数・識別・保持境界が一致しません。");
        }
        return views;
    }

    /// <summary>スロットを再利用した別結果を、古いヘッダーへ誤って結び付けません。</summary>
    /// <returns>元の一件と同じ詳細です。</returns>
    internal static WorkflowHistoryRecord Restore(WorkflowHistoryRecord record, WorkflowHistoryBlock block, IReadOnlyList<ExecutionResultView> views)
    {
        long index = record.Sequence - block.FirstSequence;
        if (index < 0 || views.Count <= index || Header(record.View) != Header(views[(int)index]))
        {
            throw new InvalidDataException("実行履歴ヘッダーとブロックの識別が一致しません。");
        }
        return record with { View = views[(int)index], Payload = null };
    }

    /// <summary>個別形式とブロック形式に共通の、末尾と内容ハッシュの検証です。</summary>
    /// <returns>呼出側が上限を確認した長さと一致するJSONです。</returns>
    private static byte[] Inflate(byte[] data, int length, string hash)
    {
        byte[] raw = new byte[length];
        using BrotliDecoder decoder = new();
        OperationStatus status = decoder.Decompress(data, raw, out int consumed, out int written);
        if (status != OperationStatus.Done || consumed != data.Length || written != raw.Length
            || !string.Equals(Convert.ToHexString(SHA256.HashData(raw)), hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("実行履歴の展開内容が一致しません。");
        }
        return raw;
    }

    /// <summary>グループ判定だけのために、全ノードを数え直して要約を割り当てません。</summary>
    /// <returns>形式を跨いで同じ意味を持つ成否と完了時刻です。</returns>
    internal static (EOutcome Outcome, DateTimeOffset CompletedAt) State(ExecutionResultView view)
    {
        if (view.Report is { } report)
        {
            return (report.Outcome, report.CompletedAt);
        }
        return view.Summary is { } summary ? (summary.Outcome, summary.CompletedAt)
                                           : throw new InvalidDataException("実行履歴の結果がありません。");
    }

    /// <summary>本文を省いたことを旧版へ明示し、同じ実行ID・成否・件数を保持します。</summary>
    /// <returns>詳細の復元時にも照合する識別情報です。</returns>
    internal static ExecutionReportSummary Header(ExecutionResultView view)
    {
        if (view.Report is { } report)
        {
            return new(report.Id, report.WorkflowId, report.Revision, report.Outcome, report.StartedAt, report.CompletedAt, noTargets, report.Nodes.Count, report.Nodes.Sum(node => node.Targets.Count));
        }
        return view.Summary is { } summary ? summary with { Targets = noTargets }
                                           : throw new InvalidDataException("実行履歴の結果がありません。");
    }
}
