import { node, checkbox, table, details, action, badge, empty } from './dom.js';
import { validateNetwork } from './network-model.js';
import { vlanLabel, vlanTone } from './vlan.js';
import { validateVirtualSwitch } from './virtual-switches.js';
import { resolveBulkPatch } from './bulk-patches.js';
import { createReader } from './loader.js';
import { displayModeLabel } from './display.js';
import { powerPreview } from './power-model.js';
import { windowsUpdatePreview } from './windows-update.js';
import { loginPreview } from './login.js';
import { wakePreview } from './wake-on-lan.js';

/** 現在の値を確認用の説明に変換します。
 * @returns 呼出し元が使用する値です。
 */
export function describe(value, translate = key => key)
{
    if (value == null)
    {
        return '—';
    }
    if (typeof value === 'boolean')
    {
        return translate(value ? 'Enabled' : 'Disabled');
    }
    if (typeof value !== 'object')
    {
        return value === '' ? translate('Empty value') : String(value);
    }
    if ('content' in value)
    {
        return value.content || translate('Empty file');
    }
    if ('address' in value && 'prefixLength' in value)
    {
        return `${value.address}/${value.prefixLength}`;
    }
    const names = { name: 'Name', value: 'Value', expandable: 'Expand variable references', enabled: 'Status', family: 'Address family', dhcp: 'DHCP',
        automaticMetric: 'Automatic metric', metric: 'Metric', addresses: 'Addresses', automaticDns: 'Automatic DNS', dns: 'DNS servers (in priority order)',
        gateways: 'Default gateways', routerDiscovery: 'Router discovery (IPv6)', direction: 'Direction', action: 'Action', protocol: 'Protocol', profile: 'Profile',
        localPorts: 'Local ports', remotePorts: 'Remote ports', localAddresses: 'Local addresses', remoteAddresses: 'Remote addresses', program: 'Program', switchName: 'Virtual switch', vlanMode: 'VLAN mode' };
    return Object.entries(value).filter(([key, item]) => item !== null && !['revision', 'detailsHash', 'id', 'editable'].includes(key))
        .map(([key, item]) => `${translate(names[key] ?? key)}: ${Array.isArray(item) ? item.map(value => describe(value, translate)).join(', ') : describe(item, translate)}`).join('\n');
}

/** 仮想NICの作成とVLAN変更を比較する値を構成します。
 * @returns 変更前と変更後の値です。
 */
function virtualNicValues(item, translate)
{
    let before = item.before.data;
    const creating = item.action === 'virtualNicCreate';
    const after = { name: item.change.name ?? before.name, switchName: creating ? before.switches.find(value => value.id === item.change.switchId)?.name ?? item.change.switchId : before.switchName,
        vlanMode: vlanLabel(item.change.vlanId == null ? 'Untagged' : 'Access', item.change.vlanId, translate) };
    before = creating ? null : { name: before.name, switchName: before.switchName, vlanMode: vlanLabel(before.vlanMode, before.vlanId, translate) };
    return [before, after];
}

/** 変更前と変更後の値を比較表示へ変換します。
 * @returns 呼出し元が使用する値です。
 */
export function previewValues(item, translate = key => key)
{
    const custom = { power: powerPreview, display: displayPreview, windowsUpdate: windowsUpdatePreview, login: loginPreview, wakeOnLan: wakePreview };
    if (custom[item.action])
    {
        return custom[item.action](item, translate);
    }
    const expectedNetworkprotocols = 'networkProtocols';
    const expectedVirtualswitchcreate = 'virtualSwitchCreate';
    const expectedVirtualswitchdelete = 'virtualSwitchDelete';
    const expectedNetwork = 'network';
    const expectedEnvironment = 'environment';
    const expectedFirewallruleadd = 'firewallRuleAdd';

    let before = item.before.data, after = item.change;
    if (item.action === expectedNetworkprotocols)
    {
        const old = {}, next = {};
        for (const version of [4, 6])
        {
            if (item.change[`ipv${version}Enabled`] !== undefined)
            {
                old[`IPv${version}`] = before[`ipv${version}`].enabled;
                next[`IPv${version}`] = item.change[`ipv${version}Enabled`];
            }
        }
        return [describe(old, translate), describe(next, translate)];
    }
    if (item.action === expectedVirtualswitchcreate)
    {
        return virtualSwitchCreationValues(item, translate);
    }
    if (item.action === expectedVirtualswitchdelete)
    {
        const ports = before.ports.map(port => `${port.managementOS ? translate('Management OS') : port.virtualMachine} · ${port.name}`).join('\n') || '—';
        return [`${translate('Name')}: ${before.switch.name}\n${translate('Switch type')}: ${translate(before.switch.kind)}\n${translate('Physical interface')}: ${before.switch.physicalInterface || '—'}\n${translate('Connected adapters')}:\n${ports}`, translate('Delete switch and its management OS adapters. The physical NIC is retained; connectivity may be interrupted.')];
    }
    if (item.action === expectedNetwork)
    {
        const expectedFamily = 'family';

        const configuration = before[item.change.family === 4 ? 'ipv4' : 'ipv6'];
        before = Object.fromEntries(Object.keys(item.change).map(key => [key, key === expectedFamily ? `IPv${item.change.family}` : configuration?.[key] ?? null]));
        after = { ...after, family: `IPv${item.change.family}` };
    }
    if (['virtualNicCreate', 'virtualNicVlan'].includes(item.action))
    {
        [before, after] = virtualNicValues(item, translate);
    }
    if (deletesResource(item))
    {
        after = translate('Delete');
    }
    if (item.action === expectedEnvironment && !item.change.delete)
    {
        before = before == null ? null : { value: before.value, expandable: before.expandable };
        after = { value: item.change.value, expandable: item.change.expandable };
    }
    if (['firewallProfile', 'firewallRuleEnabled'].includes(item.action))
    {
        before = { enabled: before?.enabled };
    }
    if (item.action === expectedFirewallruleadd)
    {
        after = item.change.rule;
    }
    return [describe(before, translate), describe(after, translate)];
}

/** 実際に対象を削除する変更を比較表示で区別します。
 * @returns 削除を明示した操作の場合はtrueです。
 */
function deletesResource(item)
{
    return ['virtualNicDelete', 'firewallRuleRemove'].includes(item.action) || item.change.delete;
}

/** 表示モードの変更前後を説明します。
 * @returns 現在モードと選択モードです。
 */
function displayPreview(item, translate)
{
    return [displayModeLabel(item.before.data.current, translate), displayModeLabel(item.change.mode, translate)];
}

/** 操作領域に応じた確認文を選びます。
 * @returns 利用者が確認する影響です。
 */
function impactMessage(previews)
{
    const display = 'display', power = 'power', updates = 'windowsUpdate', login = 'login';
    if (previews.some(item => item.query.area === login))
    {
        return 'I reviewed the account, physical access and login policies, including any selected login notice removal.';
    }
    if (previews.some(item => item.query.area === updates))
    {
        return 'I reviewed update policies and pending updates. Releasing this restriction may allow automatic updates again.';
    }
    if (previews.some(item => item.query.area === display))
    {
        return 'Display changes may interrupt the picture. I have checked the selected displays and modes.';
    }
    return previews.some(item => item.query.area === power) ? 'I checked the timers and power actions. These settings may put the host to sleep or shut it down.' : 'Settings may disconnect the selected hosts. I have checked the affected interfaces and access path.';
}

/** 仮想スイッチの作成内容と管理OSへの影響を表示する値に変換します。
 * @returns 未作成の状態と新しいスイッチの設定です。
 */
function virtualSwitchCreationValues(item, translate)
{
    const internalSwitch = 'Internal';
    const adapter = item.before.data.physicalInterfaces.find(value => value.id === item.change.physicalInterfaceId);
    return [translate('No virtual switch with this name'), `${translate('Name')}: ${item.change.name}\n${translate('Switch type')}: ${translate(item.change.kind)}\n${translate('Physical interface')}: ${adapter ? `${adapter.name} · ${adapter.description}` : '—'}\n${translate('Allow management OS sharing')}: ${translate((item.change.allowManagementOS ?? item.change.kind === internalSwitch) ? 'Enabled' : 'Disabled')}`];
}

/** hosts本文の差分を行ごとに表示します。
 * @returns 呼出し元が使用する値です。
 */
function hostsDiff(before, after, ctx)
{
    const oldLines = before.split(/\r\n|\r|\n/), newLines = after.split(/\r\n|\r|\n/);
    let prefix = 0, suffix = 0;
    while (prefix < Math.min(oldLines.length, newLines.length) && oldLines[prefix] === newLines[prefix])
    {
        prefix++;
    }
    while (suffix < Math.min(oldLines.length, newLines.length) - prefix && oldLines.at(-suffix - 1) === newLines.at(-suffix - 1))
    {
        suffix++;
    }
    return [oldLines, newLines].map((lines, index) =>
    {
        const root = node('div'), preview = node('pre', '', 'hm-text-diff'), from = Math.max(0, prefix - 2), to = Math.min(lines.length, lines.length - suffix + 2);
        if (from)
        {
            preview.append(node('span', `… ${from} ${ctx.t('lines')}`, 'hm-diff-context'));
        }
        for (let line = from; line < to; line++)
        {
            preview.append(node('span', lines[line] || ' ', (prefix) <= (line) && line < lines.length - suffix ? index ? 'hm-diff-added' : 'hm-diff-removed' : ''));
        }
        if (to < lines.length)
        {
            preview.append(node('span', `… ${lines.length - to} ${ctx.t('lines')}`, 'hm-diff-context'));
        }
        root.append(preview, details(ctx.t('Show full content'), node('pre', index ? after : before)));
        return root;
    });
}

/** 変更対象のネットワーク状態をホストごとに取得します。
 * @returns 現在の一覧、または画面が失効した場合はnullです。
 */
async function readNetworkInventoriesAsync(ctx, changes, current)
{
    const inventories = new Map();
    const networkHosts = [...new Map(changes.filter(item => ['network', 'networkProtocols'].includes(item.action)).map(item => [item.host.target.hostId, item.host])).values()];
    if (networkHosts.length)
    {
        const reader = createReader(ctx, networkHosts, () => ({ area: 'network' }), () =>
        {});
        try
        {
            await reader.load();
            if (ctx.signal.aborted || !current())
            {
                return null;
            }
            for (const [host, state] of reader.states)
            {
                if (!state.snapshot)
                {
                    throw new Error(state.error || ctx.t('Read failed. Check the selected host, its supported features and privileges.'));
                }
                inventories.set(host, state.snapshot);
            }
        }
        finally
        {
            reader.dispose();
        }
    }
    return inventories;
}

/** 変更前の状態を確認し、一件の実行要求を確定します。
 * @returns 確認対象の要求、または変更がない場合はnullです。
 */
async function preparePreviewAsync(ctx, item, inventories)
{
    const expectedVirtualswitchcreate2 = 'virtualSwitchCreate';
    const expectedVirtualswitchdelete2 = 'virtualSwitchDelete';
    const expectedNetwork2 = 'network';

    let before;
    if (['network', 'networkProtocols'].includes(item.action))
    {
        const key = item.host.target.hostId;
        const adapter = inventories.get(key).data.interfaces.find(adapter => adapter.id === item.query.resourceId);
        if (!adapter?.revision)
        {
            throw new Error(ctx.t('Read failed. Check the selected host, its supported features and privileges.'));
        }
        before = { revision: adapter.revision, data: { ...adapter, revision: null } };
    }
    else
    {
        before = item.snapshot ?? await ctx.read(item.host, item.query);
    }
    if (item.baseRevision && item.baseRevision !== before.revision)
    {
        throw new Error(ctx.t('Settings changed. Refresh before editing again.'));
    }
    if (item.patch)
    {
        item.change = resolveBulkPatch(item, before);
        if (!item.change)
        {
            return null;
        }
    }
    if (item.action === expectedVirtualswitchcreate2)
    {
        validateVirtualSwitch(item.change, before.data);
    }
    if (item.action === expectedVirtualswitchdelete2 && !before.data?.editable)
    {
        throw new Error(ctx.t(before.data?.problem || 'Device.VirtualSwitchUnavailable'));
    }
    if (item.action === expectedNetwork2)
    {
        validateNetwork(item.change, before.data);
    }
    const operationId = item.operationId ?? crypto.randomUUID();
    return { ...item, before, planned: { target: item.host.target, request: { operationId, hostId: item.host.target.hostId,
        instanceId: item.host.target.instanceId, action: item.action, read: item.query, expectedRevision: before.revision, change: item.change, acknowledgeDisconnect: false } } };
}

/** 対象ごとの変更予定と確認操作を表示します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function showPreview(ctx, proposed, refreshHistory, current = () => true, submitted)
{
    if (!proposed.length)
    {
        throw new Error(ctx.t('No changes'));
    }
    const changes = structuredClone(proposed);
    ctx.say(ctx.t('Loading…'));
    const unconfirmed = await ctx.api('unconfirmed-hosts');
    const needsAcknowledgement = changes.some(item => unconfirmed.includes(item.host.target.hostId));
    const previews = [];
    const inventories = await readNetworkInventoriesAsync(ctx, changes, current);
    if (inventories === null)
    {
        return;
    }
    for (const item of changes)
    {
        if (ctx.signal.aborted || !current())
        {
            return;
        }
        const preview = await preparePreviewAsync(ctx, item, inventories);
        if (preview)
        {
            previews.push(preview);
        }
    }
    if (ctx.signal.aborted || !current())
    {
        return;
    }
    if (!previews.length)
    {
        throw new Error(ctx.t('No changes'));
    }
    if ((256) < (previews.length))
    {
        throw new Error(ctx.t('Select at most 256 changes at a time.'));
    }
    ctx.say('');
    const dialog = ctx.dialog('Confirm changes');
    dialog.element?.classList.add('hm-preview-dialog');
    const summary = node('div', '', 'hm-summary');
    summary.append(badge(`${ctx.t('Affected hosts')}: ${new Set(previews.map(item => item.host.target.hostId)).size}`), badge(`${ctx.t('Changes')}: ${previews.length}`));
    dialog.root.append(summary, node('p', ctx.t('Review the before and after values for each target.'), 'hm-muted'));
    const layout = node('div', '', 'hm-preview-layout'), targets = node('div', '', 'hm-preview-targets'), detail = node('div');
    layout.append(targets, detail);
    dialog.root.append(layout);

    /** 対象を表示し、現在の操作位置へ配置します。 */
    function show(item, button)
    {
        const expectedHosts = 'hosts';

        detail.replaceChildren();
        for (const target of targets.children)
        {
            target.setAttribute('aria-pressed', String(target === button));
        }
        const card = node('section', '', 'hm-preview');
        card.append(node('h3', item.label ?? item.action));
        if (['network', 'networkProtocols'].includes(item.action))
        {
            card.append(badge(vlanLabel(item.before.data.vlanMode, item.before.data.vlanId, ctx.t), vlanTone(item.before.data.vlanMode)));
        }
        const columns = node('div', '', 'hm-comparison');
        const [beforeText, afterText] = previewValues(item, ctx.t);
        const contents = item.action === expectedHosts ? hostsDiff(item.before.data.content, item.change.content, ctx) : [node('pre', beforeText), node('pre', afterText)];
        for (const [index, label] of ['Before', 'After'].entries())
        {
            const column = node('div');
            column.append(node('h4', ctx.t(label)));
            if (['virtualNicCreate', 'virtualNicVlan', 'virtualNicDelete'].includes(item.action))
            {
                const expectedVirtualniccreate = 'virtualNicCreate';
                const expectedVirtualnicdelete = 'virtualNicDelete';

                const current = index === 0 && item.action !== expectedVirtualniccreate ? item.before.data : index === 1 && item.action !== expectedVirtualnicdelete ? { vlanId: item.change.vlanId, vlanMode: item.change.vlanId == null ? 'Untagged' : 'Access' } : null;
                if (current)
                {
                    column.append(badge(vlanLabel(current.vlanMode, current.vlanId, ctx.t), vlanTone(current.vlanMode)));
                }
            }
            column.append(contents[index]);
            columns.append(column);
        }
        card.append(columns, details(ctx.t('Technical details'), node('small', `${item.host.target.hostId} · ${item.host.target.clusterId} / ${item.host.target.peerId} · ${item.query.resourceId ?? item.query.area}`)));
        detail.append(card);
    }

    for (const item of previews)
    {
        const button = ctx.button(item.host.target.hostId, () => show(item, button));
        button.append(node('small', item.label ?? ctx.t(item.query.area)));
        targets.append(button);
    }
    show(previews[0], targets.firstElementChild);
    const risk = previews.some(item => ['network', 'firewall', 'display', 'power', 'windowsUpdate', 'login'].includes(item.query.area));
    const acknowledgement = checkbox(ctx.t(impactMessage(previews)));
    const footer = dialog.footer ?? dialog.root;
    footer.classList.add('hm-confirm-footer');
    if (risk)
    {
        footer.append(acknowledgement.root);
    }
    const previous = checkbox(ctx.t('I checked the previous unconfirmed operations and current settings. Start this as a new change; preserve the previous unknown results.'));
    if (needsAcknowledgement)
    {
        footer.append(previous.root);
    }
    const actions = node('div', '', 'hm-actions');
    const confirm = ctx.button('Apply confirmed changes', async () =>
    {
        if ((risk && !acknowledgement.control.checked) || (needsAcknowledgement && !previous.control.checked))
        {
            return;
        }
        const batch = { operationId: crypto.randomUUID(), acknowledgeUnconfirmed: needsAcknowledgement && previous.control.checked, changes: previews.map(item => ({ ...item.planned,
            request: { ...item.planned.request, acknowledgeDisconnect: risk } })) };

        // Commit the original ID before dispatch. Lost responses are recovered by reading, never by reposting.
        await window.contentsOperationStore.write(JSON.stringify({ operationId: batch.operationId, kind: 'HostManager', request: batch,
            readPath: `/client/host-manager/batches/${batch.operationId}`, recoverPath: null, createdAt: new Date().toISOString() }), 'create');
        dialog.close();
        submitted?.();
        ctx.submitted?.(batch.operationId);
        try
        {
            await ctx.api('batches', batch);
        }
        finally
        {
            await refreshHistory();
        }
    }, 'primary');
    confirm.disabled = risk || needsAcknowledgement;

    /** 適用に必要な条件と入力を確認します。 */
    const check = () =>
    {
        confirm.disabled = (risk && !acknowledgement.control.checked) || (needsAcknowledgement && !previous.control.checked);
    };

    acknowledgement.control.onchange = previous.control.onchange = check;
    actions.append(ctx.button('Back to editing', dialog.close), confirm);
    footer.append(actions);
}

/** 各変更の実行結果と確認できた手順を履歴へ追加します。 */
function appendHistoryRows(list, state, ctx)
{
    for (let index = 0; index < state.request.changes.length; index++)
    {
        const expectedDeviceDispatching = 'Device.Dispatching';

        const change = state.request.changes[index], result = state.results[index];
        const status = state.running && result?.reason === expectedDeviceDispatching ? 'Running'
            : result ? ({ succeeded: 'Succeeded', failed: 'Failed', notExecuted: 'Not executed', unknown: 'Unknown' }[result.outcome] ?? result.outcome) : 'Queued';
        const extra = node('div');
        if (result?.reason)
        {
            extra.append(details(ctx.t('Details'), node('small', ctx.t(result.reason))));
        }
        if (result?.completedSteps?.length)
        {
            extra.append(details(ctx.t('Completed steps'), node('pre', result.completedSteps.map(step => ctx.t(step)).join('\n'))));
        }
        extra.append(details(ctx.t('Details'), node('code', change.request.operationId)));
        const area = { network: 'NetworkIF', hosts: 'Hosts', firewall: 'Firewall', environment: 'PATH', display: 'Display', power: 'Power', windowsUpdate: 'Windows Update', login: 'Login', wakeOnLan: 'wake.title' }[change.request.read.area] ?? change.request.read.area;
        list.add([change.target.hostId, ctx.t(area), node('span', ctx.t(status), `hm-result ${result?.outcome ?? ''}`), extra]);
    }
}

/** 実際に記録された操作と結果を表示します。 */
export function showHistory(root, ctx, states, refresh)
{
    root.replaceChildren();
    const feedback = node('p', '', 'hm-message');
    feedback.dataset.hmFeedback = '';
    feedback.setAttribute('role', 'status');
    root.append(feedback);
    const tools = node('div', '', 'hm-toolbar');
    tools.append(badge(`${states.length} ${ctx.t('operations')}`), action(ctx, 'Refresh results', 'refresh', refresh));
    root.append(tools);
    if (!states.length)
    {
        root.append(empty(ctx.t('No active operations.'), ctx.t('Confirmed changes and their results appear here.'), 'history'));
    }
    for (const state of states)
    {
        const body = node('div'), list = table(['Host', 'Operation', 'Result', 'Details'].map(ctx.t));
        appendHistoryRows(list, state, ctx);
        body.append(list.root);
        if (!state.running && state.results.some(result => result?.outcome === 'unknown'))
        {
            body.append(node('p', ctx.t('A pending change is unconfirmed. Query its original result before starting another change on that host.')),
                ctx.button('Query original results', async () =>
                {
                    await ctx.api(`batches/${state.request.operationId}/reconcile`, {});
                    await refresh();
                }));
        }
        const outcome = state.running ? 'Running' : state.results.some(result => result?.outcome === 'unknown') ? 'Unknown' : state.results.every(result => result?.outcome === 'succeeded') ? 'Succeeded' : 'Check results';
        const heading = `${new Date(state.createdAt).toLocaleString()} · ${state.request.changes.length} ${ctx.t('Changes')} · ${ctx.t(outcome)}`;
        const item = details(heading, body);
        item.classList.add('hm-history-entry');
        item.open = state.running || state.results.some(result => result?.outcome !== 'succeeded');
        root.append(item);
    }
}
