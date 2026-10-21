using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>実PCへ認証・資格情報保存せず、自動ログインの実行順と障害境界を検証します。</summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceLoginTests
{
    /// <summary>有効化を最後に行い、秘密値を設定のJSONや返却値へ含めません。</summary>
    [Fact]
    public void EnablesOnlyAfterCredentialAndAccountArePersisted()
    {
        using DeviceLoginCipher cipher = new();
        Access access = new();
        WindowsLoginSettings settings = new(access, cipher);
        DeviceSnapshot before = settings.Read();
        DeviceChangeRequest request = Request(before, cipher, access.User.Sid);
        DeviceChangeRules.Validate(request);
        DeviceChangeResult result = settings.Apply(request, before);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(["verify", "disable", "secret", "plaintext", "user", "enable"], access.Calls);
        Assert.True(DeviceChangeConfirmation.Matches(request, settings.Read()));
        Assert.All(access.Password!, value => Assert.Equal('\0', value));
        Assert.DoesNotContain("synthetic-login-password", request.Change.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-login-password", DeviceSnapshots.Create(result).Data.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>認証失敗、読取り変更、有効化できないアカウントを変更前に拒否します。</summary>
    [Theory]
    [InlineData("credentials")]
    [InlineData("account")]
    [InlineData("changed")]
    [InlineData("smartCard")]
    [InlineData("passwordPolicy")]
    [InlineData("unknownPolicy")]
    public void DoesNotWriteUnverifiedSettings(string scenario)
    {
        using DeviceLoginCipher cipher = new();
        Access access = new();
        access.State = scenario switch
        {
            "account" => access.State with { Users = [access.User with { Selectable = false }] },
            "smartCard" => access.State with { Restrictions = ["Device.LoginNoticePolicy", "Device.LoginSmartCardPolicy"] },
            "passwordPolicy" => access.State with { Restrictions = ["Device.LoginPasswordPolicy"] },
            "unknownPolicy" => access.State with { Restrictions = ["unknown-policy"] },
            _ => access.State,
        };
        WindowsLoginSettings settings = new(access, cipher);
        DeviceSnapshot before = settings.Read();
        string credentials = "credentials", changed = "changed";
        access.Valid = !string.Equals(scenario, credentials, StringComparison.Ordinal);
        if (string.Equals(scenario, changed, StringComparison.Ordinal))
        {
            access.State = access.State with { Domain = "another" };
        }
        Assert.Equal(EOutcome.NotExecuted, settings.Apply(Request(before, cipher, access.User.Sid), before).Outcome);
        Assert.DoesNotContain("disable", access.Calls);
        Assert.DoesNotContain("enable", access.Calls);
    }

    /// <summary>メッセージの維持と明示解除を区別し、どちらでもアカウントを保存できます。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavesAccountWithNoticeAndClearsItOnlyWhenRequested(bool clearNotice)
    {
        using DeviceLoginCipher cipher = new();
        Access access = new();
        access.State = access.State with { Restrictions = ["Device.LoginNoticePolicy"] };
        WindowsLoginSettings settings = new(access, cipher);
        DeviceSnapshot before = settings.Read();
        DeviceChangeRequest request = Request(before, cipher, access.User.Sid, clearNotice);
        DeviceChangeRules.Validate(request);
        Assert.Equal(EOutcome.Succeeded, settings.Apply(request, before).Outcome);
        Assert.True(DeviceChangeConfirmation.Matches(request, settings.Read()));
        if (clearNotice)
        {
            Assert.Equal(["verify", "disable", "secret", "plaintext", "user", "notice", "enable"], access.Calls);
            Assert.Empty(access.State.Restrictions);
            access.State = access.State with { Restrictions = ["Device.LoginNoticePolicy"] };
            Assert.False(DeviceChangeConfirmation.Matches(request, settings.Read()));
        }
        else
        {
            Assert.Equal(["verify", "disable", "secret", "plaintext", "user", "enable"], access.Calls);
            Assert.Equal(["Device.LoginNoticePolicy"], access.State.Restrictions);
        }
        Assert.All(access.Password!, value => Assert.Equal('\0', value));
    }

    /// <summary>解除失敗とポリシーの再適用は成功とせず、自動ログインを無効に保ちます。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LeavesLoginDisabledWhenRequestedNoticeRemovalCannotBeConfirmed(bool failWrite)
    {
        using DeviceLoginCipher cipher = new();
        Access access = new() { FailNotice = failWrite, RetainNotice = !failWrite };
        access.State = access.State with { Restrictions = ["Device.LoginNoticePolicy"] };
        WindowsLoginSettings settings = new(access, cipher);
        DeviceSnapshot before = settings.Read();
        DeviceChangeRequest request = Request(before, cipher, access.User.Sid, true);
        DeviceChangeResult result = settings.Apply(request, before);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.False(access.State.Enabled);
        Assert.DoesNotContain("enable", access.Calls);
        Assert.DoesNotContain("login.noticeCleared", result.CompletedSteps!);
        Assert.False(DeviceChangeConfirmation.Matches(request, settings.Read()));
        Assert.All(access.Password!, value => Assert.Equal('\0', value));
    }

    /// <summary>資格情報の確認に失敗したときは、解除指定があってもポリシーを変更しません。</summary>
    [Fact]
    public void RejectedCredentialDoesNotClearTheNotice()
    {
        using DeviceLoginCipher cipher = new();
        Access access = new() { Valid = false };
        access.State = access.State with { Restrictions = ["Device.LoginNoticePolicy"] };
        WindowsLoginSettings settings = new(access, cipher);
        DeviceSnapshot before = settings.Read();
        Assert.Equal(EOutcome.NotExecuted, settings.Apply(Request(before, cipher, access.User.Sid, true), before).Outcome);
        Assert.Equal(["verify"], access.Calls);
        Assert.Equal(["Device.LoginNoticePolicy"], access.State.Restrictions);
    }

    /// <summary>無効化操作へメッセージ削除を混ぜる要求を拒否します。</summary>
    [Fact]
    public void DisablingCannotClearTheNotice()
    {
        Assert.Throws<ArgumentException>(() => new DeviceLoginChange(false, ClearLogonNotice: true).Validate());
    }

    /// <summary>LSA保存失敗時は、自動ログインを有効化しません。</summary>
    [Fact]
    public void LeavesAutomaticLoginDisabledAfterSecretFailure()
    {
        using DeviceLoginCipher cipher = new();
        Access access = new() { FailSecret = true };
        WindowsLoginSettings settings = new(access, cipher);
        DeviceSnapshot before = settings.Read();
        DeviceChangeResult result = settings.Apply(Request(before, cipher, access.User.Sid), before);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(["login.disabled"], result.CompletedSteps);
        Assert.False(access.State.Enabled);
        Assert.DoesNotContain("enable", access.Calls);
        Assert.All(access.Password!, value => Assert.Equal('\0', value));
    }

    /// <summary>無効化はパスワード入力を要求せず、両方式の保存値を取り除きます。</summary>
    [Fact]
    public void DisablingRemovesProtectedAndLegacyCredentialsWithoutVerifyingAPassword()
    {
        using DeviceLoginCipher cipher = new();
        Access access = new();
        WindowsLoginSettings settings = new(access, cipher);
        DeviceSnapshot before = settings.Read();
        DeviceChangeRequest request = Request(before, cipher, access.User.Sid) with { Change = DeviceSnapshots.Create(new DeviceLoginChange(false)).Data };
        Assert.Equal(EOutcome.Succeeded, settings.Apply(request, before).Outcome);
        Assert.Equal(["disable", "secret", "plaintext"], access.Calls);
        Assert.True(DeviceChangeConfirmation.Matches(request, settings.Read()));
    }

    /// <summary>通常のパスワードフィールドを保存前の契約検証で拒否します。</summary>
    [Fact]
    public void RejectsPlaintextPasswordProperties()
    {
        using DeviceLoginCipher cipher = new();
        Access access = new();
        DeviceSnapshot before = new WindowsLoginSettings(access, cipher).Read();
        DeviceChangeRequest request = Request(before, cipher, access.User.Sid) with { Change = JsonSerializer.SerializeToElement(new { enabled = false, password = "synthetic-only" }) };
        Assert.Throws<JsonException>(() => DeviceChangeRules.Validate(request));
    }

    /// <summary>実Windowsでは候補と設定の読取りだけを行い、ログインは試行しません。</summary>
    [Fact]
    public void ReadsLocalUsersAndPasswordPresenceWithoutReturningSecretContents()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        DeviceLoginState state = new WindowsLoginAccess().Read();
        Assert.NotEmpty(state.Users);
        Assert.All(state.Users, user => Assert.StartsWith("S-1-", user.Sid, StringComparison.Ordinal));
        Assert.Contains(state.Users, user => user.Selectable);
    }

    /// <summary>合成資格情報を固定要求へ暗号化します。</summary>
    /// <returns>平文を含まない設定要求です。</returns>
    private static DeviceChangeRequest Request(DeviceSnapshot before, DeviceLoginCipher cipher, string sid, bool clearNotice = false)
    {
        DeviceChangeRequest request = new(Guid.NewGuid(), "host", Guid.NewGuid(), EDeviceAction.Login, new(EDeviceArea.Login), before.Revision,
            DeviceSnapshots.Create(new DeviceLoginChange(false)).Data, true);
        DeviceLoginCredential credential = LoginTestCredential.Seal(cipher.PublicKey, request, sid, "synthetic-login-password");
        return request with { Change = DeviceSnapshots.Create(new DeviceLoginChange(true, sid, credential, clearNotice)).Data };
    }

    /// <summary>平文の寿命と変更順序を記録する試験専用OSです。</summary>
    private sealed class Access : IWindowsLoginAccess
    {
        internal DeviceLoginUser User => State.Users[0];

        internal DeviceLoginState State
        {
            get; set;
        } = new(false, "", "", null, false, true, 1, [new("S-1-5-21-100-200-300-1001", "operator", "Operator", 0, true)], []);

        internal List<string> Calls
        {
            get;
        } = [];

        internal char[]? Password
        {
            get; private set;
        }

        internal bool Valid
        {
            get; set;
        } = true;

        internal bool FailSecret
        {
            get; init;
        }

        internal bool FailNotice
        {
            get; init;
        }

        internal bool RetainNotice
        {
            get; init;
        }

        /// <summary>保存した設定だけを返します。</summary>
        /// <returns>秘密値を持たない状態です。</returns>
        public DeviceLoginState Read() => State;

        /// <summary>渡された合成値と対象を確認します。</summary>
        /// <returns>設定済みの試験成否です。</returns>
        public bool Verify(DeviceLoginUser user, char[] password)
        {
            Assert.Equal(User.Sid, user.Sid);
            Assert.Equal("synthetic-login-password".ToCharArray(), password);
            Password = password;
            Calls.Add("verify");
            return Valid;
        }

        /// <summary>自動ログイン設定の順序を記録します。</summary>
        public void SetEnabled(bool enabled)
        {
            string call = enabled ? "enable"
                                  : "disable";
            Calls.Add(call);
            State = State with { Enabled = enabled };
        }

        /// <summary>秘密値の有無だけを保存します。</summary>
        public void SetPassword(char[]? password)
        {
            if (FailSecret)
            {
                throw new Win32Exception(5);
            }
            Calls.Add("secret");
            State = State with { ProtectedPasswordPresent = password is not null };
        }

        /// <summary>固定ユーザーと回数解除を保存します。</summary>
        public void SetUser(DeviceLoginUser user)
        {
            Calls.Add("user");
            State = State with { UserSid = user.Sid, UserName = user.Name, RemainingLogons = null };
        }

        /// <summary>旧方式の秘密値を削除します。</summary>
        public void RemovePlaintextPassword()
        {
            Calls.Add("plaintext");
            State = State with { PlaintextPasswordPresent = false };
        }

        /// <summary>明示されたメッセージ削除と、その失敗・再適用を再現します。</summary>
        public void ClearLogonNotice()
        {
            Calls.Add("notice");
            if (FailNotice)
            {
                throw new UnauthorizedAccessException();
            }
            if (!RetainNotice)
            {
                State = State with { Restrictions = [] };
            }
        }
    }
}
