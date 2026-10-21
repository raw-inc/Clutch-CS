namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>操作と設定方式に応じたページ順を定義します。</summary>
internal static class WizardNavigation
{
    /// <summary>必要な設定だけを通る経路を返します。</summary>
    /// <returns>確認と結果を含む表示順です。</returns>
    public static EWizardPage[] Route(EInstallerOperation operation, bool advanced, bool configureClient = true, bool uninstallService = true)
    {
        if (operation == EInstallerOperation.Uninstall)
        {
            if (!uninstallService)
            {
                return [EWizardPage.Language, EWizardPage.Operation, EWizardPage.Components, EWizardPage.Maintenance, EWizardPage.Review, EWizardPage.Result];
            }
            return [EWizardPage.Language, EWizardPage.Operation, EWizardPage.Components, EWizardPage.Network, EWizardPage.Targets, EWizardPage.Maintenance, EWizardPage.Review, EWizardPage.Result];
        }
        if (operation != EInstallerOperation.Install)
        {
            return [EWizardPage.Language, EWizardPage.Operation, EWizardPage.Network, EWizardPage.Targets, EWizardPage.Maintenance, EWizardPage.Review, EWizardPage.Result];
        }
        if (advanced)
        {
            if (!configureClient)
            {
                return [EWizardPage.Language, EWizardPage.Operation, EWizardPage.License, EWizardPage.Network, EWizardPage.Targets, EWizardPage.Identity, EWizardPage.Options, EWizardPage.Advanced, EWizardPage.Review, EWizardPage.Result];
            }
            return [EWizardPage.Language, EWizardPage.Operation, EWizardPage.License, EWizardPage.Network, EWizardPage.Targets, EWizardPage.Identity, EWizardPage.Options, EWizardPage.Advanced, EWizardPage.Appearance, EWizardPage.Review, EWizardPage.Result];
        }
        return [EWizardPage.Language, EWizardPage.Operation, EWizardPage.License, EWizardPage.Network, EWizardPage.Targets, EWizardPage.Identity, EWizardPage.Options, EWizardPage.Review, EWizardPage.Result];
    }
}
