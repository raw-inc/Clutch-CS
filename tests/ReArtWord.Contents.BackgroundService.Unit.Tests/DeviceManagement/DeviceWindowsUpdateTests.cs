using System.Runtime.Versioning;
using Microsoft.Win32;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WindowsUpdate;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WindowsUpdate;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>実更新を開始・停止せず、ポリシーの部分保存と確認を検証します。</summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceWindowsUpdateTests
{
    /// <summary>期限の優先を含めて確認し、進行中の更新を完了済みと扱いません。</summary>
    /// <returns>要求された更新制限の反映確認です。</returns>
    [Fact]
    public async Task AppliesManualModeAndRequiresDeadlinePoliciesToBeDisabledAsync()
    {
        Access access = new();
        WindowsUpdateSettings settings = new(access);
        DeviceSnapshot before = settings.Read();
        DeviceChangeRequest request = Request(before, true);
        DeviceChangeRules.Validate(request);
        Assert.Equal(EOutcome.Succeeded, (await settings.ApplyAsync(request, before).ConfigureAwait(true)).Outcome);
        Assert.Equal(1, access.Writes);
        Assert.True(DeviceChangeConfirmation.Matches(request, settings.Read()));
        Assert.True(access.State.RebootRequired);
        Assert.True(access.State.InstallInProgress);
        access.State = access.State with { Deadlines = new Dictionary<string, int?> { ["SetComplianceDeadlineForQU"] = 1 } };
        Assert.False(DeviceChangeConfirmation.Matches(request, settings.Read()));
    }

    /// <summary>未対応エディション、外部管理、変更後の版を拒否します。</summary>
    /// <returns>OS変更前の拒否です。</returns>
    [Theory]
    [InlineData("edition")]
    [InlineData("managed")]
    [InlineData("changed")]
    public async Task DoesNotWriteUnsupportedOrUnconfirmedTargetsAsync(string scenario)
    {
        Access access = new();
        access.State = scenario switch
        {
            "edition" => access.State with { Supported = false, Edition = "Core" },
            "managed" => access.State with { Restrictions = ["Device.UpdateDomainManaged"] },
            _ => access.State,
        };
        WindowsUpdateSettings settings = new(access);
        DeviceSnapshot before = settings.Read();
        string changed = "changed";
        if (string.Equals(scenario, changed, StringComparison.Ordinal))
        {
            access.State = access.State with { NoAutoUpdate = 1 };
        }
        Assert.Equal(EOutcome.NotExecuted, (await settings.ApplyAsync(Request(before, true), before).ConfigureAwait(true)).Outcome);
        Assert.Equal(0, access.Writes);
    }

    /// <summary>不完全な保存を成功にせず、完了したGPO保存を残します。</summary>
    /// <returns>部分変更の報告です。</returns>
    [Fact]
    public async Task PreservesPartialPolicyFailureAsync()
    {
        Access access = new() { Fail = true };
        WindowsUpdateSettings settings = new(access);
        DeviceSnapshot before = settings.Read();
        DeviceChangeResult result = await settings.ApplyAsync(Request(before, true), before).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(["updates.localPolicySaved"], result.CompletedSteps);
    }

    /// <summary>隔離したHKCUのキーで、実Windowsのポリシーを触らず保存範囲を確認します。</summary>
    [Fact]
    public void WritesOnlyTheRequestedPolicyValuesInAnIsolatedRegistryRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string path = @"SOFTWARE\ReArtWord\ContentsTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using RegistryKey root = Registry.CurrentUser.CreateSubKey(path);
            using RegistryKey automatic = root.CreateSubKey(WindowsUpdateAccess.AutomaticPath);
            automatic.SetValue("AUOptions", 4, RegistryValueKind.DWord);
            automatic.SetValue("Unrelated", "retained", RegistryValueKind.String);
            LocalUpdatePolicy.Set(root, true);
            Assert.Equal(1, automatic.GetValue("NoAutoUpdate"));
            using RegistryKey update = root.OpenSubKey(WindowsUpdateAccess.UpdatePath)!;
            foreach (string name in WindowsUpdateAccess.DeadlineNames)
            {
                Assert.Equal(0, update.GetValue(name));
            }
            LocalUpdatePolicy.Set(root, false);
            Assert.Null(automatic.GetValue("NoAutoUpdate"));
            Assert.Equal(4, automatic.GetValue("AUOptions"));
            Assert.Equal("retained", automatic.GetValue("Unrelated"));
            Assert.All(WindowsUpdateAccess.DeadlineNames, name => Assert.Equal(0, update.GetValue(name)));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
    }

    /// <summary>実OSでは読取りだけを行います。</summary>
    [Fact]
    public void ReadsTheWindowsEditionAndUpdateStateWithoutChangingPolicy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        DeviceWindowsUpdateState value = new WindowsUpdateAccess().Read();
        Assert.NotEmpty(value.Edition);
        Assert.Equal(WindowsUpdateAccess.DeadlineNames.Count, value.Deadlines.Count);
    }

    /// <summary>内容版とOS設定の確認を含む要求を構成します。</summary>
    /// <returns>明示した更新モードです。</returns>
    private static DeviceChangeRequest Request(DeviceSnapshot before, bool manualOnly) => new(Guid.NewGuid(), "host", Guid.NewGuid(), EDeviceAction.WindowsUpdate,
        new(EDeviceArea.WindowsUpdate), before.Revision, DeviceSnapshots.Create(new DeviceWindowsUpdateChange(manualOnly)).Data, true);

    /// <summary>外部管理と部分失敗を再現するOS境界です。</summary>
    private sealed class Access : IWindowsUpdateAccess
    {
        internal DeviceWindowsUpdateState State
        {
            get; set;
        } = new("Professional", true, null, new Dictionary<string, int?> { ["SetComplianceDeadlineForQU"] = 1 }, [], true, true);

        internal int Writes
        {
            get; private set;
        }

        internal bool Fail
        {
            get; init;
        }

        /// <summary>試験対象の設定を取得します。</summary>
        /// <returns>現在値です。</returns>
        public DeviceWindowsUpdateState Read() => State;

        /// <summary>ポリシー保存と途中の失敗を再現します。</summary>
        /// <returns>一度だけの書込み結果です。</returns>
        public Task WriteAsync(DeviceWindowsUpdateChange change, Action<string> completed, CancellationToken cancellationToken = default)
        {
            Writes++;
            completed("updates.localPolicySaved");
            if (Fail)
            {
                throw new IOException("policy failure");
            }
            int? value = change.ManualOnly ? 1
                                           : null;
            State = State with { NoAutoUpdate = value, Deadlines = State.Deadlines.ToDictionary(item => item.Key, _ => (int?)0, StringComparer.Ordinal) };
            completed("updates.effectivePolicySaved");
            return Task.CompletedTask;
        }
    }
}
