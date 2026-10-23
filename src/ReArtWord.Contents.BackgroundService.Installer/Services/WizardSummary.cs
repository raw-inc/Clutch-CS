using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>実行前に固定した設定を利用者向け一覧へ整形します。</summary>
internal static class WizardSummary
{
    /// <summary>対象、保存、権限と表示設定を省略せず示します。</summary>
    /// <returns>最終確認用の一覧です。</returns>
    public static string Create(InstallationViewModel settings)
    {
        StringBuilder text = new();
        Localization.InstallerText labels = new(settings.Japanese);
        text.AppendLine(labels[settings.Operation.ToString()]);
        if (settings.Operation == EInstallerOperation.Install)
        {
            Contracts.Installation.InstallationSettings value = settings.Settings();
            text.AppendLine(labels["ClusterName"] + ": " + value.ClusterId);
            text.AppendLine(labels["Network"] + ": " + value.Network.BindAddress);
            text.AppendLine(labels["Port"] + ": " + value.Network.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            text.AppendLine(labels["Host"] + ": " + value.HostId);
            text.AppendLine(labels["Peer"] + ": " + value.PeerId);
            text.AppendLine(settings.Text("Priority: ", "優先度：") + value.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
            text.AppendLine(value.Root);
            string startup = settings.Text("Starts with the computer (all users)", "コンピューター起動時に開始（すべてのユーザー）");
            if (value.PerUser)
            {
                startup = settings.Text("Starts at sign-in (current user)", "サインイン時に開始（現在のユーザー）");
            }
            text.AppendLine(startup);
            if (OperatingSystem.IsWindows())
            {
                text.AppendLine(settings.Text("Windows Firewall: allow Clutch-CS TCP / UDP on the selected network", "Windows Firewall：選択したネットワークでClutch-CSのTCP / UDP通信を許可"));
            }
            AppendClient(text, settings, value, labels);
            return text.ToString();
        }
        foreach (ObservedTarget target in settings.Selected)
        {
            text.AppendLine(target.DisplayName(settings.Japanese));
            if (target.Installation is not null)
            {
                text.AppendLine("  " + target.Installation.Settings.Root);
            }
        }
        if (settings.Operation == EInstallerOperation.Uninstall)
        {
            if (settings.Components.RemoveService)
            {
                text.AppendLine(settings.Text("Saved data: ", "保存データ：") + Retention(settings, settings.KeepDatabase));
            }
            if (settings.Components.RemoveWebClient)
            {
                text.AppendLine(settings.Text("Remove the web client on this computer", "このPCの Web クライアントを削除する"));
                text.AppendLine(settings.Components.Existing!.Root);
            }
            text.AppendLine(settings.Text("Settings: ", "設定：") + Retention(settings, settings.KeepConfiguration));
        }
        if (settings.Operation == EInstallerOperation.Repair)
        {
            if (settings.Repair.RestoreArchive)
            {
                text.AppendLine(labels["ArchiveRestore"]);
                text.AppendLine(settings.Repair.Archive.Path);
                if (settings.Repair.Archive.Preview is { } preview)
                {
                    text.AppendLine(labels["ArchiveSource"] + ": " + preview.SourceCluster + " / " + preview.SourcePeer);
                    text.AppendLine(labels["ArchiveAdded"] + ": " + string.Join(", ", preview.Added));
                    text.AppendLine(labels["ArchiveReplaced"] + ": " + string.Join(", ", preview.Replaced));
                    text.AppendLine(labels["ArchiveDeleted"] + ": " + string.Join(", ", preview.Deleted));
                    text.AppendLine(labels["ArchiveLogs"] + ": " + preview.LogEntries.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    text.AppendLine(labels["ArchiveRestoreHelp"]);
                }
                return text.ToString();
            }
            if (settings.Repair.Reinstall)
            {
                text.AppendLine(labels["Reinstall"]);
                string data = labels["KeepDb"];
                if (!settings.KeepDatabase)
                {
                    data = labels["Resync"];
                }
                text.AppendLine(data);
            }
            else
            {
                text.AppendLine(labels["AutoRepair"]);
            }
        }
        if (settings.Operation == EInstallerOperation.Update)
        {
            foreach (ObservedWebClient client in settings.SelectedWebClients)
            {
                text.AppendLine(client.DisplayName(settings.Japanese));
            }
            if (settings.SelectedWebClients.Count != 0)
            {
                text.AppendLine(settings.Text("Web client settings and startup method are kept. The management page disconnects briefly during the update.",
                    "Webクライアントの設定と起動方法は保持します。更新中は管理画面への接続が一時的に切れます。"));
            }
            if (settings.AllowActive)
            {
                text.AppendLine(labels["Active"]);
            }
        }
        return text.ToString();
    }

    /// <summary>新規導入・既存利用・導入なしを確認画面で区別します。</summary>
    private static void AppendClient(StringBuilder text, InstallationViewModel settings, InstallationSettings value, Localization.InstallerText labels)
    {
        if (value.WebClient is { Install: false, ExistingRoot: null })
        {
            text.AppendLine(settings.Text("Web client: do not install", "Web クライアント：インストールしない"));
            return;
        }
        if (value.WebClient is { ExistingRoot: not null } existing)
        {
            text.AppendLine(settings.Text("Web client: use the installed copy", "Web クライアント：インストール済みのものを使用"));
            text.AppendLine(existing.ExistingRoot);
            text.AppendLine(labels["WebClientPort"] + ": " + existing.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        text.AppendLine(settings.Text("Web client: install", "Web クライアント：インストールする"));
        text.AppendLine(Infrastructure.Installation.WebClientCatalog.DefaultRoot(!value.WebClient!.RunAsService));
        string startup = labels["WebClientServiceStartup"];
        if (!value.WebClient!.RunAsService)
        {
            startup = labels["WebClientUserStartup"];
        }
        text.AppendLine(startup);
        text.AppendLine(labels["WebClientPort"] + ": " + value.WebClient!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        text.AppendLine(settings.Text("Title: ", "タイトル：") + (value.Appearance.Title ?? ClientAppearance.DefaultTitle));
        text.AppendLine(settings.Text("Subtitle: ", "サブタイトル：") + (value.Appearance.Subtitle ?? ClientAppearance.DefaultSubtitle));
        text.AppendLine(settings.Text("Icon: ", "アイコン：") + (value.Appearance.IconPath ?? settings.Text("Default", "既定")));
        text.AppendLine(labels["Favicon"] + ": " + (value.Appearance.FaviconPath ?? settings.Text("Default", "既定")));
    }

    /// <summary>保持指定を利用者向けの動詞にします。</summary>
    /// <returns>保存または削除の表示です。</returns>
    private static string Retention(InstallationViewModel settings, bool keep)
    {
        if (keep)
        {
            return settings.Text("Keep", "残す");
        }
        return settings.Text("Delete", "削除する");
    }
}
