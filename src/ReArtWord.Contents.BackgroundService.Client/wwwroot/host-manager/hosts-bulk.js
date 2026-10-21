import { node, heading, action, table, details, field, input, select, textarea } from './dom.js';
import { createReader, readStatus } from './loader.js';
import { triState } from './tri-state.js';
import { aggregateHosts, hostsEntryKey } from './hosts-bulk-model.js';
import { validateHostsEntry, validateHostsText } from './hosts-model.js';

/** hosts設定を複数ホストへ変更する画面を構成します。
 * @returns dispose・reload・hasChangesを含む結果です。
 */
export function hostsBulkView(root, ctx, hosts)
{
    const choices = new Map(), added = new Map();
    let rows = [], loaded = false;
    const title = heading(ctx.t('Hosts file'), ctx.t('Change selected entries while preserving each host’s own file.')), status = node('div'), listing = node('div');
    const source = select(hosts.map(host => [host.target.hostId, host.target.hostId]), hosts[0].target.hostId), tools = node('div', '', 'hm-toolbar');
    const add = action(ctx, 'Add hosts entry', 'plus', addEntry), edit = action(ctx, 'Edit text on host', 'settings', editText);
    tools.append(add, field(ctx.t('Source host'), source), edit);
    const preview = action(ctx, 'Preview changes', 'check', () => ctx.preview(hosts.map(host => ({ host, query: { area: 'hosts' }, action: 'hosts', patch: { kind: 'hosts', entries: pending() }, label: ctx.t('Selected hosts entries') })), () =>
    {
        choices.clear();
        added.clear();
    }), true);
    const bar = node('div', '', 'hm-selection-bar'), count = node('span');
    bar.append(count, ctx.button('Reset changes', () =>
    {
        choices.clear();
        added.clear();
        render();
    }), preview);
    title.append(action(ctx, 'Refresh settings', 'refresh', () => ctx.discard(reload, hasChanges())));
    root.append(title, node('p', ctx.t('disable / other / enable: other preserves each host’s current setting. Missing entries also appear as other.'), 'hm-muted'), tools, status, listing, bar);

    /** 保存前の変更予定を呼出し元へ返します。
     * @returns 呼出し元が使用する値です。
     */
    function pending()
    {
        return rows.filter(row => choices.has(row.key) && choices.get(row.key) !== 'other' && choices.get(row.key) !== row.state).map(row => ({ entry: row.entry, state: choices.get(row.key) }));
    }

    /** 元の値から変更された項目があるか判定します。
     * @returns 呼出し元が使用する判定結果です。
     */
    function hasChanges()
    {
        return (0) < (pending().length);
    }

    /** 現在の設定と状態から表示を更新します。 */
    function update()
    {
        count.textContent = `${ctx.t('Changes')}: ${pending().length}`;
        preview.disabled = !loaded || !hasChanges();
    }

    /** 現在の値を表示内容へ反映します。 */
    function render()
    {
        listing.replaceChildren();
        add.disabled = edit.disabled = !loaded;
        if (!loaded)
        {
            rows = [];
            update();
            return;
        }
        rows = aggregateHosts(hosts.map(host => reader.states.get(host.target.hostId).snapshot.data.content));
        for (const [key, entry] of added)
        {
            if (!rows.some(row => row.key === key))
            {
                rows.push({ key, entry, states: hosts.map(() => 'missing'), state: 'other' });
            }
        }
        const grid = table(['IP address', 'Host names and aliases', 'Across selected hosts', 'Host states'].map(ctx.t));
        for (const row of rows)
        {
            const expectedMissing = 'missing';

            const names = node('div', row.entry.names);
            if (row.entry.comment)
            {
                names.append(node('small', row.entry.comment));
            }
            const states = table(['Host', 'Status'].map(ctx.t));
            hosts.forEach((host, index) => states.add([host.target.hostId, ctx.t(row.states[index] === expectedMissing ? 'Not defined' : row.states[index])]));
            const toggle = triState(ctx, `${row.entry.address} · ${row.entry.names}`, choices.get(row.key) ?? row.state, value =>
            {
                choices.set(row.key, value);
                update();
            });
            grid.add([row.entry.address, names, toggle, details(ctx.t('Host states'), states.root)]);
        }
        listing.append(grid.root);
        update();
    }

    /** 新しい編集項目を追加します。 */
    function addEntry()
    {
        const dialog = ctx.dialog('Add hosts entry'), address = input(''), names = input(''), comment = input('');
        dialog.root.append(field(ctx.t('IP address'), address), field(ctx.t('Host names and aliases'), names), field(ctx.t('Comment'), comment));
        dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Use entry', 'check', () =>
        {
            const entry = validateHostsEntry({ address: address.value.trim(), names: names.value, comment: comment.value, enabled: true }), key = hostsEntryKey(entry);
            added.set(key, entry);
            choices.set(key, 'enable');
            dialog.close();
            render();
        }, true));
    }

    /** 現在の設定を文字列として編集します。 */
    function editText()
    {
        const host = hosts.find(item => item.target.hostId === source.value), snapshot = reader.states.get(source.value).snapshot;
        const dialog = ctx.dialog('Edit text on host'), text = textarea(snapshot.data.content, 18);
        text.className = 'hm-code-editor';
        dialog.root.append(node('p', host.target.hostId, 'hm-muted'), field(ctx.t('Replace hosts content'), text));
        dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Preview changes', 'check', () => ctx.preview([{ host, query: { area: 'hosts' }, action: 'hosts', baseRevision: snapshot.revision, change: { content: validateHostsText(text.value) }, label: ctx.t('Replace hosts content') }]), true));
    }

    /** 最新状態を取得し、現在の表示へ反映します。
     * @returns 呼出し元が使用する値です。
     */
    function reload()
    {
        choices.clear();
        added.clear();
        return reader.load();
    }

    const reader = createReader(ctx, hosts, () => ({ area: 'hosts' }), states =>
    {
        readStatus(status, ctx, states);
        loaded = states.size === hosts.length && [...states.values()].every(state => state.snapshot);
        render();
    });
    ctx.guard(reader.load);
    return { dispose: reader.dispose, reload, hasChanges };
}
