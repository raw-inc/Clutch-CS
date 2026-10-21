using System.Buffers;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Scripting;

/// <summary>PyPIの公式名簿を一日キャッシュし、検索結果だけを画面へ返します。</summary>
internal sealed class PypiProjectIndex(HttpClient http, string directory) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string Cache => Path.Combine(directory, "pypi-projects.txt");

    /// <summary>完全一致・前方一致・部分一致の順で有限件を選びます。</summary>
    /// <returns>名前検索の一ページと、キャッシュ利用の説明です。</returns>
    internal async Task<ScriptPackageSearchResult> SearchAsync(string query, int offset, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool stale = false;
            if (!File.Exists(Cache) || TimeSpan.FromDays(1) < DateTimeOffset.UtcNow - new DateTimeOffset(File.GetLastWriteTimeUtc(Cache)))
            {
                try
                {
                    await DownloadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (File.Exists(Cache) && exception is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stale = true;
                }
            }
            string needle = ScriptPackageNames.Normalize(query, EScriptPackageKind.Pip);
            int Rank(string name)
            {
                if (string.Equals(name, needle, StringComparison.Ordinal))
                {
                    return 0;
                }
                if (name.StartsWith(needle, StringComparison.Ordinal))
                {
                    return 1;
                }
                return 2;
            }
            SortedSet<string> matches = new(Comparer<string>.Create((left, right) =>
            {
                int rank = Rank(left).CompareTo(Rank(right));
                return rank == 0 ? StringComparer.Ordinal.Compare(left, right)
                                 : rank;
            }));
            using StreamReader reader = new(Cache);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } name)
            {
                if (!name.Contains(needle, StringComparison.Ordinal))
                {
                    continue;
                }
                matches.Add(name);
                if (offset + 21 < matches.Count)
                {
                    matches.Remove(matches.Max!);
                }
            }
            string notice = "script.packages.pypiNameSearch";
            if (stale)
            {
                notice = "script.packages.pypiCachedSearch";
            }
            return new(matches.Skip(offset).Take(20).Select(name => new ScriptPackageSearchItem(name, null, string.Empty)).ToArray(), offset + 20 < matches.Count, notice);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>応答サイズと時間を制限し、取得完了後だけ既存索引を置き換えます。</summary>
    /// <returns>原子的なキャッシュ更新です。</returns>
    // 抑制理由: 取得した具象Streamを直後のawait usingで所有し、ConfigureAwait(false)付きで非同期解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "直後のawait usingが具体的なStreamとWriterを非同期解放します。")]
    private async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using HttpRequestMessage request = new(HttpMethod.Get, "https://pypi.org/simple/");
            request.Headers.Accept.ParseAdd("application/vnd.pypi.simple.v1+json");
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                StreamWriter output = new(temporary, false, new UTF8Encoding(false));
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(false);
                PypiIndexReader parser = new();
                int retained = 0, count = 0;
                long total = 0;
                List<string> names = [];
                bool complete = false;
                while (!complete)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(retained, buffer.Length - retained), timeout.Token).ConfigureAwait(false);
                    total += read;
                    if (134217728 < total)
                    {
                        throw new IOException("Package index exceeds the size limit.");
                    }
                    int consumed = parser.Read(buffer.AsSpan(0, retained + read), read == 0, names);
                    retained += read - consumed;
                    if (buffer.Length <= retained)
                    {
                        throw new IOException("Package index token exceeds the size limit.");
                    }
                    buffer.AsSpan(consumed, retained).CopyTo(buffer);
                    foreach (string name in names)
                    {
                        await output.WriteLineAsync(name.AsMemory(), timeout.Token).ConfigureAwait(false);
                        count++;
                    }
                    names.Clear();
                    if (read == 0)
                    {
                        if (!parser.SeenProjects || count == 0 || retained != 0)
                        {
                            throw new IOException("Package index is incomplete.");
                        }
                        complete = true;
                    }
                }
            }
            File.Move(temporary, Cache, true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            File.Delete(temporary);
        }
    }

    /// <summary>索引の取得排他を解放します。</summary>
    public void Dispose() => gate.Dispose();
}
