import { node, input, select, field, heading, action, badge, checkbox } from './dom.js';
import { createReader, readStatus, noSelection } from './loader.js';
import { sealLoginCredentialAsync } from './login-credential.js';

const noticePolicy = 'Device.LoginNoticePolicy';

/** 有効設定と、動作に必要な資格情報の確認を分けます。
 * @returns 自動ログインの現在表示です。
 */
export function loginStatus(state)
{
    if (state.enabled === false)
    {
        return 'Automatic login disabled';
    }
    const available = state.users.some(user => user.selectable && user.sid === state.userSid);
    if (state.enabled && available && state.remainingLogons !== 0 && state.protectedPasswordPresent === true && !state.plaintextPasswordPresent && !hasBlockingPolicy(state))
    {
        if (state.restrictions.includes(noticePolicy))
        {
            return 'Automatic login account saved; login notice prevents automatic login';
        }
        return 'Automatic login configured';
    }
    return 'Automatic login configuration is unconfirmed';
}

/** 資格情報や暗号文を確認画面へ展開せず、対象と作用だけを比較します。
 * @returns 自動ログインの変更前後です。
 */
export function loginPreview(item, t = key => key)
{
    const state = item.before.data;
    const before = `${t(loginStatus(state))}\n${loginAccount(state, t)}`;
    if (!item.change.enabled)
    {
        return [before, `${t('Automatic login disabled')}\n${t('The saved automatic login password will be removed.')}`];
    }
    const user = state.users.find(user => user.sid === item.change.userSid);
    const status = state.restrictions.includes(noticePolicy) && !item.change.clearLogonNotice
        ? 'Automatic login account saved; login notice prevents automatic login' : 'Automatic login configured';
    const after = [t(status), `${t('Local user')}: ${user?.name ?? item.change.userSid}`,
        t('The account password will be verified and stored in Windows LSA. Automatic login count limits will be removed.')];
    if (item.change.clearLogonNotice)
    {
        after.push(t('The current login notice title and text will be removed from this PC. Managed policies may restore them.'));
    }
    return [before, after.join('\n')];
}

/** メッセージ表示だけではアカウント設定を禁止しません。
 * @returns 設定を禁止するその他のポリシーがあるかです。
 */
function hasBlockingPolicy(state)
{
    return state.restrictions.some(reason => reason !== noticePolicy);
}

/** 未設定のアカウントに区切り文字だけを表示しません。
 * @returns ドメインを含む設定済みアカウント、または未構成の説明です。
 */
function loginAccount(state, t)
{
    if (!state.userName)
    {
        return t('Not configured');
    }
    return state.domain ? `${state.domain}\\${state.userName}` : state.userName;
}

/** パスワードを通常の設定オブジェクトや画面保存へ渡さず取得します。 */
function editLogin(ctx, state)
{
    const value = state.snapshot.data, dialog = ctx.dialog('Configure automatic login');
    const users = select([['', ctx.t('Select a local user')], ...value.users.filter(user => user.selectable).map(user => [user.sid, `${user.name}${user.fullName ? ` · ${user.fullName}` : ''}`])], value.userSid ?? '');
    const password = input('', 'password');
    password.autocomplete = 'new-password';
    password.spellcheck = false;
    password.maxLength = 256;
    dialog.root.append(node('p', state.host.target.hostId), field(ctx.t('Local user'), users), field(ctx.t('Account password (not PIN)'), password,
        ctx.t('Enter the account password. An empty field will be verified as an empty password. The password is encrypted before it leaves this browser.')));
    dialog.root.append(node('p', ctx.t('Automatic login allows access without a password prompt. Use it on physically controlled PCs. Windows administrators can recover the LSA credential.'), 'hm-muted'));
    const clearNotice = checkbox(ctx.t('Disable the login notice on this PC'));
    if (value.restrictions.includes(noticePolicy))
    {
        dialog.root.append(node('p', ctx.t(noticePolicy), 'hm-muted'), clearNotice.root,
            node('p', ctx.t('The current login notice title and text will be removed from this PC. Managed policies may restore them.'), 'hm-muted'));
    }
    dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Preview changes', 'check', async () =>
    {
        const user = value.users.find(user => user.selectable && user.sid === users.value);
        if (!user)
        {
            throw new Error(ctx.t('Device.LoginSelectUser'));
        }
        const operationId = crypto.randomUUID();
        let credential;
        try
        {
            credential = await sealLoginCredentialAsync(password.value, value.encryptionKey, state.host.target, operationId, user.sid);
        }
        finally
        {
            password.value = '';
        }
        if (!dialog.element.isConnected || !dialog.element.open)
        {
            return;
        }
        await ctx.preview([{ host: state.host, query: { area: 'login' }, action: 'login', operationId,
            baseRevision: state.snapshot.revision, change: { enabled: true, userSid: user.sid, credential, clearLogonNotice: clearNotice.control.checked }, label: ctx.t('Login') }]);
    }, true));
}

/** パスワード保存先の確認だけを表示し、内容を読み出しません。 */
function renderState(root, ctx, state)
{
    const value = state.snapshot.data, section = node('section', '', 'hm-section');
    section.append(node('h3', state.host.target.hostId), badge(ctx.t(loginStatus(value))));
    section.append(node('p', `${ctx.t('Configured account')}: ${loginAccount(value, ctx.t)}`));
    if (value.remainingLogons != null)
    {
        section.append(node('p', `${ctx.t('Remaining automatic logins')}: ${value.remainingLogons}`));
    }
    const stored = value.protectedPasswordPresent == null ? 'Unconfirmed' : value.protectedPasswordPresent ? 'Present' : 'Absent';
    section.append(node('p', `${ctx.t('Protected automatic login password')}: ${ctx.t(stored)}`));
    if (value.plaintextPasswordPresent)
    {
        section.append(node('p', ctx.t('A legacy plaintext login password exists. Configuring or disabling automatic login here removes it.'), 'hm-muted'));
    }
    for (const reason of value.restrictions)
    {
        section.append(node('p', ctx.t(reason), 'hm-muted'));
    }
    const enable = action(ctx, 'Configure automatic login', 'user', () => editLogin(ctx, state));
    enable.disabled = !state.host.capabilities.elevated || hasBlockingPolicy(value) || !value.users.some(user => user.selectable) || !globalThis.crypto?.subtle;
    if (!globalThis.crypto?.subtle)
    {
        section.append(node('p', ctx.t('Device.LoginCryptoUnavailable'), 'hm-muted'));
    }
    const disable = action(ctx, 'Disable automatic login', 'close', () => ctx.preview([{ host: state.host, query: { area: 'login' }, action: 'login',
        baseRevision: state.snapshot.revision, change: { enabled: false }, label: ctx.t('Login') }]));
    disable.disabled = !state.host.capabilities.elevated;
    const buttons = node('div', '', 'hm-actions');
    buttons.append(enable, disable);
    section.append(buttons);
    root.append(section);
}

/** Hostごとのローカルユーザーと自動ログインを管理します。
 * @returns 読取りの寿命制御です。
 */
export function loginView(root, ctx)
{
    const hosts = structuredClone(ctx.hosts());
    if (!hosts.length)
    {
        return noSelection(root, ctx);
    }
    const title = heading(ctx.t('Login'), `${ctx.t('Selected hosts')}: ${hosts.length}`), status = node('div'), listing = node('div');
    const reader = createReader(ctx, hosts, () => ({ area: 'login' }), render);
    title.append(action(ctx, 'Refresh settings', 'refresh', () => reader.load()));
    root.append(title, node('p', ctx.t('Choose a local account for the next Windows login. Applying this setting does not sign out, restart the PC or register application startup entries.'), 'hm-muted'), status, listing);

    /** 資格情報を含まない現在値だけを表示します。 */
    function render()
    {
        readStatus(status, ctx, reader.states);
        listing.replaceChildren();
        for (const state of [...reader.states.values()].filter(state => state.snapshot))
        {
            renderState(listing, ctx, state);
        }
    }

    ctx.guard(reader.load);
    return { reload: () => reader.load(),

        dispose: () => reader.dispose() };
}
