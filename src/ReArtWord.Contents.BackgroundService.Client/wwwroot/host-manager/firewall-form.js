import { node, input, select, checkbox, values, field, section, details, action } from './dom.js';

/** ファイアウォール規則の入力と検証を構成します。 */
export function firewallForm(ctx, hosts)
{
    const dialog = ctx.dialog('Add rule'), basic = section(ctx.t('Rule')), grid = node('div', '', 'hm-grid');
    const name = input(''), direction = select(['Inbound', 'Outbound'].map(value => [value, ctx.t(value)]), 'Inbound'), effect = select(['Allow', 'Block'].map(value => [value, ctx.t(value)]), 'Allow'), enabled = checkbox(ctx.t('Enabled'), true);
    basic.append(field(ctx.t('Name'), name));
    grid.append(field(ctx.t('Direction'), direction), field(ctx.t('Action'), effect));
    basic.append(grid, enabled.root);
    const traffic = section(ctx.t('Traffic')), trafficGrid = node('div', '', 'hm-grid');
    const protocol = select(['TCP', 'UDP', 'Any', 'ICMPv4', 'ICMPv6'].map(value => [value, value]), 'TCP'), localPorts = input('Any'), remotePorts = input('Any'), localAddresses = input('Any'), remoteAddresses = input('Any'), program = input('Any');
    localPorts.placeholder = '80, 443, 8000-8100';
    trafficGrid.append(field(ctx.t('Protocol'), protocol), field(ctx.t('Local ports'), localPorts));
    traffic.append(trafficGrid);
    protocol.onchange = () =>
    {
        localPorts.disabled = remotePorts.disabled = !['TCP', 'UDP'].includes(protocol.value);
    };
    const scope = section(ctx.t('Profiles')), all = checkbox(ctx.t('Any'), true), profileChoices = ['Domain', 'Private', 'Public'].map(value => ({ value, ...checkbox(ctx.t(value)) })), choices = node('div', '', 'hm-toolbar');
    profileChoices.forEach(choice =>
    {
        choice.control.disabled = true;
        choices.append(choice.root);
    });
    all.control.onchange = () => profileChoices.forEach(choice =>
    {
        choice.control.disabled = all.control.checked;
    });
    scope.append(all.root, choices);
    const advanced = node('div', '', 'hm-grid');
    for (const [label, control] of [['Remote ports', remotePorts], ['Local addresses', localAddresses], ['Remote addresses', remoteAddresses], ['Program', program]])
    {
        advanced.append(field(ctx.t(label), control));
    }
    dialog.root.append(node('p', `${ctx.t('Affected hosts')}: ${hosts.map(host => host.target.hostId).join(', ')}`, 'hm-muted'), basic, traffic, scope, details(ctx.t('Advanced conditions'), advanced));
    dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Preview changes', 'check', () =>
    {
        const profile = all.control.checked ? 'Any' : profileChoices.filter(choice => choice.control.checked).map(choice => choice.value).join(',');
        if (!name.value.trim() || !profile)
        {
            throw new Error(ctx.t('Validation failed. Check the input values.'));
        }
        const ports = ['TCP', 'UDP'].includes(protocol.value);
        return ctx.preview(hosts.map(host =>
        {
            const operationId = crypto.randomUUID(), id = 'Contents.HostManager.' + operationId.replaceAll('-', '');
            return { host, operationId, query: { area: 'firewall', resourceId: 'rule/' + id }, action: 'firewallRuleAdd', label: name.value,
                change: { rule: { id, name: name.value, direction: direction.value, action: effect.value, enabled: enabled.control.checked, profile, protocol: protocol.value,
                    localPorts: ports ? values(localPorts.value) : ['Any'], remotePorts: ports ? values(remotePorts.value) : ['Any'],
                    localAddresses: values(localAddresses.value), remoteAddresses: values(remoteAddresses.value), program: program.value, editable: true } } };
        }));
    }, true));
}
