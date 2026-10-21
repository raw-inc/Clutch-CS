import { node, field, input, textarea, select, checkbox, values, integer, section } from './dom.js';
import { parseAddresses, addressBytes } from './network-model.js';
import { windowsDnsDefaults } from './dns.js';

/** ネットワーク設定の入力と変更内容を構成します。
 * @returns 呼出し元が使用する値です。
 */
export function networkForm(root, ctx, family, current = null, bulk = false)
{
    const controls = {};

    /** 選択値を反転して表示へ反映します。 */
    function toggle(grid, name, label, automatic = false)
    {
        const unchanged = ctx.t('Keep unchanged') + (current?.[name] == null ? '' : ` · ${ctx.t('Current')}: ${ctx.t(current[name] ? (automatic ? 'Automatic' : 'Enabled') : (automatic ? 'Manual' : 'Disabled'))}`);
        const control = select([['', unchanged], ['true', ctx.t(automatic ? 'Automatic' : 'Enabled')], ['false', ctx.t(automatic ? 'Manual' : 'Disabled')]]);
        controls[name] = control;
        grid.append(field(ctx.t(label), control));
    }

    root.append(node('p', ctx.t('Only explicitly selected fields will change.'), 'hm-muted'));
    if (!bulk)
    {
        const connection = section(ctx.t('Connection and addressing')), grid = node('div', '', 'hm-grid');
        toggle(grid, 'enabled', 'Protocol binding');
        toggle(grid, 'dhcp', 'DHCP');
        if (family === 6)
        {
            toggle(grid, 'routerDiscovery', 'Router discovery (IPv6)');
        }
        toggle(grid, 'automaticMetric', 'Automatic metric', true);
        controls.metric = input(String(current?.metric ?? 10), 'number');
        controls.metric.min = '0';
        controls.metric.max = '9999';
        controls.metric.disabled = true;
        grid.append(field(ctx.t('Interface metric (0–9999)'), controls.metric));
        controls.automaticMetric.onchange = () =>
        {
            controls.metric.disabled = controls.automaticMetric.value !== 'false';
        };
        controls.addresses = textarea((current?.addresses ?? []).filter(item => !item.automatic).map(item => `${item.address}/${item.prefixLength}`).join('\n'), 3);
        controls.addresses.disabled = true;
        controls.addresses.placeholder = family === 4 ? '192.0.2.10/24' : '2001:db8::10/64';
        controls.replaceAddresses = checkbox(ctx.t('Replace static addresses'));
        controls.replaceAddresses.control.onchange = () =>
        {
            controls.addresses.disabled = !controls.replaceAddresses.control.checked;
        };
        connection.append(grid, controls.replaceAddresses.root, field(ctx.t('Static addresses (address/prefix)'), controls.addresses));
        root.append(connection);
    }
    const dns = section(ctx.t('DNS')), dnsGrid = node('div', '', 'hm-grid');
    toggle(dnsGrid, 'automaticDns', 'DNS configuration', true);
    if (current && (!current.dns.length || windowsDnsDefaults(current, family)))
    {
        controls.automaticDns.firstElementChild.textContent = `${ctx.t('Keep unchanged')} · ${ctx.t('Current')}: ${ctx.t('No DNS servers')}`;
    }
    const none = node('option', ctx.t('No DNS servers'));
    none.value = 'none';
    controls.automaticDns.append(none);
    dns.append(node('p', ctx.t('No DNS servers clears the configured list. Disable DHCP and IPv6 router discovery first, or select that change explicitly.'), 'hm-muted'));
    controls.dns = textarea(current?.dns?.join('\n') ?? '', 3);
    controls.dns.disabled = true;
    controls.dns.placeholder = family === 4 ? '192.0.2.53\n192.0.2.54' : '2001:db8::53';
    controls.automaticDns.onchange = () =>
    {
        controls.dns.disabled = controls.automaticDns.value !== 'false';
    };
    dnsGrid.append(field(ctx.t('DNS servers (in priority order)'), controls.dns));
    dns.append(dnsGrid);
    root.append(dns);
    const gateway = section(ctx.t('Default gateways'));
    controls.gateways = textarea(current?.gateways?.join('\n') ?? '', 2);
    controls.gateways.disabled = true;
    controls.replaceGateways = checkbox(ctx.t('Replace default gateways'));
    const note = node('p', ctx.t('Gateway changes require DHCP (and IPv6 router discovery) to be disabled explicitly or already disabled.'), 'hm-muted');
    note.hidden = true;
    controls.replaceGateways.control.onchange = () =>
    {
        controls.gateways.disabled = !controls.replaceGateways.control.checked;
        note.hidden = controls.gateways.disabled;
    };
    gateway.append(controls.replaceGateways.root, field(ctx.t('Default gateways'), controls.gateways, ctx.t('Leave empty to remove default gateways.')), note);
    root.append(gateway);
    return () =>
    {
        const expectedNone = 'none';

        const change = { family };
        for (const name of ['enabled', 'dhcp', 'routerDiscovery', 'automaticMetric', 'automaticDns'])
        {
            if (controls[name]?.value)
            {
                change[name] = controls[name].value === 'true';
            }
        }
        if (change.automaticMetric === false)
        {
            change.metric = integer(controls.metric.value, 0, 9999);
        }
        if (controls.replaceAddresses?.control.checked)
        {
            change.addresses = parseAddresses(controls.addresses.value, family);
        }
        if (controls.automaticDns.value === expectedNone)
        {
            change.dns = [];
        }
        else if (change.automaticDns === false)
        {
            change.dns = values(controls.dns.value);
            if (!change.dns.length)
            {
                throw new Error(ctx.t('DNS servers (in priority order)'));
            }
        }
        if (controls.replaceGateways.control.checked)
        {
            change.gateways = values(controls.gateways.value);
        }
        for (const value of [...(change.dns ?? []), ...(change.gateways ?? [])])
        {
            addressBytes(value, family);
        }
        if (Object.keys(change).length < 2)
        {
            throw new Error(ctx.t('No changes'));
        }
        return change;
    };
}
