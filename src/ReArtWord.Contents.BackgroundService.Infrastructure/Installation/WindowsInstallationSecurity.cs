using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>サービスの配置は管理者と SYSTEM だけが変更でき、一般ユーザーには必要な読取り権限だけを与えます。</summary>
[SupportedOSPlatform("windows")]
public static class WindowsInstallationSecurity
{
    private static readonly SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly Lazy<SecurityIdentifier> trustedInstaller = new(() => (SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier)));

    /// <summary>安全な親の配下へ作成し、既存の対象にも同じ保護を適用します。</summary>
    public static void PrepareDirectory(string path, bool perUser, bool privateData = false)
    {
        string root = InstallationFiles.Root(path);
        SecurityIdentifier? user = CurrentUser(perUser);
        PrepareDefaultParent(root, user);
        Stack<DirectoryInfo> missing = new();
        DirectoryInfo? current = new(root);
        while (!current.Exists)
        {
            missing.Push(current);
            current = current.Parent ?? throw new UnauthorizedAccessException("The installation parent is unavailable.");
        }
        DirectoryInfo? ancestor = current;
        if (missing.Count == 0)
        {
            ancestor = current.Parent;
        }
        ValidateAncestors(ancestor, user);
        while (missing.TryPop(out DirectoryInfo? directory))
        {
            directory.Create(DirectoryDescriptor(user, privateData && string.Equals(directory.FullName, root, StringComparison.OrdinalIgnoreCase)));
        }
        ProtectDirectory(root, user, privateData);
    }

    /// <summary>管理者所有の標準共通フォルダーだけを保護し、ほかのクラスターの子要素は変更しません。</summary>
    private static void PrepareDefaultParent(string root, SecurityIdentifier? user)
    {
        string? parent = Path.GetDirectoryName(root);
        if (user is not null || parent is null || !string.Equals(parent, InstallationDefaults.WindowsServiceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (Directory.Exists(parent))
        {
            IdentityReference? owner = new DirectoryInfo(InstallationFiles.Root(parent)).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            if (owner is null || !Trusted(owner, null))
            {
                throw new UnauthorizedAccessException("The shared installation folder must be owned by administrators.");
            }
        }
        PrepareDirectory(parent, perUser: false);
    }

    /// <summary>所有する配置の全要素へ適用し、リンク経由の変更を拒否します。</summary>
    public static void ProtectTree(string path, bool perUser, bool privateData = false)
    {
        PrepareDirectory(path, perUser, privateData);
        SecurityIdentifier? user = CurrentUser(perUser);
        ProtectChildren(InstallationFiles.Root(path), user, privateData);
    }

    /// <summary>設定した権限を起動前に検証します。</summary>
    public static void ValidateTree(string path, bool perUser)
    {
        string root = InstallationFiles.Root(path);
        SecurityIdentifier? user = CurrentUser(perUser);
        ValidateDescriptor(new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), user, false);
        ValidateAncestors(new DirectoryInfo(root), user);
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            _ = InstallationFiles.Root(entry);
            if (Directory.Exists(entry))
            {
                ValidateTree(entry, perUser);
            }
            else
            {
                ValidateDescriptor(new FileInfo(entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), user, false);
            }
        }
    }

    /// <summary>外部に置くサービス設定は、親も含めて一般ユーザーが変更できないことを確認します。</summary>
    public static void ValidateFile(string path)
    {
        string file = InstallationFiles.Root(path);
        ValidateAncestors(new FileInfo(file).Directory, null);
        ValidateDescriptor(new FileInfo(file).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), null, false);
    }

    /// <summary>通常権限の導入では、そのユーザーだけに変更を許可します。</summary>
    /// <returns>ユーザー導入の所有者、サービス導入なら null です。</returns>
    private static SecurityIdentifier? CurrentUser(bool perUser)
    {
        if (!perUser)
        {
            return null;
        }
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new UnauthorizedAccessException("The installation user could not be identified.");
    }

    /// <summary>既存の親を変更せず、一般ユーザーによる置換が可能な配置先を拒否します。</summary>
    private static void ValidateAncestors(DirectoryInfo? directory, SecurityIdentifier? user)
    {
        // ユーザー導入は本人のプロファイルを使用します。サービスだけが管理者所有の親を必要とします。
        if (user is not null)
        {
            return;
        }
        while (directory is not null)
        {
            _ = InstallationFiles.Root(Path.Combine(directory.FullName, ".clutch-access-check"));
            ValidateDescriptor(directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), user, true);
            directory = directory.Parent;
        }
    }

    /// <summary>所有者と、対象そのものに適用される変更権限を確認します。</summary>
    private static void ValidateDescriptor(FileSystemSecurity security, SecurityIdentifier? user, bool ancestor)
    {
        if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null)
        {
            throw new UnauthorizedAccessException("The installation folder has unrestricted access.");
        }
        IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
        if (owner is null || !Trusted(owner, user))
        {
            throw new UnauthorizedAccessException("Choose an installation folder whose parent is controlled by administrators.");
        }
        FileSystemRights writes = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        if (!ancestor)
        {
            writes |= FileSystemRights.Write;
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                && (rule.FileSystemRights & writes) != 0 && !Trusted(rule.IdentityReference, user))
            {
                throw new UnauthorizedAccessException("The installation folder can be changed by an unprivileged account.");
            }
        }
    }

    /// <summary>変更を許可する管理主体を限定します。</summary>
    /// <returns>許可する管理主体かどうかです。</returns>
    private static bool Trusted(IdentityReference identity, SecurityIdentifier? user) =>
        identity == administrators || identity == system || identity == trustedInstaller.Value || (user is not null && identity == user);

    /// <summary>継承された書込み権限を除き、導入形態ごとの権限を作成します。</summary>
    /// <returns>ディレクトリと子要素の ACL です。</returns>
    internal static DirectorySecurity DirectoryDescriptor(SecurityIdentifier? user, bool privateData)
    {
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user ?? administrators);
        InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new(administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (user is not null)
        {
            security.AddAccessRule(new(user, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }
        if (user is null && !privateData)
        {
            security.AddAccessRule(new(users, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    /// <summary>対象を保持したまま所有者と ACL を更新します。</summary>
    private static void ProtectDirectory(string path, SecurityIdentifier? user, bool privateData)
    {
        _ = InstallationFiles.Root(path);
        using SafeFileHandle handle = WindowsInstallationFile.Open(path, true);
        WindowsInstallationFile.SetSecurity(handle, DirectoryDescriptor(user, privateData));
    }

    /// <summary>子の明示権限も残さず、ファイルはハンドルで固定して変更します。</summary>
    private static void ProtectChildren(string root, SecurityIdentifier? user, bool privateData)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            _ = InstallationFiles.Root(entry);
            if (Directory.Exists(entry))
            {
                ProtectDirectory(entry, user, privateData);
                ProtectChildren(entry, user, privateData);
            }
            else
            {
                using SafeFileHandle handle = WindowsInstallationFile.Open(entry, false);
                FileSecurity security = new();
                security.SetSecurityDescriptorBinaryForm(DirectoryDescriptor(user, privateData).GetSecurityDescriptorBinaryForm());
                WindowsInstallationFile.SetSecurity(handle, security);
            }
        }
    }
}
