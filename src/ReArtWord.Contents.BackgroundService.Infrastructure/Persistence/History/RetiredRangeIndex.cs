namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>退役済み区間を発行元・用途別に保持し、未使用の欠番を新規受付禁止へ含めません。</summary>
internal sealed class RetiredRangeIndex
{
    /// <summary>同じ発行元・用途の区間を連番順に保持します。</summary>
    public Dictionary<string, List<RetiredIdRange>> Groups
    {
        get; init;
    } = new(StringComparer.Ordinal);

    /// <summary>隣接または重なる区間だけを合成し、再送によって区間を増やしません。</summary>
    internal void Add(RetiredIdRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        range.Validate();
        string bucket = range.Bucket;
        if (!Groups.TryGetValue(bucket, out List<RetiredIdRange>? rows))
        {
            Groups[bucket] = [range];
            return;
        }
        int index = Start(rows, range.First);
        if (0 < index && range.First <= (ulong)rows[index - 1].Last + 1)
        {
            index--;
        }
        int end = index;
        while (end < rows.Count && rows[end].First <= (ulong)range.Last + 1)
        {
            range = range with { First = Math.Min(range.First, rows[end].First), Last = Math.Max(range.Last, rows[end].Last) };
            end++;
        }
        rows.RemoveRange(index, end - index);
        rows.Insert(index, range);
    }

    /// <summary>実際に記録した一点だけを照合し、連番の最大値を失効境界として使いません。</summary>
    /// <returns>指定した用途のIDが退役済みかです。</returns>
    internal bool Contains(RetiredIdRange point)
    {
        if (!Groups.TryGetValue(point.Bucket, out List<RetiredIdRange>? rows))
        {
            return false;
        }
        int index = Start(rows, point.First);
        return 0 < index && point.First <= rows[index - 1].Last;
    }

    /// <summary>操作結果は、共有・ローカルのどちらの確定した退役証拠も照合します。</summary>
    /// <returns>指定IDが実際に退役したかです。</returns>
    internal bool ContainsOperation(Guid id) => Contains(RetiredIdRange.Operation(id, true)) || Contains(RetiredIdRange.Operation(id, false));

    /// <summary>受付キーではスコープと元の文字列表記を含めて照合します。</summary>
    /// <returns>元の受付が退役したかです。</returns>
    internal bool ContainsKey(string scope, string key) => RetiredIdRange.Key(scope, key) is { } point && Contains(point);

    /// <summary>区間の分割履歴に左右されない順序で、同期・チェックポイントへ渡します。</summary>
    /// <returns>共有指定に従った正規化済みの区間です。</returns>
    internal RetiredIdRange[] Export(bool sharedOnly = true)
        => Groups.OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair => pair.Value)
            .Where(range => !sharedOnly || range.Shared).ToArray();

    /// <summary>未対応の内容を黙って捨てず、保存した索引の区間と用途を検証します。</summary>
    internal void Validate()
    {
        if (Groups is null)
        {
            throw new InvalidDataException("退役済みIDの索引がありません。");
        }
        foreach ((string bucket, List<RetiredIdRange> rows) in Groups)
        {
            if (rows is null || rows.Any(row => row is null))
            {
                throw new InvalidDataException("退役済みIDの区間が欠けています。");
            }
            ulong? previous = null;
            foreach (RetiredIdRange row in rows)
            {
                row.Validate();
                if (!string.Equals(bucket, row.Bucket, StringComparison.Ordinal) || previous is not null && row.First <= previous + 1)
                {
                    throw new InvalidDataException("退役済みIDの索引が整列していません。");
                }
                previous = row.Last;
            }
        }
    }

    /// <summary>指定番号以下で始まる最後の区間の次を、二分探索で返します。</summary>
    /// <returns>挿入と照合に共通の境界です。</returns>
    private static int Start(List<RetiredIdRange> rows, uint value)
    {
        int first = 0, last = rows.Count;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            if (value < rows[middle].First)
            {
                last = middle;
            }
            else
            {
                first = middle + 1;
            }
        }
        return first;
    }
}
