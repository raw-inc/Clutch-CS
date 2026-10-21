using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;

/// <summary>Peerの登録実行先と既定選択を保持し、実行ごとに探索や永続書込みを繰り返しません。</summary>
public sealed class ScriptRuntimeRegistry : IDisposable
{
    private readonly Lock gate = new();
    private readonly SemaphoreSlim discovery = new(1, 1);
    private readonly string root;
    private readonly Func<ScriptRuntimeUser> userProvider;
    private readonly Dictionary<string, ScriptRuntimeConfiguration> configurations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, string? UserId, ScriptRuntimeCatalog Catalog)> cache = new(StringComparer.Ordinal);

    /// <summary>データ保存先と、OS境界から取得するログイン利用者を固定します。</summary>
    public ScriptRuntimeRegistry(string dataDirectory, Func<ScriptRuntimeUser> userProvider)
    {
        ArgumentNullException.ThrowIfNull(userProvider);
        this.userProvider = userProvider;
        root = Path.Combine(Path.GetFullPath(dataDirectory), "script-runtimes");
        Directory.CreateDirectory(root);
        foreach (string language in new[] { "python", "javascript" })
        {
            configurations[language] = ScriptRecordFile.Read<ScriptRuntimeConfiguration>(ConfigurationPath(language)) ?? new(null, [], null);
        }
    }

    /// <summary>導入受付時に利用者を確定し、サービスアカウントへ代替しません。</summary>
    /// <returns>対象PCの現在の利用者です。</returns>
    public ScriptRuntimeUser User() => userProvider();

    /// <summary>同時更新の検査に使用する小さい設定版を取得します。</summary>
    /// <returns>初期状態はnullです。</returns>
    public string? Revision(string language)
    {
        using (gate.EnterScope())
        {
            return configurations[ScriptRuntimePaths.Language(language)].Revision;
        }
    }

    /// <summary>候補の検査を直列化し、表示要求間では五分間共有します。</summary>
    /// <returns>既定と利用可否を含む候補一覧です。</returns>
    public async Task<ScriptRuntimeCatalog> CatalogAsync(ScriptRuntimeQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        string language = ScriptRuntimePaths.Language(query.Language);
        ScriptRuntimeUser? user = null;
        string? problem = null;
        try
        {
            user = User();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            problem = "script.runtime.userUnavailable";
        }
        await discovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ScriptRuntimeConfiguration configuration;
            using (gate.EnterScope())
            {
                configuration = configurations[language];
                if (!query.Refresh && cache.TryGetValue(language, out (DateTimeOffset At, string? UserId, ScriptRuntimeCatalog Catalog) prior)
                    && string.Equals(prior.UserId, user?.Id, StringComparison.Ordinal) && DateTimeOffset.UtcNow - prior.At < TimeSpan.FromMinutes(5))
                {
                    return prior.Catalog;
                }
            }
            if (user is not null)
            {
                (IReadOnlyDictionary<string, string> aliases, string? migrationProblem) = await LegacyScriptRuntimeMigration.MigrateAsync(user, language, configuration, cancellationToken).ConfigureAwait(false);
                problem ??= migrationProblem;
                using (gate.EnterScope())
                {
                    ScriptRuntimeConfiguration current = configurations[language];
                    configuration = current.Migrate(aliases);
                    if (!ReferenceEquals(current, configuration))
                    {
                        ScriptRecordFile.Write(ConfigurationPath(language), configuration);
                        configurations[language] = configuration;
                        cache.Remove(language);
                    }
                }
            }
            Dictionary<string, string> candidates = new(ScriptRuntimePaths.Comparer);
            if (configuration.DefaultPath is not null)
            {
                candidates[configuration.DefaultPath] = "registered";
            }
            foreach (string path in configuration.Paths)
            {
                candidates.TryAdd(path, "registered");
            }
            foreach (KeyValuePair<string, string> candidate in ScriptRuntimeDiscovery.Find(language, user))
            {
                candidates.TryAdd(candidate.Key, candidate.Value);
            }
            KeyValuePair<string, string>[] inputs = candidates.Take(64).ToArray();
            ScriptRuntimeEntry[] entries = new ScriptRuntimeEntry[inputs.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, inputs.Length), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (index, token) =>
            {
                KeyValuePair<string, string> item = inputs[index];
                entries[index] = await ScriptRuntimePaths.ProbeAsync(language, item.Key, item.Value, configuration.Paths.Contains(item.Key, ScriptRuntimePaths.Comparer), token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            string? effective = configuration.DefaultPath ?? entries.FirstOrDefault(entry => entry.Available)?.Path;
            string? directory = null;
            if (user is not null)
            {
                directory = ScriptRuntimePaths.InstallRoot(user, language);
            }
            ScriptRuntimeCatalog catalog = new(language, configuration.Revision, configuration.DefaultPath, effective, entries, directory, problem);
            using (gate.EnterScope())
            {
                if (problem is null && string.Equals(configurations[language].Revision, configuration.Revision, StringComparison.Ordinal))
                {
                    cache[language] = (DateTimeOffset.UtcNow, user?.Id, catalog);
                }
            }
            return catalog;
        }
        finally
        {
            discovery.Release();
        }
    }

    /// <summary>実在する対応版だけを登録し、指定された版から設定が変わっていれば上書きしません。</summary>
    /// <returns>更新後の候補一覧です。</returns>
    public async Task<ScriptRuntimeCatalog> ChangeAsync(ScriptRuntimeChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        string language = ScriptRuntimePaths.Language(change.Language);
        string? path = ScriptRuntimePaths.Normalize(change.Path);
        if (path is not null)
        {
            ScriptRuntimeEntry entry = await ScriptRuntimePaths.ProbeAsync(language, path, "registered", true, cancellationToken).ConfigureAwait(false);
            if (!entry.Available)
            {
                throw new InvalidOperationException(entry.Problem);
            }
        }
        else
        {
            if (!change.SetDefault)
            {
                throw new ArgumentException("script.runtime.absolutePathRequired", nameof(change));
            }
        }
        _ = Update(language, path, change.SetDefault, change.ExpectedRevision, true);
        return await CatalogAsync(new(language), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>検証済み導入先を登録し、導入中に利用者が変更していなければ既定も更新します。</summary>
    /// <returns>受付時の版から既定を更新できたかです。</returns>
    internal bool CompleteInstall(string language, string path, string? revision) => Update(language, path, true, revision, false);

    /// <summary>登録と既定を一つの小さい原子的記録として更新します。</summary>
    /// <returns>要求された既定選択を適用できたかです。</returns>
    private bool Update(string language, string? path, bool setDefault, string? expectedRevision, bool requireRevision)
    {
        using (gate.EnterScope())
        {
            ScriptRuntimeConfiguration configuration = configurations[language];
            if (path is not null)
            {
                path = configuration.Resolve(path);
            }
            bool matches = string.Equals(configuration.Revision, expectedRevision, StringComparison.Ordinal);
            if (requireRevision && !matches)
            {
                throw new InvalidOperationException("script.runtime.configurationChanged");
            }
            string[] paths = configuration.Paths.ToArray();
            if (path is not null && !paths.Contains(path, ScriptRuntimePaths.Comparer))
            {
                if (64 <= paths.Length)
                {
                    throw new InvalidOperationException("script.runtime.registrationLimit");
                }
                paths = [.. paths, path];
            }
            string? selected = configuration.DefaultPath;
            if (setDefault && matches)
            {
                selected = path;
            }
            ScriptRuntimeConfiguration updated = new(Guid.NewGuid().ToString("N"), paths, selected, configuration.LegacyPaths);
            ScriptRecordFile.Write(ConfigurationPath(language), updated);
            configurations[language] = updated;
            cache.Remove(language);
            return setDefault && matches;
        }
    }

    /// <summary>明示値、設定済み既定、互換用の現行環境、自動検出の順に一回の実行先を確定します。</summary>
    /// <returns>選択後は変更しない絶対パスです。</returns>
    public async Task<string> ResolveAsync(string language, string? explicitPath, string? fallback = null, CancellationToken cancellationToken = default)
    {
        string? path = SnapshotPath(language, explicitPath, fallback);
        if (path is null)
        {
            path = (await CatalogAsync(new(language), cancellationToken).ConfigureAwait(false)).EffectivePath;
        }
        if (path is null || !File.Exists(path))
        {
            throw new InvalidOperationException("script.runtime.unavailable");
        }
        return path;
    }

    /// <summary>受付時の明示・既定設定を固定し、開始済み操作へ後の既定変更を遡及させません。</summary>
    /// <returns>明示された実行先、または探索が必要なnullです。</returns>
    internal string? SnapshotPath(string language, string? explicitPath, string? fallback = null)
    {
        language = ScriptRuntimePaths.Language(language);
        string? path = ScriptRuntimePaths.Normalize(explicitPath);
        using (gate.EnterScope())
        {
            path ??= configurations[language].DefaultPath;
            path ??= ScriptRuntimePaths.Normalize(fallback);
            if (path is not null)
            {
                path = configurations[language].Resolve(path);
            }
        }
        return path;
    }

    /// <summary>新しい解析には選択された実行先と一致する現在のパッケージ構成だけを使用します。</summary>
    /// <returns>現在の既定で解析するための環境です。</returns>
    public async Task<ScriptPackageEnvironment> ForAnalysisAsync(ScriptAnalysisRequest request, ScriptPackageStore packages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(packages);
        string csharp = "csharp";
        if (string.Equals(request.Language, csharp, StringComparison.Ordinal))
        {
            return packages.ForAnalysis(request);
        }
        bool python = string.Equals(request.Language, "python", StringComparison.Ordinal);
        EScriptPackageKind kind = EScriptPackageKind.Npm;
        if (python)
        {
            kind = EScriptPackageKind.Pip;
        }
        ScriptPackageEnvironmentInfo? current = packages.Current(kind);
        string? explicitPath = request.NodePath, fallback = current?.NodePath;
        if (python)
        {
            explicitPath = request.PythonPath;
            fallback = current?.PythonPath;
        }
        string path = await ResolveAsync(request.Language, explicitPath, fallback, cancellationToken).ConfigureAwait(false);
        if (fallback is not null && ScriptRuntimePaths.Comparer.Equals(path, SnapshotPath(request.Language, fallback)))
        {
            // 固定済みvenvやnpm環境は再構築せず、旧配置を参照する既存の不変環境を再使用します。
            path = fallback;
        }
        ScriptAnalysisRequest selected = request with { NodePath = path };
        if (python)
        {
            selected = request with { PythonPath = path };
        }
        return await BindAsync(selected, packages.ForAnalysis(selected), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存済みの不変パッケージ環境を優先し、標準環境の実行先だけを解決します。</summary>
    /// <returns>元の解析要求の指紋を変えず、選択された実行先を持つ環境です。</returns>
    public async Task<ScriptPackageEnvironment> BindAsync(ScriptAnalysisRequest request, ScriptPackageEnvironment environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);
        string csharp = "csharp";
        if (environment.Info is not null || string.Equals(request.Language, csharp, StringComparison.Ordinal))
        {
            return environment;
        }
        string? path = request.Language switch { "python" => request.PythonPath, _ => request.NodePath };
        return environment with { RuntimeExecutable = await ResolveAsync(request.Language, path, cancellationToken: cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>探索の待機資源を解放します。呼出元は実行停止後に破棄します。</summary>
    public void Dispose() => discovery.Dispose();

    /// <summary>二言語だけの固定ファイル名を生成します。</summary>
    /// <returns>Peerローカルの設定パスです。</returns>
    private string ConfigurationPath(string language) => Path.Combine(root, language + ".json");
}
