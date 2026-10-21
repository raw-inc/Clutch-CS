import { node, field, input, select, table, details, heading, action, badge, empty } from './dom.js';
import { createReader, readStatus, noSelection } from './loader.js';
import { firewallForm } from './firewall-form.js';
import { firewallBulkView } from './firewall-bulk.js';

/** ファイアウォール設定の照会と編集を構成します。
 * @returns reload・disposeを含む結果です。
 */
export function firewallView(root, ctx)
{
    const hosts = structuredClone(ctx.hosts());
    if (!hosts.length)
    {
        return noSelection(root, ctx);
    }
    if ((1) < (hosts.length))
    {
        return firewallBulkView(root, ctx, hosts);
    }
    let disposed = false;
    const pages = new Map(), requests = new Map();
    const title = heading(ctx.t('Firewall'), `${ctx.t('Selected hosts')}: ${hosts.length}`), status = node('div'), profiles = node('div', '', 'hm-profile-grid');
    const hostPick = select(hosts.map(host => [host.target.hostId, host.target.hostId]), hosts[0].target.hostId), filter = input('', 'search');
    filter.className = 'hm-search';
    filter.placeholder = ctx.t('Search rules');
    filter.setAttribute('aria-label', ctx.t('Search rules'));
    const tools = node('div', '', 'hm-toolbar'), listing = node('div'), pager = node('div', '', 'hm-pagination'), range = node('span');
    const previous = ctx.button('Previous', () => loadPageAsync(Math.max(0, (pages.get(hostPick.value)?.offset ?? 0) - 10))), next = ctx.button('Next', () => loadPageAsync(current()?.nextOffset));
    const search = ctx.button('Search rules', () => loadPageAsync(0));
    const actions = node('div', '', 'hm-actions');
    actions.append(action(ctx, 'Refresh settings', 'refresh', reload), action(ctx, 'Add rule', 'plus', () => firewallForm(ctx, hosts), true));
    title.append(actions);
    tools.append(field(ctx.t('Rules on host'), hostPick), filter, search);
    pager.append(range, previous, next);
    root.append(title, status, profiles, node('h3', ctx.t('Rules')), tools, listing, pager);

    /** 現在選択されている値を取得します。
     * @returns 呼出し元が使用する値です。
     */
    function current()
    {
        return pages.get(hostPick.value)?.snapshot?.data ?? reader.states.get(hostPick.value)?.snapshot?.data;
    }

    /** ファイアウォールのプロファイル状態を表示します。 */
    function renderProfiles()
    {
        profiles.replaceChildren();
        const states = [...reader.states.values()], allLoaded = states.every(state => state.snapshot);
        for (const name of ['Domain', 'Private', 'Public'])
        {
            const known = states.filter(state => state.snapshot?.data.profiles.some(profile => profile.name === name));
            const enabled = known.filter(state => state.snapshot.data.profiles.find(profile => profile.name === name).enabled).length;
            const card = node('section', '', 'hm-profile'), heading = node('div');
            heading.append(node('strong', ctx.t(name)), badge(ctx.t(known.length < hosts.length ? states.some(state => state.loading) ? 'Loading…' : 'Unavailable' : enabled === hosts.length ? 'Enabled' : enabled ? 'Mixed' : 'Disabled'), enabled === hosts.length ? 'good' : enabled ? 'warn' : ''));
            const change = ctx.button('Change profile', () =>
            {
                const dialog = ctx.dialog('Change profile'), state = select([['', ctx.t('Choose setting')], ['true', ctx.t('Enabled')], ['false', ctx.t('Disabled')]]);
                dialog.root.append(node('p', `${ctx.t(name)} · ${hosts.map(host => host.target.hostId).join(', ')}`, 'hm-muted'), field(ctx.t('Status'), state));
                const preview = action(ctx, 'Preview changes', 'check', () => ctx.preview(hosts.map(host => ({ host, query: { area: 'firewall', resourceId: 'profile/' + name }, action: 'firewallProfile', change: { enabled: state.value === 'true' }, label: `${ctx.t('Profile')} · ${ctx.t(name)}` }))), true);
                preview.disabled = true;
                state.onchange = () =>
                {
                    preview.disabled = !state.value;
                };
                dialog.footer.append(ctx.button('Cancel', dialog.close), preview);
            });
            change.disabled = !allLoaded || known.length !== hosts.length;
            card.append(heading, node('span', `${enabled} / ${hosts.length} · ${ctx.t('Enabled')}`, 'hm-muted'), change);
            profiles.append(card);
        }
    }

    /** 規則の条件と適用範囲を詳細表示します。 */
    function ruleDetails(host, rule)
    {
        const dialog = ctx.dialog('Rule details'), facts = table([ctx.t('Name'), ctx.t('Value')]);
        dialog.root.append(node('p', `${host.target.hostId} · ${rule.name}`, 'hm-muted'));
        for (const [label, value] of [['Direction', ctx.t(rule.direction)], ['Action', ctx.t(rule.action)], ['Protocol', rule.protocol], ['Profile', rule.profile], ['Local ports', rule.localPorts], ['Remote ports', rule.remotePorts], ['Local addresses', rule.localAddresses], ['Remote addresses', rule.remoteAddresses], ['Program', rule.program]])
        {
            facts.add([ctx.t(label), Array.isArray(value) ? value.join(', ') : value]);
        }
        dialog.root.append(facts.root, details(ctx.t('Technical details'), node('code', rule.id)));
        dialog.footer.append(ctx.button('Close', dialog.close));
    }

    /** 対象のファイアウォール規則を一覧表示します。 */
    function renderRules()
    {
        const host = hosts.find(host => host.target.hostId === hostPick.value), data = current(), page = pages.get(hostPick.value);
        const busy = requests.has(hostPick.value) || reader.states.get(hostPick.value)?.loading;
        previous.disabled = !!busy || !page?.offset;
        next.disabled = !!busy || data?.nextOffset == null;
        search.disabled = !!busy;
        listing.setAttribute('aria-busy', String(!!busy));
        listing.replaceChildren();
        range.textContent = '';
        if (!data)
        {
            return;
        }
        if (!data.rules.length)
        {
            listing.append(empty(ctx.t('No matching rules'), ctx.t('Try another rule name or add a rule.'), 'shield'));
            return;
        }
        const rows = table(['Name', 'Direction / action', 'Profile', 'Status', 'Edit'].map(ctx.t));
        for (const rule of data.rules)
        {
            const expectedAllow = 'Allow';

            const name = node('div');
            name.append(ctx.button(rule.name, () => ruleDetails(host, rule), 'hm-link'), node('small', `${rule.protocol} · ${rule.localPorts.join(', ')}`));
            const behavior = node('div', '', 'hm-stacked');
            behavior.append(node('span', ctx.t(rule.direction)), badge(ctx.t(rule.action), rule.action === expectedAllow ? '' : 'warn'));
            const actions = node('div', '', 'hm-actions');
            if (rule.editable)
            {
                actions.append(ctx.button(rule.enabled ? 'Disable rule' : 'Enable rule', () => ctx.preview([{ host, query: { area: 'firewall', resourceId: 'rule/' + rule.id }, action: 'firewallRuleEnabled', change: { enabled: !rule.enabled }, label: rule.name }])),
                    ctx.button('Remove rule', () => ctx.preview([{ host, query: { area: 'firewall', resourceId: 'rule/' + rule.id }, action: 'firewallRuleRemove', change: {}, label: rule.name }]), 'danger'));
            }
            else
            {
                actions.append(badge(ctx.t('Read only')));
            }
            rows.add([name, behavior, rule.profile, badge(ctx.t(rule.enabled ? 'Enabled' : 'Disabled'), rule.enabled ? 'good' : ''), actions]);
        }
        listing.append(rows.root);
        const start = page?.offset ?? 0;
        range.textContent = `${start + 1}–${start + data.rules.length} · ${ctx.t('Rules')}` + (page?.filter ? ` · ${page.filter}` : '');
    }

    /** 指定位置にある一覧のページを取得します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function loadPageAsync(offset)
    {
        if (offset == null)
        {
            return;
        }
        const id = hostPick.value, host = hosts.find(host => host.target.hostId === id), text = filter.value.trim();
        requests.get(id)?.abort();
        const controller = new AbortController();
        requests.set(id, controller);
        renderRules();
        try
        {
            const snapshot = await ctx.read(host, { area: 'firewall', offset, filter: text || null }, AbortSignal.any([ctx.signal, controller.signal]));
            if (!disposed && requests.get(id) === controller)
            {
                pages.set(id, { snapshot, offset, filter: text });
            }
        }
        finally
        {
            if (requests.get(id) === controller)
            {
                requests.delete(id);
            }
            if (!disposed)
            {
                renderRules();
            }
        }
    }

    /** 最新状態を取得し、現在の表示へ反映します。
     * @returns 呼出し元が使用する値です。
     */
    function reload()
    {
        requests.forEach(controller => controller.abort());
        requests.clear();
        pages.clear();
        filter.value = '';
        return reader.load();
    }

    const reader = createReader(ctx, hosts, () => ({ area: 'firewall', offset: 0 }), states =>
    {
        readStatus(status, ctx, states);
        renderProfiles();
        renderRules();
    });
    hostPick.onchange = () =>
    {
        filter.value = pages.get(hostPick.value)?.filter ?? '';
        renderRules();
    };
    filter.onkeydown = event =>
    {
        const expectedEnter = 'Enter';

        if (event.key === expectedEnter)
        {
            event.preventDefault();
            ctx.guard(() => loadPageAsync(0));
        }
    };
    ctx.guard(reader.load);
    return { reload,

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            disposed = true;
            reader.dispose();
            requests.forEach(controller => controller.abort());
        } };
}
