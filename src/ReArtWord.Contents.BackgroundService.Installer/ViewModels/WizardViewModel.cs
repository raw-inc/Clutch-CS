using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReArtWord.Contents.BackgroundService.Installer.Localization;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.Services;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>確認画面を挟んだウィザード遷移と一回の実行を管理します。</summary>
internal sealed partial class WizardViewModel : ObservableObject
{
    private readonly IInstallerInteraction interaction;
    private readonly Func<InstallationViewModel, IProgress<Contracts.Execution.OperationProgress>, CancellationToken, Task<WizardResult>> execute;
    private readonly InstallerText[] languages = [new(false), new(true)];

    /// <summary>OS対話と入力を注入し、Viewへの参照を持ちません。</summary>
    public WizardViewModel(InstallationViewModel settings, DiscoveryViewModel discovery, IInstallerInteraction interaction,
        Func<InstallationViewModel, IProgress<Contracts.Execution.OperationProgress>, CancellationToken, Task<WizardResult>>? execute = null)
    {
        Settings = settings;
        Discovery = discovery;
        this.interaction = interaction;
        this.execute = execute ?? WizardExecution.ExecuteAsync;
    }

    /// <summary>導入・保守の入力です。</summary>
    public InstallationViewModel Settings
    {
        get;
    }

    /// <summary>通信経路と対象選択です。</summary>
    public DiscoveryViewModel Discovery
    {
        get;
    }

    /// <summary>実行専用画面の進捗と詳細履歴です。</summary>
    public ExecutionProgressViewModel Execution
    {
        get;
    } = new();

    /// <summary>表示する段階です。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Heading)), NotifyPropertyChangedFor(nameof(NextLabel)), NotifyPropertyChangedFor(nameof(CanBack)),
     NotifyPropertyChangedFor(nameof(IsLanguage)), NotifyPropertyChangedFor(nameof(IsOperation)), NotifyPropertyChangedFor(nameof(IsLicense)),
     NotifyPropertyChangedFor(nameof(IsNetwork)), NotifyPropertyChangedFor(nameof(IsTargets)), NotifyPropertyChangedFor(nameof(IsIdentity)), NotifyPropertyChangedFor(nameof(IsOptions)),
     NotifyPropertyChangedFor(nameof(IsAdvanced)), NotifyPropertyChangedFor(nameof(IsAppearance)), NotifyPropertyChangedFor(nameof(IsReview)),
     NotifyPropertyChangedFor(nameof(IsResult)), NotifyPropertyChangedFor(nameof(IsMaintenance)), NotifyPropertyChangedFor(nameof(IsExecuting)), NotifyPropertyChangedFor(nameof(IsComponents))]
    public partial EWizardPage Page
    {
        get; set;
    }

    /// <summary>英語または日本語です。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Text)), NotifyPropertyChangedFor(nameof(Heading)), NotifyPropertyChangedFor(nameof(NextLabel))]
    public partial int LanguageIndex
    {
        get; set;
    }

    /// <summary>四つの操作から選択します。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsInstall)), NotifyPropertyChangedFor(nameof(IsUpdate)), NotifyPropertyChangedFor(nameof(IsRepair)), NotifyPropertyChangedFor(nameof(IsUninstall))]
    public partial int OperationIndex
    {
        get; set;
    }

    /// <summary>一回の処理を実行中です。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Idle)), NotifyPropertyChangedFor(nameof(CanBack)), NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool Busy
    {
        get; set;
    }

    /// <summary>画面内で示す入力または操作の問題です。</summary>
    [ObservableProperty]
    public partial string Error
    {
        get; set;
    } = string.Empty;

    /// <summary>処理中の段階です。</summary>
    [ObservableProperty]
    public partial string Progress
    {
        get; set;
    } = string.Empty;

    /// <summary>確認画面に固定した一覧です。</summary>
    [ObservableProperty]
    public partial string ReviewText
    {
        get; set;
    } = string.Empty;

    /// <summary>実行結果の一覧です。</summary>
    [ObservableProperty]
    public partial string Results
    {
        get; set;
    } = string.Empty;

    /// <summary>必要な場合に展開する診断情報です。</summary>
    [ObservableProperty]
    public partial string ResultDetails
    {
        get; set;
    } = string.Empty;

    /// <summary>再起動の個別確認です。</summary>
    [ObservableProperty]
    public partial bool RestartConfirmed
    {
        get; set;
    }

    /// <summary>初回導入が実Readyへ達したかです。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NextLabel))]
    public partial bool Installed
    {
        get; set;
    }

    /// <summary>現在言語の表示文言です。</summary>
    public InstallerText Text => languages[LanguageIndex];

    /// <summary>入力を変更できるかです。</summary>
    public bool Idle => !Busy;

    /// <summary>戻る操作を許可するかです。</summary>
    public bool CanBack => !Busy && Page != EWizardPage.Language && Page != EWizardPage.Result && !IsExecuting;

    /// <summary>初回導入の選択です。</summary>
    public bool IsInstall => (EInstallerOperation)OperationIndex == EInstallerOperation.Install;

    /// <summary>更新の選択です。</summary>
    public bool IsUpdate => (EInstallerOperation)OperationIndex == EInstallerOperation.Update;

    /// <summary>修復の選択です。</summary>
    public bool IsRepair => (EInstallerOperation)OperationIndex == EInstallerOperation.Repair;

    /// <summary>削除の選択です。</summary>
    public bool IsUninstall => (EInstallerOperation)OperationIndex == EInstallerOperation.Uninstall;

    /// <summary>言語選択の表示条件です。</summary>
    public bool IsLanguage => Page == EWizardPage.Language;

    /// <summary>動作選択の表示条件です。</summary>
    public bool IsOperation => Page == EWizardPage.Operation;

    /// <summary>削除する構成要素の選択です。</summary>
    public bool IsComponents => Page == EWizardPage.Components;

    /// <summary>ライセンスの表示条件です。</summary>
    public bool IsLicense => Page == EWizardPage.License;

    /// <summary>対象選択の表示条件です。</summary>
    public bool IsNetwork => Page == EWizardPage.Network;

    /// <summary>クラスタ・対象選択の表示条件です。</summary>
    public bool IsTargets => Page == EWizardPage.Targets;

    /// <summary>識別名の表示条件です。</summary>
    public bool IsIdentity => Page == EWizardPage.Identity;

    /// <summary>設定方式の表示条件です。</summary>
    public bool IsOptions => Page == EWizardPage.Options;

    /// <summary>高度な設定の表示条件です。</summary>
    public bool IsAdvanced => Page == EWizardPage.Advanced;

    /// <summary>外観設定の表示条件です。</summary>
    public bool IsAppearance => Page == EWizardPage.Appearance;

    /// <summary>内容確認の表示条件です。</summary>
    public bool IsReview => Page == EWizardPage.Review;

    /// <summary>実行中の詳細を開いて確認できる専用画面です。</summary>
    public bool IsExecuting => Page == EWizardPage.Executing;

    /// <summary>結果の表示条件です。</summary>
    public bool IsResult => Page == EWizardPage.Result;

    /// <summary>保守設定の表示条件です。</summary>
    public bool IsMaintenance => Page == EWizardPage.Maintenance;

    /// <summary>画面の見出しです。</summary>
    public string Heading => Text[Page switch { EWizardPage.Language => "Welcome", EWizardPage.Operation => "Operation", EWizardPage.Components => "UninstallComponents", EWizardPage.License => "Terms", EWizardPage.Network => "Network", EWizardPage.Targets => "Targets", EWizardPage.Identity => "Identity", EWizardPage.Options => "Options", EWizardPage.Advanced => "Privileges", EWizardPage.Appearance => "Appearance", EWizardPage.Review => "Review", EWizardPage.Executing => "Running" + Settings.Operation, EWizardPage.Result => "Results", _ => "Maintenance" }];

    /// <summary>次へbuttonの目的を示します。</summary>
    public string NextLabel => Text[Page switch { EWizardPage.Review => Settings.Operation.ToString(), EWizardPage.Result when Installed => "RestartLater", EWizardPage.Result => "Finish", _ => "Next" }];

    /// <summary>言語の選択を入力のエラー説明へ反映します。</summary>
    partial void OnLanguageIndexChanged(int value) => Settings.Japanese = value == 1;

    /// <summary>動作を切り替えたときの確認状態を破棄します。</summary>
    partial void OnOperationIndexChanged(int value)
    {
        Settings.Operation = (EInstallerOperation)value;
        Settings.Confirmed = false;
    }

    /// <summary>選択した動作を明示して切り替えます。</summary>
    [RelayCommand]
    private void ChooseOperation(EInstallerOperation operation) => OperationIndex = (int)operation;

    /// <summary>推奨設定と高度な設定を切り替えます。</summary>
    [RelayCommand]
    private void ChooseSettingsMode(bool advanced) => Settings.Advanced = advanced;

    /// <summary>次の段階へ進み、確認後だけ実行します。</summary>
    /// <returns>画面遷移完了です。</returns>
    [RelayCommand(CanExecute = nameof(Idle))]
    private Task NextAsync(CancellationToken cancellationToken = default) => GuardAsync(AdvanceAsync, cancellationToken);

    /// <summary>戻った後に最終確認を取り直します。</summary>
    [RelayCommand]
    private void Back()
    {
        if (!CanBack)
        {
            return;
        }
        EWizardPage[] route = WizardNavigation.Route(Settings.Operation, Settings.Advanced, Settings.Components.Install, Settings.Components.RemoveService);
        Page = route[Math.Max(0, Array.IndexOf(route, Page) - 1)];
        Settings.Confirmed = false;
        Error = string.Empty;
    }

    /// <summary>進行中は終了を拒否します。</summary>
    [RelayCommand]
    private void Close()
    {
        if (Busy)
        {
            Error = Text["BusyClose"];
            return;
        }
        interaction.Close();
    }

    /// <summary>観測対象を更新します。</summary>
    /// <returns>走査完了です。</returns>
    [RelayCommand]
    private Task ScanAsync(CancellationToken cancellationToken = default) => GuardAsync(async token =>
    {
        Progress = Text["Searching"];
        await Discovery.ScanAsync(token).ConfigureAwait(true);
    }, cancellationToken);

    /// <summary>停止したローカル配置を選択します。</summary>
    /// <returns>選択完了です。</returns>
    [RelayCommand]
    private Task AddLocalAsync(CancellationToken cancellationToken = default) => GuardAsync(token => Discovery.BrowseLocalAsync(interaction, token), cancellationToken);

    /// <summary>入力欄に対応するfolderまたは画像を選択します。</summary>
    /// <returns>選択結果の反映です。</returns>
    [RelayCommand]
    private Task BrowseAsync(string field, CancellationToken cancellationToken = default) => GuardAsync(token => Settings.BrowseAsync(interaction, field, token), cancellationToken);

    /// <summary>確認済みの場合だけこのPCを再起動します。</summary>
    /// <returns>OSへの要求完了です。</returns>
    [RelayCommand]
    private Task RestartAsync(CancellationToken cancellationToken = default) => GuardAsync(async token =>
    {
        if (!RestartConfirmed || !Installed || !IsResult)
        {
            throw new InvalidOperationException(Text["RestartConfirm"]);
        }
        Progress = Text["Restarting"];
        await interaction.RestartAsync(token).ConfigureAwait(true);
    }, cancellationToken);

    /// <summary>導入済みクライアントを開きます。</summary>
    /// <returns>起動結果の反映です。</returns>
    [RelayCommand]
    private Task OpenClientAsync(CancellationToken cancellationToken = default) => GuardAsync(token => Settings.Components.OpenAsync(interaction, token), cancellationToken);

    /// <summary>OS対話と実行中の例外を画面へ集約します。</summary>
    /// <returns>UI操作完了です。</returns>
    private async Task GuardAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        if (Busy)
        {
            return;
        }
        Busy = true;
        Error = string.Empty;
        try
        {
            await action(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or FormatException or OperationCanceledException)
        {
            Error = exception.Message;
            if (IsExecuting)
            {
                Results = Text["Progress.outcome-Unknown"];
                ResultDetails = exception.Message;
                Page = EWizardPage.Result;
            }
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>副作用の前に確認画面を必ず通します。</summary>
    /// <returns>次の段階の準備完了です。</returns>
    private async Task AdvanceAsync(CancellationToken cancellationToken = default)
    {
        if (IsLicense && !Settings.LicenseConfirmed)
        {
            return;
        }
        if (IsResult)
        {
            Busy = false;
            interaction.Close();
            return;
        }
        if (IsNetwork)
        {
            Progress = Text["Searching"];
            await Discovery.ScanAsync(cancellationToken).ConfigureAwait(true);
        }
        if (IsTargets)
        {
            Discovery.CaptureTargets();
        }
        EWizardPage[] route = WizardNavigation.Route(Settings.Operation, Settings.Advanced, Settings.Components.Install, Settings.Components.RemoveService);
        EWizardPage next = route[Array.IndexOf(route, Page) + 1];
        if (next is EWizardPage.Options or EWizardPage.Components)
        {
            await Settings.Components.RefreshAsync(IsUninstall, cancellationToken).ConfigureAwait(true);
        }
        if (next == EWizardPage.Review)
        {
            await WizardExecution.PrepareAsync(Settings, cancellationToken).ConfigureAwait(true);
            ReviewText = WizardSummary.Create(Settings);
        }
        if (IsReview)
        {
            if (!Settings.Confirmed)
            {
                throw new InvalidDataException(Text["ConfirmError"]);
            }
            Execution.Start(Text);
            Page = EWizardPage.Executing;
            WizardResult result = await execute(Settings, new Progress<Contracts.Execution.OperationProgress>(Execution.Report), cancellationToken).ConfigureAwait(true);
            (Results, ResultDetails, Settings.Components.InstalledClient) = result;
            Installed = Settings.Installed;
        }
        Page = next;
    }
}
