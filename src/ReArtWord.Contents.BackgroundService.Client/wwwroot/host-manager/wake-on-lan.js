import { node, heading, action, checkbox, table } from './dom.js';
import { createReader, readStatus, noSelection } from './loader.js';

/** 明示選択と物理NICの既定選択から、送信対象を決めます。
 * @returns 現在の一覧に存在する選択済みNICです。
 */
export function selectedWakeInterfaces(inventory, selection)
{
    const ids = new Set(selection.interfaceIds.map(id => id.toLowerCase()));
    return inventory.interfaces.filter(item => selection.usePhysicalDefaults ? item.physical : ids.has(item.id.toLowerCase()));
}

/** 保存する対象を、内部IDだけではなくNIC名とMACで比較します。
 * @returns 設定変更の前後です。
 */
export function wakePreview(item, t = key => key)
{
    const state = item.before.data;
    return [state.selection, item.change].map(selection =>
    {
        const names = selectedWakeInterfaces(state.inventory, selection).map(adapter => `${adapter.name} (${adapter.macAddress})`).join('\n');
        return `${t(selection.usePhysicalDefaults ? 'wake.defaults' : 'wake.explicit')}\n${names || t('wake.none')}`;
    });
}

/** 読取り時の版へ変更案を固定し、NIC設定自体は書き換えません。 */
function renderHost(root, ctx, state, draft)
{
    const section = node('section', '', 'hm-section'), selected = new Set(selectedWakeInterfaces(state.snapshot.data.inventory, draft.selection).map(item => item.id));
    const automatic = checkbox(ctx.t('wake.defaults'), draft.selection.usePhysicalDefaults);
    const list = table([ctx.t('wake.selected'), ctx.t('Name'), 'MAC', ctx.t('wake.kind')]);
    const rows = [];
    const editable = state.host.capabilities.elevated === true;
    automatic.control.disabled = !editable;
    section.append(node('h3', state.host.target.hostId), automatic.root, list.root);
    for (const adapter of state.snapshot.data.inventory.interfaces)
    {
        const check = checkbox('', selected.has(adapter.id));
        check.control.setAttribute('aria-label', adapter.name);
        check.control.disabled = !editable;
        check.control.onchange = () =>
        {
            automatic.control.checked = false;
            draft.selection.usePhysicalDefaults = false;
            if (check.control.checked)
            {
                selected.add(adapter.id);
            }
            else
            {
                selected.delete(adapter.id);
            }
            draft.selection.interfaceIds = [...selected];
        };
        rows.push({ adapter, control: check.control });
        list.add([check.root, adapter.name, adapter.macAddress, ctx.t(adapter.physical ? 'wake.physical' : 'wake.virtual')]);
    }
    automatic.control.onchange = () =>
    {
        draft.selection.usePhysicalDefaults = automatic.control.checked;
        if (automatic.control.checked)
        {
            selected.clear();
            for (const row of rows)
            {
                row.control.checked = row.adapter.physical === true;
                if (row.control.checked)
                {
                    selected.add(row.adapter.id);
                }
            }
            draft.selection.interfaceIds = [];
        }
        else
        {
            draft.selection.interfaceIds = [...selected];
        }
    };
    const save = action(ctx, 'wake.review', 'settings', () => ctx.preview([{ host: state.host, query: { area: 'wakeOnLan' }, action: 'wakeOnLan',
        baseRevision: draft.revision, change: structuredClone(draft.selection), label: ctx.t('wake.title') }]));
    save.disabled = !editable;
    section.append(save);
    root.append(section);
}

/** PCが停止する前に対象NICを保存する、ホスト単位の設定画面を構成します。
 * @returns 読取りの寿命制御です。
 */
export function wakeOnLanView(root, ctx)
{
    const hosts = structuredClone(ctx.hosts());
    if (!hosts.length)
    {
        return noSelection(root, ctx);
    }
    const drafts = new Map(), title = heading(ctx.t('wake.title'), ctx.t('wake.description')), status = node('div'), listing = node('div');
    const reader = createReader(ctx, hosts, () => ({ area: 'wakeOnLan', resourceId: 'refresh' }), render);

    /** 明示した再取得または適用後にだけ、未保存の選択を読み直します。
     * @returns 現在のホスト設定の再取得です。
     */
    const reload = () =>
    {
        drafts.clear();
        return reader.load();
    };

    title.append(action(ctx, 'Refresh settings', 'refresh', reload));
    root.append(title, node('p', ctx.t('wake.requirements'), 'hm-muted'), node('p', ctx.t('wake.inventoryCache'), 'hm-muted'), status, listing);

    /** 別ホストの読み取り完了で、先に編集した選択を消しません。 */
    function render()
    {
        readStatus(status, ctx, reader.states);
        listing.replaceChildren();
        for (const state of [...reader.states.values()].filter(value => value.snapshot))
        {
            const key = state.host.target.hostId;
            if (!drafts.has(key))
            {
                drafts.set(key, { revision: state.snapshot.revision, selection: structuredClone(state.snapshot.data.selection) });
            }
            renderHost(listing, ctx, state, drafts.get(key));
        }
    }

    ctx.guard(reader.load);
    return { reload,

        dispose: () => reader.dispose() };
}
