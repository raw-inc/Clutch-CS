import test from 'node:test';
import assert from 'node:assert/strict';
import { loginStatus, loginView } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/login.js';
import { sealLoginCredentialAsync } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/login-credential.js';
import { previewValues } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/operations.js';
import { createDocument, descendants } from './host-manager-dom.mjs';

/** パスワード本文を持たない画面用状態を用意します。
 * @returns 自動ログインが保存済みの状態です。
 */
function state()
{
    return { enabled: true, userSid: 'S-1-5-21-1001', userName: 'operator', domain: 'PC', protectedPasswordPresent: true, restrictions: [],
        users: [{ sid: 'S-1-5-21-1001', name: 'operator', selectable: true }] };
}

test('login distinguishes configured, disabled and unconfirmed credentials', () =>
{
    const current = state();
    assert.equal(loginStatus(current), 'Automatic login configured');
    assert.equal(loginStatus({ ...current, enabled: false }), 'Automatic login disabled');
    assert.equal(loginStatus({ ...current, protectedPasswordPresent: null }), 'Automatic login configuration is unconfirmed');
    assert.equal(loginStatus({ ...current, restrictions: ['policy'] }), 'Automatic login configuration is unconfirmed');
    assert.equal(loginStatus({ ...current, users: [{ ...current.users[0], selectable: false }] }), 'Automatic login configuration is unconfirmed');
    assert.equal(loginStatus({ ...current, remainingLogons: 0 }), 'Automatic login configuration is unconfirmed');
    assert.equal(loginStatus({ ...current, plaintextPasswordPresent: true }), 'Automatic login configuration is unconfirmed');
});

test('login distinguishes a saved account from a notice that still blocks unattended login', () =>
{
    const current = { ...state(), restrictions: ['Device.LoginNoticePolicy'] };
    assert.equal(loginStatus(current), 'Automatic login account saved; login notice prevents automatic login');
    assert.equal(loginStatus({ ...current, enabled: false }), 'Automatic login disabled');
    assert.equal(loginStatus({ ...current, protectedPasswordPresent: null }), 'Automatic login configuration is unconfirmed');
    assert.equal(loginStatus({ ...current, restrictions: [...current.restrictions, 'Device.LoginSmartCardPolicy'] }), 'Automatic login configuration is unconfirmed');
});

test('login preview discloses whether the notice is retained or explicitly removed', () =>
{
    const item = { action: 'login', before: { data: { ...state(), restrictions: ['Device.LoginNoticePolicy'] } }, change: { enabled: true, userSid: 'S-1-5-21-1001' } };
    const retained = previewValues(item)[1];
    assert.match(retained, /login notice prevents automatic login/u);
    assert.doesNotMatch(retained, /title and text will be removed/u);
    item.change.clearLogonNotice = true;
    const cleared = previewValues(item)[1];
    assert.match(cleared, /Automatic login configured/u);
    assert.match(cleared, /title and text will be removed/u);
    assert.match(cleared, /Managed policies may restore them/u);
});

test('login preview contains the account and storage effect without exposing the credential object', () =>
{
    const item = { action: 'login', before: { data: state() }, change: { enabled: true, userSid: 'S-1-5-21-1001', credential: { ciphertext: 'opaque-test-value' } } };
    const [before, after] = previewValues(item);
    assert.match(before, /PC\\operator/u);
    assert.match(after, /Local user: operator/u);
    assert.match(after, /Windows LSA/u);
    assert.doesNotMatch(after, /opaque-test-value|ciphertext/u);
    item.change = { enabled: false };
    assert.match(previewValues(item)[1], /saved automatic login password will be removed/u);
});

test('login refuses to send an unencrypted password when browser encryption is unavailable', async () =>
{
    await assert.rejects(() => sealLoginCredentialAsync('synthetic-only', {}, {}, '', '', {}), /Device.LoginCryptoUnavailable/u);
});

test('login view permits account configuration with a notice and keeps notice removal opt-in', async t =>
{
    const previous = Object.getOwnPropertyDescriptor(globalThis, 'document');
    globalThis.document = createDocument();
    t.after(() =>
    {
        if (previous)
        {
            Object.defineProperty(globalThis, 'document', previous);
        }
        else
        {
            delete globalThis.document;
        }
    });
    const cases = [
        { restrictions: ['Device.LoginNoticePolicy'], disabled: false },
        { restrictions: ['Device.LoginNoticePolicy', 'Device.LoginSmartCardPolicy'], disabled: true },
        { restrictions: ['Device.LoginPasswordPolicy'], disabled: true },
        { restrictions: ['future-policy'], disabled: true },
        { restrictions: ['Device.LoginNoticePolicy'], elevated: false, disabled: true },
        { restrictions: [], users: [], disabled: true },
    ];
    const root = document.createElement('div'), dialogRoot = document.createElement('div');
    let pending;
    const ctx = {
        t: key => key,

        signal: new AbortController().signal,

        hosts: () => cases.map((item, index) => ({ target: { hostId: String(index) }, capabilities: { elevated: item.elevated !== false } })),

        read: async host => ({ revision: 'revision', data: { ...state(), ...cases[Number(host.target.hostId)] } }),

        guard: run =>
        {
            pending = run();
        },

        dialog: () => ({ root: dialogRoot, footer: document.createElement('div') }),

        /** 画面で生成したボタンを実際のイベントから操作します。
         * @returns 表示ラベルを持つボタンです。
         */
        button(label, run)
        {
            const button = document.createElement('button');
            button.textContent = label;
            button.onclick = run;
            return button;
        },
    };
    const view = loginView(root, ctx);
    t.after(() => view.dispose());
    await pending;
    const buttons = descendants(root).filter(item => item.tag === 'button' && item.textContent === 'Configure automatic login');
    assert.equal(buttons.length, cases.length);
    assert.deepEqual(buttons.map(button => button.disabled), cases.map(item => item.disabled));
    buttons[0].onclick();
    const removal = descendants(dialogRoot).find(item => item.type === 'checkbox');
    assert(removal);
    assert.equal(removal.checked, false);
    assert.match(dialogRoot.textContent, /Disable the login notice on this PC/u);
});
