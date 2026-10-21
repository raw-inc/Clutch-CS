// 更新処理自身が結果を保持するため、ここでは予期しない起動失敗だけを表示します。
window.contentsBrowser.registerPwa().catch(error =>
{
    const notice = document.getElementById('blazor-error-ui');
    notice.hidden = false;
    notice.textContent = error.message;
});
