import { node, heading, action, badge, table } from './dom.js';
import { createReader, readStatus, noSelection } from './loader.js';

/** 外部管理や期限による上書きを、手動更新の確認済み表示から分けます。
 * @returns 更新ポリシーの確認結果です。
 */
export function windowsUpdateStatus(value)
{
    if (!value.supported)
    {
        return 'Device.UpdateEditionUnsupported';
    }
    if (value.restrictions.length)
    {
        return 'Update policy is managed or unconfirmed';
    }
    if (value.noAutoUpdate === 1)
    {
        return Object.values(value.deadlines).some(setting => setting !== null && setting !== 0) ? 'Update deadlines can override manual mode' : 'Manual updates only';
    }
    return 'Automatic updates are allowed';
}

/** 手動更新に伴う期限無効化も確認画面へ含めます。
 * @returns 更新の変更前後です。
 */
export function windowsUpdatePreview(item, t = key => key)
{
    const value = item.before.data;
    const before = [t(windowsUpdateStatus(value)), ...Object.entries(value.deadlines).map(([key, setting]) => `${t(key)}: ${t(setting == null ? 'Not configured' : setting ? 'Enabled' : 'Disabled')}`)];
    const after = item.change.manualOnly ? [t('Manual updates only'), t('Local update and restart deadlines will be disabled.')] : [t('Automatic updates are allowed'), t('The manual-only restriction will be removed. Disabled deadline policies will stay disabled.')];
    return [before.join('\n'), after.join('\n')];
}

/** 対象と変更前の版を固定して更新ポリシーの確認へ渡します。
 * @returns 変更確認の完了です。
 */
function previewUpdate(ctx, states, manualOnly)
{
    return ctx.preview(states.map(state => ({ host: state.host, query: { area: 'windowsUpdate' }, action: 'windowsUpdate',
        baseRevision: state.snapshot.revision, change: { manualOnly }, label: ctx.t('Windows Update') })));
}

/** プロセス権限とローカル管理の両方を確認します。
 * @returns 変更確認へ進める状態です。
 */
function editable(state)
{
    return !!state.snapshot && state.host.capabilities.elevated && state.snapshot.data.supported && !state.snapshot.data.restrictions.length;
}

/** 現在のポリシーと進行済み更新を表示します。 */
function renderState(root, ctx, state)
{
    const value = state.snapshot.data, section = node('section', '', 'hm-section');
    section.append(node('h3', state.host.target.hostId), node('p', `Windows · ${value.edition}`), badge(ctx.t(windowsUpdateStatus(value))));
    const list = table([ctx.t('Setting'), ctx.t('Status')]);
    list.add([ctx.t('Update installation in progress'), ctx.t(value.installInProgress == null ? 'Unconfirmed' : value.installInProgress ? 'Yes' : 'No')]);
    list.add([ctx.t('Windows update restart pending'), ctx.t(value.rebootRequired ? 'Yes' : 'No')]);
    section.append(list.root);
    for (const reason of value.restrictions)
    {
        section.append(node('p', ctx.t(reason), 'hm-muted'));
    }
    const buttons = node('div', '', 'hm-actions');
    for (const [label, manualOnly] of [['Require manual updates', true], ['Release automatic update restriction', false]])
    {
        const button = action(ctx, label, 'settings', () => previewUpdate(ctx, [state], manualOnly));
        button.disabled = !editable(state);
        buttons.append(button);
    }
    section.append(buttons);
    root.append(section);
}

/** 自動更新の制限を、Hostごとの確認済みポリシーに適用します。
 * @returns 読取りの寿命制御です。
 */
export function windowsUpdateView(root, ctx)
{
    const hosts = structuredClone(ctx.hosts());
    if (!hosts.length)
    {
        return noSelection(root, ctx);
    }
    const title = heading(ctx.t('Windows Update'), `${ctx.t('Selected hosts')}: ${hosts.length}`), status = node('div'), listing = node('div');
    const reader = createReader(ctx, hosts, () => ({ area: 'windowsUpdate' }), render);
    const manual = action(ctx, 'Require manual updates for selected hosts', 'settings', () => previewUpdate(ctx, [...reader.states.values()], true));
    manual.disabled = true;
    title.append(action(ctx, 'Refresh settings', 'refresh', () => reader.load()), manual);
    root.append(title, node('p', ctx.t('Manual mode disables automatic Windows updates without an expiry. Install updates explicitly from Windows Settings. Existing installations or pending restarts are not canceled.'), 'hm-muted'),
        node('p', ctx.t('This controls Windows Update. Microsoft Store, Defender intelligence and other applications have separate update settings.'), 'hm-muted'), status, listing);

    /** 未確認のホストをまとめて適用しません。 */
    function render()
    {
        readStatus(status, ctx, reader.states);
        const states = [...reader.states.values()];
        manual.disabled = states.length !== hosts.length || !states.every(editable);
        listing.replaceChildren();
        for (const state of states.filter(state => state.snapshot))
        {
            renderState(listing, ctx, state);
        }
    }

    ctx.guard(reader.load);
    return { reload: () => reader.load(),

        dispose: () => reader.dispose() };
}
