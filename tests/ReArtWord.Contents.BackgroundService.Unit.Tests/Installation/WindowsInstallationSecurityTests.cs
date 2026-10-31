using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Installation;

/// <summary>専用の一時配置を使い、通常権限の利用とサービス配置の保護を確認します。</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsInstallationSecurityTests
{
    /// <summary>共通フォルダー自体を保護しても、既存の別配置の ACL は変更しません。</summary>
    [WindowsFact]
    public void ProtectingParentDoesNotRewriteExistingChildPermissions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string root = CreateRoot();
        try
        {
            string sibling = Directory.CreateDirectory(Path.Combine(root, "another-installation")).FullName;
            string file = Path.Combine(sibling, "retained.txt");
            File.WriteAllText(file, "retained");
            FileSecurity original = new FileInfo(file).GetAccessControl();
            original.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Modify, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(original);
            string before = new FileInfo(file).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            WindowsInstallationSecurity.PrepareDirectory(root, perUser: true);
            Assert.Equal(before, new FileInfo(file).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            Assert.Equal("retained", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>サービス更新も、ユーザーが差し替えられる保存先を準備段階で拒否します。</summary>
    [WindowsFact]
    public void ServiceUpdateRejectsUserControlledStagingBeforeCreatingAnOperation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using UpdateTestScope fixture = new();
        UpdateLayout layout = new(fixture.Options with { ServiceKind = EUpdateServiceKind.WindowsService }, Guid.NewGuid());
        Assert.Throws<UnauthorizedAccessException>(layout.CreateDirectories);
        Assert.False(Directory.Exists(layout.OperationRoot));
        Assert.False(Directory.Exists(layout.CandidateRoot));
    }

    /// <summary>本人の書込みは維持し、子ファイルに残った全員の変更権限も除去します。</summary>
    [WindowsFact]
    public void UserInstallationRemovesBroadWriteAccessAndKeepsOwnerAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string root = CreateRoot();
        try
        {
            string program = Path.Combine(root, "app");
            WindowsInstallationSecurity.PrepareDirectory(program, perUser: true);
            string file = Path.Combine(program, "client.txt");
            File.WriteAllText(file, "before");
            FileSecurity loose = new FileInfo(file).GetAccessControl();
            loose.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Modify, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(loose);
            Assert.Throws<UnauthorizedAccessException>(() => WindowsInstallationSecurity.ValidateTree(program, perUser: true));

            WindowsInstallationSecurity.ProtectTree(program, perUser: true);
            WindowsInstallationSecurity.ValidateTree(program, perUser: true);
            File.WriteAllText(file, "after");
            Assert.Equal("after", File.ReadAllText(file));
            Assert.True(new FileInfo(file).GetAccessControl().AreAccessRulesProtected);
            Assert.Equal(default, Rights(new FileInfo(file).GetAccessControl(), WellKnownSidType.WorldSid));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>管理者用サービスをユーザーが差し替えられる親の下へ配置しません。</summary>
    [WindowsFact]
    public void ServiceInstallationRejectsUserControlledParentWithoutChangingIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string root = CreateRoot();
        try
        {
            WindowsInstallationSecurity.PrepareDirectory(root, perUser: true);
            string before = new DirectoryInfo(root).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            string destination = Path.Combine(root, "service");
            Assert.Throws<UnauthorizedAccessException>(() => WindowsInstallationSecurity.PrepareDirectory(destination, perUser: false));
            Assert.False(Directory.Exists(destination));
            Assert.Equal(before, new DirectoryInfo(root).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>サービスの実行物は一般ユーザーが読み取れ、保存データは公開されません。</summary>
    [WindowsFact]
    public void ServiceProgramsAreReadableButNotWritableAndDataIsPrivate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        DirectorySecurity program = WindowsInstallationSecurity.DirectoryDescriptor(null, privateData: false);
        DirectorySecurity data = WindowsInstallationSecurity.DirectoryDescriptor(null, privateData: true);
        Assert.Equal(FileSystemRights.ReadAndExecute, Rights(program, WellKnownSidType.BuiltinUsersSid) & ~FileSystemRights.Synchronize);
        Assert.Equal(default, Rights(data, WellKnownSidType.BuiltinUsersSid));
        foreach (DirectorySecurity security in new[] { program, data })
        {
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(FileSystemRights.FullControl, Rights(security, WellKnownSidType.LocalSystemSid));
            Assert.Equal(FileSystemRights.FullControl, Rights(security, WellKnownSidType.BuiltinAdministratorsSid));
        }
    }

    /// <summary>別の配置と実体を共有するファイルの権限を変更しません。</summary>
    [WindowsFact]
    public void HardLinkedFileIsRejectedWithoutChangingTheOtherFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string root = CreateRoot();
        try
        {
            string outside = Path.Combine(root, "outside.txt");
            File.WriteAllText(outside, "retained");
            string before = new FileInfo(outside).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            string program = Path.Combine(root, "app");
            WindowsInstallationSecurity.PrepareDirectory(program, perUser: true);
            Assert.True(CreateHardLink(Path.Combine(program, "linked.txt"), outside, IntPtr.Zero));
            Assert.Throws<UnauthorizedAccessException>(() => WindowsInstallationSecurity.ProtectTree(program, perUser: true));
            Assert.Equal(before, new FileInfo(outside).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            Assert.Equal("retained", File.ReadAllText(outside));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>試験で作成したフォルダーだけを返します。</summary>
    /// <returns>一意の一時配置です。</returns>
    private static string CreateRoot() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "clutch-acl-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>指定グループに許可される権限を集計します。</summary>
    /// <returns>そのグループの許可権限です。</returns>
    private static FileSystemRights Rights(FileSystemSecurity security, WellKnownSidType identity)
    {
        SecurityIdentifier sid = new(identity, null);
        FileSystemRights rights = default;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference == sid && rule.AccessControlType == AccessControlType.Allow)
            {
                rights |= rule.FileSystemRights;
            }
        }
        return rights;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string path, string existing, IntPtr security);
}
