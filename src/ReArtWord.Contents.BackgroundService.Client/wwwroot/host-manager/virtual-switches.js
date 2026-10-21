import { hasControlCharacters } from './input-validation.js';
import { node, field, input, select, checkbox, table, action, badge, empty } from './dom.js';
import { createReader, readStatus, noSelection } from './loader.js';

/** 仮想スイッチの名前と構成条件を検証します。 */
export function validateVirtualSwitch(change, state)
{
    const expectedExternal = 'External';

    if (!change.name || change.name !== change.name.trim() || (64) < (change.name.length) || (/[*?[\]]/.test(change.name) || hasControlCharacters(change.name, { includeDelete: true })) || !['External', 'Internal', 'Private'].includes(change.kind))
    {
        throw new Error('Device.InvalidVirtualSwitch');
    }
    if (state.switches.some(item => item.switch.name.toLocaleLowerCase() === change.name.toLocaleLowerCase()))
    {
        throw new Error('Device.VirtualSwitchExists');
    }
    if (change.kind === expectedExternal && (!state.physicalInterfaces.some(item => item.id === change.physicalInterfaceId && !item.inUse) || typeof change.allowManagementOS !== 'boolean'))
    {
        throw new Error('Device.PhysicalInterfaceInUse');
    }
}

/** 仮想スイッチの一覧と変更操作を構成します。
 * @returns dispose・reloadを含む結果です。
 */
export function virtualSwitchesView(root, ctx)
{
    const hosts = structuredClone(ctx.hosts());
    if (!hosts.length)
    {
        return noSelection(root, ctx);
    }
    const status = node('div'), listing = node('div'), toolbar = node('div', '', 'hm-toolbar');
    let entries = [];
    const create = action(ctx, 'Create virtual switch', 'plus', () => form(), true);
    create.disabled = true;
    toolbar.append(create, node('span', ctx.t('Manage External, Internal and Private switches. Review connected adapters before deleting.'), 'hm-muted'));
    root.append(toolbar, status, listing);

    /** 現在の値を表示内容へ反映します。 */
    function render(states)
    {
        readStatus(status, ctx, states);
        entries = [...states.values()].filter(item => item.snapshot).map(item => ({ host: item.host, state: item.snapshot.data }));
        create.disabled = !entries.length;
        listing.replaceChildren();
        const rows = table(['Virtual switch', 'Host', 'Switch type', 'Physical interface', 'Connected adapters', 'Edit'].map(ctx.t));
        let count = 0;
        for (const entry of entries)
        {
            for (const item of entry.state.switches)
            {
                count++;
                const location = node('div', item.switch.physicalInterface || '—'), connections = node('div');
                const managed = item.ports.filter(port => port.managementOS).length, guests = item.ports.length - managed;
                connections.append(node('span', `${ctx.t('Management OS')}: ${managed}`), node('small', `${ctx.t('Virtual machines')}: ${guests}`));
                const actions = node('div', '', 'hm-actions'), remove = action(ctx, 'Delete', 'trash', () => ctx.preview([{ host: entry.host, query: { area: 'network', resourceId: 'switch/' + item.switch.id }, action: 'virtualSwitchDelete', change: {}, label: `${ctx.t('Delete virtual switch')} · ${item.switch.name}` }]));
                remove.classList.add('danger');
                remove.disabled = !item.editable;
                actions.append(remove);
                if (item.problem)
                {
                    actions.append(node('small', ctx.t(item.problem), 'hm-muted'));
                }
                rows.add([ctx.button(item.switch.name, () => details(entry, item), 'hm-link'), entry.host.target.hostId, badge(ctx.t(item.switch.kind)), location, connections, actions]);
            }
        }
        if (entries.length)
        {
            listing.append(count ? rows.root : empty(ctx.t('No virtual switches'), ctx.t('Create a switch to connect virtual adapters.'), 'network'));
        }
    }

    /** 折り畳み可能な詳細表示を構成します。 */
    function details(entry, item)
    {
        const dialog = ctx.dialog('Virtual switch');
        dialog.root.append(node('p', `${entry.host.target.hostId} · ${item.switch.name}`, 'hm-muted'));
        const facts = table([ctx.t('Name'), ctx.t('Value')]);
        facts.add([ctx.t('Switch type'), ctx.t(item.switch.kind)]);
        facts.add([ctx.t('Physical interface'), item.switch.physicalInterface || '—']);
        facts.add([ctx.t('Allow management OS sharing'), ctx.t(item.allowManagementOS ? 'Enabled' : 'Disabled')]);
        dialog.root.append(facts.root);
        const ports = table(['Name', 'Management OS', 'Virtual machines'].map(ctx.t));
        for (const port of item.ports)
        {
            ports.add([port.name, ctx.t(port.managementOS ? 'Yes' : 'No'), port.virtualMachine || '—']);
        }
        dialog.root.append(node('h3', ctx.t('Connected adapters')), ports.root);
        if (item.problem)
        {
            dialog.root.append(node('p', ctx.t(item.problem), 'hm-muted'));
        }
        dialog.footer.append(ctx.button('Close', dialog.close));
    }

    /** 編集に必要な入力欄と検証を構成します。 */
    function form()
    {
        let entry = entries[0];
        const dialog = ctx.dialog('Create virtual switch'), grid = node('div', '', 'hm-grid');
        const host = select(entries.map(item => [item.host.target.hostId, item.host.target.hostId]), entry.host.target.hostId), name = input('');
        name.maxLength = 64;
        const kind = select(['Internal', 'External', 'Private'].map(value => [value, ctx.t(value)]), 'Internal'), physical = select([]), sharing = checkbox(ctx.t('Allow management OS sharing'), true);
        const physicalField = field(ctx.t('Physical interface'), physical), hint = node('p', '', 'hm-muted');
        const preview = action(ctx, 'Preview changes', 'check', () =>
        {
            const expectedExternal2 = 'External';

            const change = { name: name.value, kind: kind.value };
            if (kind.value === expectedExternal2)
            {
                change.physicalInterfaceId = physical.value;
                change.allowManagementOS = sharing.control.checked;
            }
            validateVirtualSwitch(change, entry.state);
            return ctx.preview([{ host: entry.host, query: { area: 'network', resourceId: 'switches' }, action: 'virtualSwitchCreate', change, label: `${ctx.t('Create virtual switch')} · ${name.value}` }]);
        }, true);

        /** 現在の設定と状態から表示を更新します。 */
        function update()
        {
            const expectedInternal = 'Internal';

            const previous = physical.value, choices = entry.state.physicalInterfaces.filter(item => !item.inUse);
            physical.replaceChildren(...select(choices.map(item => [item.id, `${item.name} · ${item.description}`])).children);
            physical.value = choices.some(item => item.id === previous) ? previous : choices[0]?.id ?? '';
            const external = kind.value === 'External';
            physicalField.hidden = sharing.root.hidden = !external;
            preview.disabled = external && !choices.length;
            hint.textContent = ctx.t(external ? choices.length ? 'An External switch changes the physical NIC binding and may interrupt this connection. Management OS sharing creates a host vNIC.' : 'No unused physical interfaces are available.'
                : kind.value === expectedInternal ? 'Internal connects this host and its VMs and creates a management OS vNIC.' : 'Private connects VMs only and does not create a management OS vNIC.');
        }

        host.onchange = () =>
        {
            entry = entries.find(item => item.host.target.hostId === host.value);
            update();
        };
        kind.onchange = update;
        grid.append(field(ctx.t('Host'), host), field(ctx.t('Name'), name), field(ctx.t('Switch type'), kind), physicalField);
        dialog.root.append(grid, sharing.root, hint);
        dialog.footer.append(ctx.button('Cancel', dialog.close), preview);
        update();
    }

    const reader = createReader(ctx, hosts, () => ({ area: 'network', resourceId: 'switches' }), render);
    ctx.guard(reader.load);
    return { dispose: reader.dispose, reload: reader.load };
}
