using System.Net;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>動的入力理由の翻訳と原文値・既存例外型の保持を検証します。</summary>
public sealed class UiErrorTests
{
    /// <summary>同じ例外を言語変更後に表示し直し、フィールド名の書式記号は変更しません。</summary>
    /// <returns>型と描画時翻訳の確認です。</returns>
    [Fact]
    public async Task NumberErrorKeepsFieldAndTranslatesWhenRenderedAgain()
    {
        UiText ui = new(new(new BrowserTestRuntime()));
        string field = "Sensor {0} / 保存";
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => FormInput.Integer("bad", field, 2, 9));
        Func<string> render = ui.DeferredException(error);
        Assert.Equal("Enter an integer from 2 to 9 for Sensor {0} / 保存.", render());
        await ui.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("Sensor {0} / 保存には2～9の整数を入力してください。", render());
        Assert.Equal(field, UiError.From(error)!.Arguments[0]);
        object?[] arguments = ["original"];
        UiError attached = UiError.From(UiError.Attach(new FormatException(), "{0}", arguments))!;
        arguments[0] = "changed";
        Assert.Equal("original", attached.Arguments[0]);
    }

    /// <summary>Groupの行番号と入力名を、それぞれの理由書式の値として保持します。</summary>
    /// <returns>同じ基底例外と利用者文字列の確認です。</returns>
    [Fact]
    public async Task GroupValidationPreservesNamesAndRowNumber()
    {
        UiText ui = new(new(new BrowserTestRuntime()));
        string group = "Save {1} / 部屋";
        InvalidOperationException empty = Assert.Throws<InvalidOperationException>(() => ConfigurationForm.Build("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, [(group, "") ]));
        InvalidOperationException duplicate = Assert.Throws<InvalidOperationException>(() => ConfigurationForm.Build("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, [(group, "a"), (group, "b")]));
        InvalidOperationException row = Assert.Throws<InvalidOperationException>(() => ConfigurationForm.Build("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, [(group, "a"), ("", "b")]));
        Assert.Contains(group, ui.DeferredException(empty)(), StringComparison.Ordinal);
        Assert.Equal("Enter a name for Group 2.", ui.DeferredException(row)());
        await ui.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("グループ名「Save {1} / 部屋」が重複しています。", ui.DeferredException(duplicate)());
        Assert.Equal("グループ 2 行目の名前を入力してください。", ui.DeferredException(row)());
    }

    /// <summary>取得不能な定義を編集可能にせず、API由来の詳細理由を原文で残します。</summary>
    /// <returns>生理由と例外型の保持です。</returns>
    [Fact]
    public async Task MissingWorkflowPreservesRawDetailReason()
    {
        string reason = "Remote.reason:{0}/共有";
        WorkflowView view = new(null, null, null, DetailReason: reason);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => view.RequireSaved());
        UiText ui = new(new(new BrowserTestRuntime()));
        Func<string> render = ui.DeferredException(error);
        Assert.EndsWith(reason, render(), StringComparison.Ordinal);
        await ui.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("ワークフローの完全な保存本文を取得できません。編集・保存を開始しません。" + reason, render());
        Assert.Equal(reason, ui.DeferredException(new InvalidOperationException(reason))());
    }

    /// <summary>HTTP状態コードと例外型を維持し、一回の失敗を自動再送しません。</summary>
    /// <returns>HTTP境界と動的数値の翻訳です。</returns>
    [Fact]
    public async Task HttpFailureKeepsStatusAndUsesTypedDisplayArguments()
    {
        int requests = 0;
        using ClientHttpHandler handler = new((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(client);
        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetAsync<object>("api/test")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(1, requests);
        UiText ui = new(new(new BrowserTestRuntime()));
        Func<string> render = ui.DeferredException(error);
        Assert.Contains("(503)", render(), StringComparison.Ordinal);
        await ui.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("管理APIが操作を確認できませんでした (503)。副作用要求は同じ操作IDで照会してください。", render());
    }
}
