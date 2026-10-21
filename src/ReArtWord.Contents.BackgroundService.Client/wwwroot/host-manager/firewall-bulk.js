import { node, heading, action, input, table, details } from './dom.js';
import { createReader, readStatus } from './loader.js';
import { triState, aggregateState } from './tri-state.js';
import { firewallGroupKey, aggregateFirewall, readFirewallTemplates, firewallChanges } from './firewall-bulk-model.js';
import { firewallForm } from './firewall-form.js';

/** ファイアウォールの一括編集画面を構成します。
 * @returns reload・hasChanges・disposeを含む結果です。
 */
export function firewallBulkView(root, ctx, hosts)
{
    const choices = new Map(), profileChoices = new Map(), stagedGroups = new Map();
    let loaded = false, offset = 0, pageReader = null, groups = [];
    const title = heading(ctx.t('Firewall'), ctx.t('Compare definitions across hosts before enabling, disabling or copying a rule.'));
    const status = node('div'), pageStatus = node('div'), profiles = node('div', '', 'hm-profile-grid'), listing = node('div'), tools = node('div', '', 'hm-toolbar');
    const filter = input('', 'search');
    filter.setAttribute('aria-label', ctx.t('Search rules'));
    filter.placeholder = ctx.t('Search rules');
    const previous = ctx.button('Previous', () =>
        {
            offset = Math.max(0, offset - 8);
            loadPage();
        }), next = ctx.button('Next', () =>
        {
            offset += 8;
            loadPage();
        }), range = node('span');
    tools.append(filter, ctx.button('Search rules', () =>
    {
        offset = 0;
        loadPage();
    }), previous, range, next);
    const count = node('span'), preview = action(ctx, 'Preview changes', 'check', previewChangesAsync, true), bar = node('div', '', 'hm-selection-bar');
    bar.append(count, ctx.button('Reset changes', () =>
    {
        reset();
        renderProfiles();
        renderRules();
    }), preview);
    title.append(action(ctx, 'Refresh settings', 'refresh', () => ctx.discard(reload, hasChanges())), action(ctx, 'Add rule', 'plus', () => ctx.discard(() =>
    {
        reset();
        firewallForm(ctx, hosts);
    }, hasChanges()), true));
    root.append(title, node('p', ctx.t('other leaves each host unchanged. Missing rules are copied only when their complete conditions are supported.'), 'hm-muted'), status, profiles, tools, pageStatus, listing, bar);

    /** 現在選択されている値を取得します。
     * @returns 呼出し元が使用する値です。
     */
    const current = () => hosts.map(host => reader.states.get(host.target.hostId));

    /** 一時状態を初期値へ戻します。 */
    function reset()
    {
        choices.clear();
        profileChoices.clear();
        stagedGroups.clear();
    }

    /** 元の値から変更された項目があるか判定します。
     * @returns 呼出し元が使用する判定結果です。
     */
    function hasChanges()
    {
        return (0) < (choices.size) || (0) < (profileChoices.size);
    }

    /** 現在の設定と状態から表示を更新します。 */
    function update()
    {
        count.textContent = `${ctx.t('Changes')}: ${choices.size + profileChoices.size}`;
        preview.disabled = !loaded || !hasChanges();
    }

    /** ファイアウォールのプロファイル状態を表示します。 */
    function renderProfiles()
    {
        profiles.replaceChildren();
        for (const name of ['Domain', 'Private', 'Public'])
        {
            const values = current().map(state => state?.snapshot?.data.profiles.find(profile => profile.name === name)?.enabled);
            const state = aggregateState(values), card = node('section', '', 'hm-profile');
            card.append(node('strong', ctx.t(name)));
            const toggle = triState(ctx, ctx.t(name), profileChoices.get(name) ?? state, value =>
            {
                const expectedOther = 'other';

                if (value === expectedOther || value === state)
                {
                    profileChoices.delete(name);
                }
                else
                {
                    profileChoices.set(name, value);
                }
                update();
            });
            for (const button of toggle.children)
            {
                button.disabled = !loaded || values.some(value => value == null);
            }
            card.append(toggle);
            profiles.append(card);
        }
        update();
    }

    /** 対象のファイアウォール規則を一覧表示します。 */
    function renderRules()
    {
        listing.replaceChildren();
        const rows = table(['Rule', 'Traffic', 'Across selected hosts', 'Host states'].map(ctx.t));
        for (const group of groups)
        {
            const title = node('div', group.rule.name);
            title.append(node('small', `${group.rule.direction} · ${group.rule.action} · ${group.rule.profile}`));
            const traffic = node('div', `${group.rule.protocol} · ${group.rule.localPorts.join(', ')}`);
            traffic.append(node('small', group.rule.program));
            const toggle = triState(ctx, `${group.rule.name} · ${group.rule.protocol} · ${group.rule.localPorts.join(', ')}`, choices.get(group.key) ?? group.state, value =>
            {
                const expectedOther2 = 'other';

                if (value === expectedOther2 || value === group.state)
                {
                    choices.delete(group.key);
                    stagedGroups.delete(group.key);
                }
                else
                {
                    choices.set(group.key, value);
                    stagedGroups.set(group.key, group.group);
                }
                update();
            });
            if (!group.supported)
            {
                for (const button of toggle.children)
                {
                    button.disabled = true;
                }
                toggle.title = ctx.t('This rule has conditions that cannot be reproduced on every selected host.');
            }
            const states = table(['Host', 'Status'].map(ctx.t));
            hosts.forEach((host, index) => states.add([host.target.hostId, group.values[index].length ? group.values[index].map(rule => ctx.t(rule.enabled ? 'enable' : 'disable')).join(', ') : ctx.t('Not defined')]));
            const extra = node('div');
            extra.append(details(ctx.t('Host states'), states.root));
            if (!group.supported)
            {
                extra.append(node('small', ctx.t('This rule has conditions that cannot be reproduced on every selected host.'), 'hm-muted'));
            }
            rows.add([title, traffic, toggle, extra]);
        }
        listing.append(rows.root);
        update();
    }

    /** 指定位置にある一覧のページを取得します。 */
    function loadPage()
    {
        pageReader?.dispose();
        groups = [];
        renderRules();
        if (!loaded)
        {
            previous.disabled = next.disabled = true;
            return;
        }
        const text = filter.value.trim().toLocaleLowerCase(), keys = [...new Set(current().flatMap(state => state.snapshot.data.rules.filter(rule => `${rule.name} ${rule.id}`.toLocaleLowerCase().includes(text)).map(firewallGroupKey)))].sort();
        if ((keys.length) <= (offset))
        {
            offset = 0;
        }
        const selected = new Set(keys.slice(offset, offset + 8));
        previous.disabled = !offset;
        next.disabled = (keys.length) <= (offset + 8);
        range.textContent = `${Math.min(offset + 1, keys.length)}–${Math.min(offset + 8, keys.length)} / ${keys.length}`;
        const scoped = { ...ctx,

            read: async (host, query, signal) => ({ data: { rules: await readFirewallTemplates(ctx, host, reader.states.get(host.target.hostId).snapshot.data.rules.filter(rule => selected.has(firewallGroupKey(rule))), signal) } }) };
        pageReader = createReader(scoped, hosts, () => ({}), states =>
        {
            readStatus(pageStatus, ctx, states);
            if ([...states.values()].every(state => state.snapshot))
            {
                groups = aggregateFirewall(hosts.map(host => states.get(host.target.hostId).snapshot.data.rules));
                renderRules();
            }
        });
        ctx.guard(pageReader.load);
    }

    /** 適用前の変更内容を確認できる形へまとめます。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function previewChangesAsync()
    {
        profiles.inert = tools.inert = listing.inert = true;
        try
        {
            return await ctx.preview(async () =>
            {
                const planned = [];
                for (const [name, state] of profileChoices)
                {
                    for (const host of hosts)
                    {
                        planned.push({ host, query: { area: 'firewall', resourceId: 'profile/' + name }, action: 'firewallProfile', patch: { kind: 'enabled', enabled: state === 'enable' }, label: ctx.t(name) });
                    }
                }
                if (choices.size)
                {
                    const keys = new Set(stagedGroups.values()), fresh = [];

                    // Re-read complete inventories so a newly added equivalent rule is never treated as missing.
                    for (const host of hosts)
                    {
                        const inventory = await ctx.read(host, { area: 'firewall', resourceId: 'overview' });
                        fresh.push(await readFirewallTemplates(ctx, host, inventory.data.rules.filter(rule => keys.has(firewallGroupKey(rule))), ctx.signal));
                    }
                    const currentGroups = aggregateFirewall(fresh);
                    if ([...choices.keys()].some(key => !currentGroups.some(group => group.key === key)))
                    {
                        throw new Error(ctx.t('Settings changed. Refresh before editing again.'));
                    }
                    planned.push(...firewallChanges(hosts, currentGroups, choices, ctx.t));
                }
                return planned;
            }, reset);
        }
        finally
        {
            profiles.inert = tools.inert = listing.inert = false;
        }
    }

    /** 最新状態を取得し、現在の表示へ反映します。
     * @returns 呼出し元が使用する値です。
     */
    function reload()
    {
        reset();
        pageReader?.dispose();
        offset = 0;
        return reader.load();
    }

    const reader = createReader(ctx, hosts, () => ({ area: 'firewall', resourceId: 'overview' }), states =>
    {
        readStatus(status, ctx, states);
        loaded = states.size === hosts.length && [...states.values()].every(state => state.snapshot);
        renderProfiles();
        loadPage();
    });
    filter.onkeydown = event =>
    {
        const expectedEnter = 'Enter';

        if (event.key === expectedEnter)
        {
            event.preventDefault();
            offset = 0;
            loadPage();
        }
    };
    ctx.guard(reader.load);
    return { reload, hasChanges,

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            reader.dispose();
            pageReader?.dispose();
        } };
}
