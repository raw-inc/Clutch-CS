using System.Net;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

/// <summary>更新専用の絶対rootと各操作の生成先を検証します。</summary>
public sealed class UpdateLayout
{
    private readonly FileAccessPolicy policy;
    private readonly bool windowsService;

    /// <summary>installation、staging、backupの独立した境界を固定します。</summary>
    public UpdateLayout(UpdatesOptions options, Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(options);
        windowsService = options.ServiceKind == EUpdateServiceKind.WindowsService;
        InstallationRoot = Root(options.InstallationRoot);
        StagingRoot = Root(options.StagingRoot);
        BackupRoot = Root(options.BackupRoot);
        string[] roots = [InstallationRoot, StagingRoot, BackupRoot];
        for (int first = 0; first < roots.Length; first++)
        {
            for (int second = first + 1; second < roots.Length; second++)
            {
                if (Contains(roots[first], roots[second]) || Contains(roots[second], roots[first]))
                {
                    throw new InvalidDataException("更新用の三rootは互いに重ならない絶対pathにしてください。");
                }
            }
        }
        if (operationId == Guid.Empty || options.StopTimeoutMilliseconds < 1 || options.StartTimeoutMilliseconds < 1)
        {
            throw new InvalidDataException("更新識別子と有限の停止・健康確認上限が必要です。");
        }
        _ = UpdateObserver.Binding(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClusterId);
        policy = new FileAccessPolicy(new FileSystemOptions(roots));
        foreach (string root in roots)
        {
            policy.ValidatePath(root, true);
        }
        OperationRoot = Path.Combine(StagingRoot, "operations", operationId.ToString("N"));
        PayloadRoot = Path.Combine(OperationRoot, "payload");
        CandidateRoot = Path.Combine(OperationRoot, "candidate");
        BackupPath = Path.Combine(BackupRoot, operationId.ToString("N"));
        JournalPath = Path.Combine(OperationRoot, "journal.json");
        JobPath = Path.Combine(OperationRoot, "job.json");
    }

    /// <summary>入替対象rootです。</summary>
    public string InstallationRoot
    {
        get;
    }

    /// <summary>更新管理専用rootです。</summary>
    public string StagingRoot
    {
        get;
    }

    /// <summary>旧リリース専用rootです。</summary>
    public string BackupRoot
    {
        get;
    }

    /// <summary>この操作の専用ディレクトリです。</summary>
    public string OperationRoot
    {
        get;
    }

    /// <summary>分割転送するペイロードの保存先です。</summary>
    public string PayloadRoot
    {
        get;
    }

    /// <summary>全チェックサムを確認してから切り替える候補です。</summary>
    public string CandidateRoot
    {
        get;
    }

    /// <summary>この操作が退避する旧リリースです。</summary>
    public string BackupPath
    {
        get;
    }

    /// <summary>単独実行と常駐プロセスで共有するローカル状態です。</summary>
    public string JournalPath
    {
        get;
    }

    /// <summary>単独実行へ渡す固定入力です。</summary>
    public string JobPath
    {
        get;
    }

    /// <summary>管理rootと操作ディレクトリを境界検証後に作ります。</summary>
    public void CreateDirectories()
    {
        Validate(OperationRoot);
        Validate(BackupRoot, true);
        if (OperatingSystem.IsWindows() && windowsService)
        {
            WindowsInstallationSecurity.PrepareDirectory(StagingRoot, perUser: false, privateData: true);
            WindowsInstallationSecurity.PrepareDirectory(BackupRoot, perUser: false, privateData: true);
            WindowsInstallationSecurity.PrepareDirectory(OperationRoot, perUser: false, privateData: true);
            return;
        }
        Directory.CreateDirectory(OperationRoot);
        Directory.CreateDirectory(BackupRoot);
    }

    /// <summary>更新候補と切戻し先にもサービス配置の保護を適用します。</summary>
    public void ProtectProgram(string path)
    {
        Validate(path, true);
        if (OperatingSystem.IsWindows() && windowsService)
        {
            WindowsInstallationSecurity.ProtectTree(path, perUser: false);
            WindowsInstallationSecurity.ValidateTree(path, perUser: false);
        }
    }

    /// <summary>操作直前に祖先変更と許可範囲を再確認します。</summary>
    public void Validate(string path, bool allowRoot = false)
    {
        policy.ValidatePath(path, allowRoot);
    }

    /// <summary>explicit root配下にあるかを区切り込みで判定します。</summary>
    /// <returns>同一または子孫のパスかです。</returns>
    public static bool Contains(string root, string path)
    {
        StringComparison comparison = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            comparison = StringComparison.OrdinalIgnoreCase;
        }
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalized, parent, comparison) || normalized.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>volume自体を操作対象にせず明示絶対rootを確認します。</summary>
    /// <returns>正規化された専用rootです。</returns>
    private static string Root(string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("更新rootは絶対pathで指定してください。");
        }
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(normalized)!), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("volume全体を更新rootにできません。");
        }
        return normalized;
    }
}
