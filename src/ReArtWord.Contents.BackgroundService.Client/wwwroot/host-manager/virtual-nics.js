import { hasControlCharacters } from './input-validation.js';
import { node, field, input, select, table, integer, action, badge, details, empty } from './dom.js';
import { vlanLabel, vlanTone } from './vlan.js';

/** 仮想NICの一覧と編集操作を構成します。 */
export function virtualNics(root, ctx, entries)
{
    const toolbar = node('div', '', 'hm-toolbar'), available = entries.filter(entry => entry.state.switches.length);
    const create = action(ctx, 'Create management OS vNIC', 'plus', () => form(available[0], null), true);
    create.disabled = !available.length;
    toolbar.append(create, node('span', ctx.t('Connect management OS adapters to an existing switch. Create or delete switches in Virtual switches.'), 'hm-muted'));
    root.append(toolbar);
    if (!entries.length)
    {
        return;
    }
    const rows = table(['Name', 'Host / virtual switch', 'VLAN mode', 'Edit'].map(ctx.t));
    let count = 0;
    for (const entry of entries)
    {
        const { host, state } = entry;
        if (state.hyperVProblem)
        {
            root.append(node('p', `${host.target.hostId} · ${ctx.t('Hyper-V details are unavailable. Check Hyper-V installation and the Core service account’s permissions.')}`, 'hm-read-error'));
        }
        for (const nic of state.virtualNics)
        {
            count++;
            const adapter = state.interfaces.find(item => item.virtualNicId === nic.id), name = node('div');
            name.append(ctx.button(nic.name, () => previewNic(entry, nic, adapter), 'hm-link'), node('small', adapter?.ipv4?.addresses.map(item => `${item.address}/${item.prefixLength}`).join(', ') || nic.macAddress));
            const location = node('div', host.target.hostId);
            location.append(node('small', nic.switchName));
            const actions = node('div', '', 'hm-actions'), edit = action(ctx, 'Set access VLAN', 'settings', () => form(entry, nic));
            edit.disabled = !['Access', 'Untagged'].includes(nic.vlanMode);
            const remove = action(ctx, 'Delete', 'trash', () => ctx.preview([{ host, query: { area: 'network', resourceId: 'vnic/' + nic.id }, action: 'virtualNicDelete', change: {}, label: `${ctx.t('Delete management OS vNIC')} · ${nic.name}` }]));
            remove.classList.add('danger');
            actions.append(edit, remove);
            rows.add([name, location, badge(vlanLabel(nic.vlanMode, nic.vlanId, ctx.t), vlanTone(nic.vlanMode)), actions]);
        }
        for (const adapter of state.interfaces.filter(item => item.hyperV && !item.virtualNicId))
        {
            count++;
            rows.add([ctx.button(adapter.name, () => previewNic(entry, null, adapter), 'hm-link'), host.target.hostId, badge(ctx.t('Unconfirmed'), 'warn'), badge(ctx.t('Read only'))]);
        }
    }
    root.append(count ? rows.root : empty(ctx.t('No virtual interfaces'), ctx.t('Create a management OS adapter on an existing virtual switch.'), 'network'));
    const switches = table(['Host', 'Virtual switch', 'Physical interface'].map(ctx.t));
    for (const { host, state } of entries)
    {
        for (const item of state.switches)
        {
            switches.add([host.target.hostId, `${item.name} · ${item.kind}`, item.physicalInterface]);
        }
    }
    root.append(details(ctx.t('Virtual switches'), switches.root));

    /** 仮想NICに対応するIPアドレスと経路情報を表示します。 */
    function appendAddressFacts(facts, adapter)
    {
        if (adapter)
        {
            for (const [family, configuration] of [['IPv4', adapter.ipv4], ['IPv6', adapter.ipv6]])
            {
                facts.add([family, configuration?.addresses.map(address => `${address.address}/${address.prefixLength}`).join('\n') ?? '—']);
                facts.add([`${family} · ${ctx.t('DNS')}`, configuration?.dns.join('\n') ?? '—']);
                facts.add([`${family} · ${ctx.t('Gateway')}`, configuration?.gateways.join('\n') ?? '—']);
            }
        }
    }

    /** 仮想NICの変更前後を確認用にまとめます。 */
    function previewNic(entry, nic, adapter)
    {
        const dialog = ctx.dialog('Virtual NICs');
        dialog.root.append(node('p', `${entry.host.target.hostId} · ${adapter?.name ?? nic.name}`, 'hm-muted'));
        const facts = table([ctx.t('Name'), ctx.t('Value')]);
        const mode = nic?.vlanMode ?? adapter?.vlanMode;
        facts.add([ctx.t('Virtual switch'), nic?.switchName ?? ctx.t('Unconfirmed')]);
        facts.add([ctx.t('VLAN mode'), badge(vlanLabel(mode, nic?.vlanId ?? adapter?.vlanId, ctx.t), vlanTone(mode))]);
        facts.add(['MAC', adapter?.macAddress ?? nic.macAddress]);
        appendAddressFacts(facts, adapter);
        dialog.root.append(facts.root);
        dialog.footer.append(ctx.button('Close', dialog.close));
    }

    /** 編集に必要な入力欄と検証を構成します。 */
    function form(initial, nic)
    {
        const expectedAccess = 'Access';

        let entry = initial;
        const dialog = ctx.dialog(nic ? 'Set access VLAN' : 'Create management OS vNIC'), grid = node('div', '', 'hm-grid');
        const name = input(nic?.name ?? ''), hostPick = select(available.map(item => [item.host.target.hostId, item.host.target.hostId]), entry.host.target.hostId);
        const virtualSwitch = select(entry.state.switches.map(item => [item.id, item.name]), nic?.switchId ?? entry.state.switches[0]?.id ?? '');
        const tagged = select([['', ctx.t('Untagged')], ['access', ctx.t('Access VLAN (1–4094)')]], nic?.vlanMode === expectedAccess ? 'access' : ''), vlan = input(String(nic?.vlanId ?? 1), 'number');
        vlan.min = '1';
        vlan.max = '4094';
        vlan.disabled = !tagged.value;
        tagged.onchange = () =>
        {
            vlan.disabled = !tagged.value;
        };
        hostPick.onchange = () =>
        {
            entry = available.find(item => item.host.target.hostId === hostPick.value);
            virtualSwitch.replaceChildren(...select(entry.state.switches.map(item => [item.id, item.name])).children);
            virtualSwitch.value = entry.state.switches[0]?.id ?? '';
        };
        if (!nic)
        {
            grid.append(field(ctx.t('Host'), hostPick), field(ctx.t('Name'), name), field(ctx.t('Virtual switch'), virtualSwitch));
        }
        else
        {
            dialog.root.append(node('p', `${entry.host.target.hostId} · ${nic.name} · ${nic.switchName}`, 'hm-muted'));
        }
        grid.append(field(ctx.t('VLAN mode'), tagged), field(ctx.t('Access VLAN (1–4094)'), vlan));
        dialog.root.append(grid);
        dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Preview changes', 'check', () =>
        {
            if (!nic && (!name.value.trim() || (/[*?[\]]/.test(name.value) || hasControlCharacters(name.value))))
            {
                throw new Error(ctx.t('Validation failed. Check the input values.'));
            }
            const change = { vlanId: tagged.value ? integer(vlan.value, 1, 4094) : null };
            if (!nic)
            {
                change.name = name.value;
                change.switchId = virtualSwitch.value;
            }
            return ctx.preview([{ host: entry.host, query: { area: 'network', resourceId: nic ? 'vnic/' + nic.id : null }, action: nic ? 'virtualNicVlan' : 'virtualNicCreate', change, label: `${ctx.t('Virtual NICs')} · ${name.value}` }]);
        }, true));
    }
}
