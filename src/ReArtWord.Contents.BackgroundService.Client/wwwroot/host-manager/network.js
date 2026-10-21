import { node, input, field, select, checkbox, table, action, badge, heading, segmented, empty, details } from './dom.js';
import { createReader, readStatus, noSelection } from './loader.js';
import { subnet, canGroup } from './network-model.js';
import { networkForm } from './network-form.js';
import { virtualNics } from './virtual-nics.js';
import { virtualSwitchesView } from './virtual-switches.js';
import { vlanLabel, vlanTone } from './vlan.js';
import { windowsDnsDefaults } from './dns.js';

/** ネットワーク一覧と変更確認の画面を構成します。
 * @returns hasChanges・dispose・reloadを含む結果です。
 */
export function networkView(root, ctx)
{
    const hosts = structuredClone(ctx.hosts());
    if (!hosts.length)
    {
        return noSelection(root, ctx);
    }
    let family = 4, mode = 'interfaces', records = [], selected = new Set(), switchView = null;
    const pending = new Map();
    const staged = node('div', '', 'hm-protocol-drafts');

    /** 元の値から変更された項目があるか判定します。
     * @returns 呼出し元が使用する判定結果です。
     */
    const hasChanges = () => pending.size !== 0;

    /** ネットワークの変更予定を解除します。 */
    function resetPending()
    {
        pending.clear();
        renderPending();
        renderList();
    }

    const title = heading(ctx.t('Network settings'), `${ctx.t('Selected hosts')}: ${hosts.length}`), status = node('div');
    const listing = node('div'), virtual = node('div'), switches = node('div'), filters = node('div', '', 'hm-toolbar'), bulk = node('div', '', 'hm-selection-bar');
    const search = input('', 'search');
    search.className = 'hm-search';
    search.placeholder = ctx.t('Search interfaces');
    search.setAttribute('aria-label', ctx.t('Search interfaces'));
    const group = select([['', ctx.t('All networks')]]), count = badge('0');
    const familyPicker = segmented([['4', 'IPv4'], ['6', 'IPv6']], '4', value =>
    {
        family = Number(value);
        selected.clear();
        renderGroups();
        renderList();
    }, ctx.t('Address family'));
    const views = segmented([['interfaces', ctx.t('Network interfaces')], ['virtual', ctx.t('Virtual NICs')], ['switches', ctx.t('Virtual switches')]], mode, value =>
    {
        mode = value;
        updateMode();
    }, ctx.t('Network'));
    const refresh = action(ctx, 'Refresh settings', 'refresh', () => ctx.discard(() =>
    {
        const expectedSwitches = 'switches';

        resetPending();
        return mode === expectedSwitches ? switchView.reload() : reader.load();
    }, hasChanges()));
    title.append(refresh);
    const groupField = field(ctx.t('Network'), group);
    groupField.classList.add('hm-filter-field');
    filters.append(familyPicker, search, groupField, ctx.button('Select interfaces', () =>
    {
        visibleRecords().filter(record => configuration(record)).forEach(record => selected.add(record.key));
        renderList();
    }), count);
    root.append(title, views, status, filters, listing, virtual, switches, staged);

    /** 保存前の変更予定を確認欄へ表示します。 */
    function renderPending()
    {
        staged.replaceChildren();
        const entries = [...pending.values()], changes = entries.reduce((sum, item) => sum + Object.keys(item.change).length, 0);
        const tools = node('div', '', 'hm-selection-bar');
        tools.append(node('strong', `${ctx.t('Pending protocol changes')}: ${changes} · ${ctx.t('Changed interfaces')}: ${entries.length}`));
        const actions = node('div', '', 'hm-actions'), clear = ctx.button('Clear all pending changes', resetPending), apply = action(ctx, 'Apply', 'check', async () =>
        {
            const planned = entries.map(({ record, change, revision }) => ({ host: record.host, query: { area: 'network', resourceId: record.nic.id }, action: 'networkProtocols', change: { ...change }, baseRevision: revision, label: record.nic.name }));
            listing.inert = filters.inert = views.inert = staged.inert = true;
            try
            {
                return await ctx.preview(planned, resetPending);
            }
            finally
            {
                listing.inert = filters.inert = views.inert = staged.inert = false;
            }
        }, true);
        clear.disabled = apply.disabled = !changes;
        actions.append(clear, apply);
        tools.append(actions);
        staged.append(bulk, tools);
        if (changes)
        {
            const rows = table(['Host', 'Network interface', 'Before', 'After'].map(ctx.t));
            for (const { record, change } of entries)
            {
                for (const version of [4, 6])
                {
                    const value = change[`ipv${version}Enabled`];
                    if (value === undefined)
                    {
                        continue;
                    }
                    rows.add([record.host.target.hostId, `${record.nic.name} · IPv${version}`, ctx.t(record.nic[`ipv${version}`].enabled ? 'Enabled' : 'Disabled'), ctx.t(value ? 'Enabled' : 'Disabled')]);
                }
            }
            staged.append(details(ctx.t('Review pending changes'), rows.root));
        }
    }

    /** 現在の値から適用する設定を組み立てます。
     * @returns 呼出し元が使用する値です。
     */
    function configuration(record)
    {
        return record.nic[family === 4 ? 'ipv4' : 'ipv6'];
    }

    /** 対象の項目を共通条件でまとめます。
     * @returns 呼出し元が使用する値です。
     */
    function groups(record)
    {
        return (configuration(record)?.addresses ?? []).filter(item => canGroup(item.address, family))
            .map(item => subnet(item.address, item.prefixLength, family) + ' · ' + vlanLabel(record.nic.vlanMode, record.nic.vlanId, ctx.t));
    }

    /** 現在の検索条件に該当する項目を取得します。
     * @returns 呼出し元が使用する値です。
     */
    function visibleRecords()
    {
        const query = search.value.trim().toLocaleLowerCase();
        return records.filter(record => (!group.value || groups(record).includes(group.value))
            && `${record.host.target.hostId} ${record.nic.name} ${record.nic.description} ${(configuration(record)?.addresses ?? []).map(item => item.address).join(' ')}`.toLocaleLowerCase().includes(query));
    }

    /** まとめられた項目をグループごとに表示します。 */
    function renderGroups()
    {
        const options = [...new Set(records.flatMap(groups))].sort(), previous = group.value;
        group.replaceChildren(...select([['', ctx.t('All networks')], ...options.map(value => [value, value])]).children);
        group.value = options.includes(previous) ? previous : '';
    }

    /** 経路とメトリックの表示要素を構成します。
     * @returns 一覧に追加する経路とメトリックの要素です。
     */
    function routingDetails(current)
    {
        const routing = node('div', '', 'hm-stacked');
        routing.append(node('span', windowsDnsDefaults(current, family) ? ctx.t('No configured DNS (Windows defaults)') : current?.dns.join('\n') || ctx.t('No DNS servers'), 'hm-address'), node('small', `${ctx.t('Gateway')}: ${current?.gateways.join(', ') || '—'}`));
        const metric = node('div', String(current?.metric ?? '—'));
        metric.append(node('small', current?.automaticMetric == null ? '' : ctx.t(current.automaticMetric ? 'Automatic' : 'Manual')));
        return { routing, metric };
    }

    /** 一台のインターフェースの状態と操作を一覧行へ反映します。 */
    function renderInterfaceRow(rows, record)
    {
        const expectedUp = 'Up';

        const { host, nic, key } = record, current = configuration(record);
        const pick = checkbox('', selected.has(key));
        pick.control.disabled = !current;
        pick.control.setAttribute('aria-label', `${nic.name} · ${host.target.hostId}`);
        const name = node('div', '', 'hm-adapter-name'), text = node('div');
        text.append(node('strong', nic.name), node('small', host.target.hostId));
        name.title = nic.description;
        name.append(pick.root, text);
        const addresses = node('div', '', 'hm-stacked');
        addresses.append(node('span', current?.addresses.map(item => `${item.address}/${item.prefixLength}`).join('\n') || '—', 'hm-address'));
        const tags = node('div', '', 'hm-actions');
        tags.append(badge(current ? ctx.t(current.enabled ? 'Enabled' : 'Disabled') : ctx.t('Unavailable'), current?.enabled && nic.status === expectedUp ? 'good' : ''), badge(current?.dhcp == null ? nic.status : ctx.t(current.dhcp ? 'DHCP' : 'Manual')));
        addresses.append(tags);
        const protocols = node('div', '', 'hm-protocols');
        for (const version of [4, 6])
        {
            const configuration = nic[version === 4 ? 'ipv4' : 'ipv6'];
            const property = `ipv${version}Enabled`, caption = node('small', '', 'hm-protocol-change');

            /** 現在の設定と状態から表示を更新します。 */
            const update = () =>
            {
                const value = pending.get(key)?.change[property], changed = value !== undefined;
                toggle.setAttribute('aria-checked', String(value ?? configuration?.enabled === true));
                toggle.dataset.pending = String(changed);
                caption.textContent = changed ? `${ctx.t(configuration.enabled ? 'Enabled' : 'Disabled')} → ${ctx.t(value ? 'Enabled' : 'Disabled')}` : '';
                caption.hidden = !changed;
                toggle.title = changed ? `${ctx.t('Pending protocol changes')}: ${caption.textContent}` : `IPv${version}`;
            };

            const toggle = ctx.button(`IPv${version}`, () =>
            {
                const entry = pending.get(key) ?? { record, change: {}, revision: nic.revision };
                const next = !(entry.change[property] ?? configuration.enabled);
                if (next === configuration.enabled)
                {
                    delete entry.change[property];
                }
                else
                {
                    entry.change[property] = next;
                }
                if (Object.keys(entry.change).length)
                {
                    pending.set(key, entry);
                }
                else
                {
                    pending.delete(key);
                }
                update();
                renderPending();
            }, 'hm-protocol');
            toggle.setAttribute('role', 'switch');
            toggle.setAttribute('aria-label', `IPv${version} · ${nic.name} · ${host.target.hostId}`);
            toggle.disabled = !configuration;
            update();
            const protocol = node('div');
            protocol.append(toggle, caption);
            protocols.append(protocol);
        }
        const { routing, metric } = routingDetails(current);
        const edit = action(ctx, 'Edit', 'settings', () => editInterface(record));
        edit.disabled = !current;
        const row = rows.add([name, protocols, addresses, routing, metric, badge(vlanLabel(nic.vlanMode, nic.vlanId, ctx.t), vlanTone(nic.vlanMode)), edit]);
        row.dataset.selected = String(selected.has(key));
        pick.control.onchange = () =>
        {
            if (pick.control.checked)
            {
                selected.add(key);
            }
            else
            {
                selected.delete(key);
            }
            row.dataset.selected = String(pick.control.checked);
            renderBulk();
        };
    }

    /** 現在の一覧を操作可能な行へ変換します。 */
    function renderList()
    {
        listing.replaceChildren();
        const visible = visibleRecords();
        count.textContent = `${visible.length} ${ctx.t('interfaces')}`;
        if (!records.length)
        {
            updateMode();
            return;
        }
        if (!visible.length)
        {
            listing.append(empty(ctx.t('No matching interfaces'), ctx.t('Try another search or network.'), 'network'));
            updateMode();
            return;
        }
        const rows = table(['Network interface', 'IPv4 / IPv6', 'Addressing', 'DNS / gateway', 'Metric', 'VLAN', 'Edit'].map(ctx.t));
        for (const record of visible)
        {
            renderInterfaceRow(rows, record);
        }
        listing.append(rows.root);
        renderBulk();
        renderPending();
        updateMode();
    }

    /** 指定したインターフェースの編集内容を構成します。 */
    function editInterface(record)
    {
        const dialog = ctx.dialog('Edit interface'), selectedFamily = family;
        dialog.root.append(node('p', `${record.host.target.hostId} · ${record.nic.name} · IPv${family}`, 'hm-muted'));
        const build = networkForm(dialog.root, ctx, selectedFamily, configuration(record));
        dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Preview changes', 'check', () => ctx.preview([{ host: record.host, query: { area: 'network', resourceId: record.nic.id }, action: 'network', change: build(), label: record.nic.name }]), true));
    }

    /** 複数対象に共通する変更の入力欄を表示します。 */
    function renderBulk()
    {
        bulk.replaceChildren();
        const targets = records.filter(record => selected.has(record.key));
        bulk.append(node('span', `${ctx.t('Selected interfaces')}: ${targets.length} · ${ctx.t('Hosts')}: ${new Set(targets.map(record => record.host.target.hostId)).size}`));
        const tools = node('div', '', 'hm-actions');
        tools.append(ctx.button('Clear selection', () =>
        {
            selected.clear();
            renderList();
        }), action(ctx, 'Set DNS / gateway', 'settings', () =>
        {
            const dialog = ctx.dialog('Apply DNS / gateway to selected interfaces');
            dialog.root.append(node('p', targets.map(record => `${record.host.target.hostId} / ${record.nic.name}`).join('\n'), 'hm-muted'), node('p', ctx.t('Matching subnets do not guarantee the same physical network. Check VLANs and explicitly select the intended interfaces.'), 'hm-muted'));
            const build = networkForm(dialog.root, ctx, family, null, true);
            dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Preview changes', 'check', () =>
            {
                const change = build();
                return ctx.preview(targets.map(record => ({ host: record.host, query: { area: 'network', resourceId: record.nic.id }, action: 'network', change, label: record.nic.name })));
            }, true));
        }, true));
        bulk.append(tools);
        bulk.hidden = !targets.length || mode !== 'interfaces';
    }

    /** 選択された方式に合わせて入力欄を更新します。 */
    function updateMode()
    {
        const expectedSwitches2 = 'switches';

        filters.hidden = listing.hidden = mode !== 'interfaces';
        virtual.hidden = mode !== 'virtual';
        switches.hidden = mode !== 'switches';
        status.hidden = mode === 'switches' || !status.childNodes.length;
        bulk.hidden = mode !== 'interfaces' || !selected.size;
        staged.hidden = mode !== 'interfaces' && !hasChanges();
        if (mode === expectedSwitches2 && !switchView)
        {
            switchView = virtualSwitchesView(switches, ctx);
        }
    }

    const reader = createReader(ctx, hosts, () => ({ area: 'network' }), states =>
    {
        readStatus(status, ctx, states);
        const loaded = [...states.values()].filter(state => state.snapshot);
        records = loaded.flatMap(({ host, snapshot }) => snapshot.data.interfaces.map(nic => ({ host, nic, key: host.target.hostId + '/' + nic.id })));
        selected = new Set([...selected].filter(key => records.some(record => record.key === key)));
        renderGroups();
        renderList();
        virtual.replaceChildren();
        virtualNics(virtual, ctx, loaded.map(({ host, snapshot }) => ({ host, state: snapshot.data })));
        updateMode();
    });
    search.oninput = renderList;
    group.onchange = () =>
    {
        selected.clear();
        renderList();
    };
    renderPending();
    updateMode();
    ctx.guard(reader.load);
    return { hasChanges,

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            reader.dispose();
            switchView?.dispose();
        },

        reload: () => Promise.all([reader.load(), switchView?.reload()]) };
}
