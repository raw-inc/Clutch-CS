import { node, select, field, heading, action, badge, table, empty } from './dom.js';
import { createReader, readStatus, noSelection } from './loader.js';

/** ドライバーが公開したモードを、表示設定で選ぶ説明に変換します。
 * @returns 解像度・Hzと必要なモード区別です。
 */
export function displayModeLabel(mode, translate = key => key)
{
    return `${mode.width} × ${mode.height} · ${mode.refreshRate} Hz · ${mode.bitsPerPixel} bit${mode.flags & 2 ? ` · ${translate('Interlaced')}` : ''}`;
}

/** 対応モードを確認して、保存対象の寸法・Hzを組として固定します。
 * @returns 変更確認へ送る表示モードです。
 */
export function displayChange(display, index)
{
    const mode = display.modes[index];
    if (!Number.isInteger(index) || !mode)
    {
        throw new Error('Device.UnsupportedDisplayMode');
    }
    return { mode: structuredClone(mode) };
}

/** 接続ディスプレイをHostごとに表示し、OSの対応候補から編集します。
 * @returns 読取り更新と画面寿命の制御です。
 */
export function displayView(root, ctx)
{
    const hosts = structuredClone(ctx.hosts());
    if (!hosts.length)
    {
        return noSelection(root, ctx);
    }
    const title = heading(ctx.t('Display settings'), `${ctx.t('Selected hosts')}: ${hosts.length}`), status = node('div'), listing = node('div');
    const reader = createReader(ctx, hosts, () => ({ area: 'display' }), render);
    title.append(action(ctx, 'Refresh settings', 'refresh', () => reader.load()));
    root.append(title, node('p', ctx.t('Choose a mode reported by the connected display. Desktop position, rotation and the primary display are retained.'), 'hm-muted'), status, listing);

    /** 一つの表示接続に対応したモード選択を開きます。 */
    function edit(host, display)
    {
        const dialog = ctx.dialog('Change display mode');
        const current = display.modes.findIndex(mode => JSON.stringify(mode) === JSON.stringify(display.current));
        const modes = select(display.modes.map((mode, index) => [String(index), displayModeLabel(mode, ctx.t)]), String(current));
        dialog.root.append(node('p', `${host.target.hostId} · ${display.name}`), field(ctx.t('Resolution and refresh rate'), modes));
        dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Preview changes', 'check', () => ctx.preview([
            { host, query: { area: 'display', resourceId: display.id }, action: 'display', baseRevision: display.revision,
                change: displayChange(display, modes.value.length ? Number(modes.value) : Number.NaN), label: display.name }
        ]), true));
    }

    /** 読取りが確認できたHostだけに編集操作を表示します。 */
    function render()
    {
        readStatus(status, ctx, reader.states);
        listing.replaceChildren();
        for (const state of reader.states.values())
        {
            if (!state.snapshot)
            {
                continue;
            }
            const section = node('section', '', 'hm-section');
            section.append(node('h3', state.host.target.hostId));
            const list = table([ctx.t('Connected display'), ctx.t('Current mode'), ctx.t('Actions')]);
            for (const display of state.snapshot.data.displays)
            {
                const name = node('div', display.name);
                if (display.primary)
                {
                    name.append(badge(ctx.t('Primary display')));
                }
                const change = action(ctx, 'Change display mode', 'settings', () => edit(state.host, display));
                change.disabled = !state.host.capabilities.elevated || !display.modes.length;
                list.add([name, displayModeLabel(display.current, ctx.t), change]);
            }
            section.append(state.snapshot.data.displays.length ? list.root : empty(ctx.t('No active display was found.'), '', 'monitor'));
            listing.append(section);
        }
    }

    ctx.guard(reader.load);
    return { reload: () => reader.load(),

        dispose: () => reader.dispose() };
}
