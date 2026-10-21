import test from 'node:test';
import assert from 'node:assert/strict';
import { DomNode, createDocument, descendants } from './host-manager-dom.mjs';
import { mount, unmount, textKeys } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/index.js';

/** 経過時間に応じて状態と期限を更新します。
 * @returns 呼出し元が使用する値です。
 */
const tick = () => new Promise(resolve =>
{
    setImmediate(resolve);
});

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
async function fixtureAsync(t)
{
    const before = Object.fromEntries(['document', 'Node', 'window', 'fetch'].map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
    globalThis.document = createDocument();
    globalThis.Node = DomNode;
    globalThis.window = { contentsOperationStore: {
        /** 指定された値を所有する保存先へ書き込みます。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async write(value)
        {
            saved.push(JSON.parse(value));
        } } };
    const calls = [], saved = [], batches = [], content = { 'host-a': '# keep\r\n127.0.0.1  localhost\r\n', 'host-b': '# other\n127.0.0.1 localhost\n' };
    const hosts = ['host-a', 'host-b', 'linux-host'].map((id, index) => ({ target: { hostId: id, clusterId: 'cluster', peerId: id, instanceId: 'instance-' + id }, capabilities: { operatingSystem: index === 2 ? 'Linux' : 'Windows', elevated: true, areas: index === 2 ? ['hosts'] : ['network', 'hosts', 'firewall', 'environment'], users: [{ name: 'user-' + id, sid: 'sid-' + id }] } }));
    const configuration = { enabled: true, dhcp: true, automaticDns: true, dns: ['192.0.2.1'], gateways: ['192.0.2.1'], metric: 10, automaticMetric: true, addresses: [{ address: '192.0.2.10', prefixLength: 24, automatic: true }] };
    const network = { interfaces: [0, 1].map(index => ({ id: 'nic-' + index, revision: 'nic-rev-' + index, name: 'Ethernet ' + index, description: 'Adapter', status: 'Up', vlanMode: 'Untagged', vlanId: null, ipv4: configuration, ipv6: null })), virtualNics: [], switches: [] };
    const environments = Object.fromEntries(hosts.map(host => [host.target.hostId, [{ name: 'Path', value: 'C:\\one;%ROOT%;C:\\two', expandable: true }, { name: 'TEMP', value: 'C:\\Temp', expandable: true }]]));
    const firewalls = Object.fromEntries(hosts.map(host => [host.target.hostId, { profiles: ['Domain', 'Private', 'Public'].map(name => ({ name, enabled: true })), rules: [] } ]));
    const switches = { switches: [{ switch: { id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', name: 'Internal switch', kind: 'Internal', physicalInterface: null }, physicalInterfaceIds: [], allowManagementOS: true, ports: [{ id: 'port', name: 'Host adapter', managementOS: true, virtualMachine: null }], editable: true, problem: null }], physicalInterfaces: [{ id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', name: 'Free NIC', description: 'Physical adapter', inUse: false }, { id: 'cccccccc-cccc-cccc-cccc-cccccccccccc', name: 'Used NIC', description: 'Already bound', inUse: true }] };
    globalThis.fetch = async (url, options = {}) =>
    {
        const body = options.body ? JSON.parse(options.body) : undefined, path = url.replace('/client/host-manager/', '');
        calls.push({ path, body });
        let result;
        switch (true)
        {
            case Boolean(path === 'hosts'):
                {
                    result = hosts;
                }
                break;
            case Boolean(path === 'unconfirmed-hosts'):
                {
                    result = [];
                }
                break;
            case Boolean(path === 'batches' && !body):
                {
                    result = batches;
                }
                break;
            case Boolean(path === 'batches'):
                {
                    batches.push({ request: body, createdAt: new Date().toISOString(), running: false, results: body.changes.map(() => ({ outcome: 'succeeded' })) });
                    for (const change of body.changes)
                    {
                        if (change.request.action === 'hosts')
                        {
                            content[change.target.hostId] = change.request.change.content;
                        }
                    }
                    result = batches.at(-1);
                }
                break;
            case Boolean(path === 'read'):
                {
                    const { query, target } = body;
                    let data;
                    if (query.area === 'network')
                    {
                        data = network;
                    }
                    if (query.area === 'network' && query.resourceId === 'switches')
                    {
                        data = switches;
                    }
                    if (query.area === 'network' && query.resourceId?.startsWith('switch/'))
                    {
                        data = switches.switches.find(item => item.switch.id === query.resourceId.slice(7)) ?? null;
                    }
                    if (query.area === 'hosts')
                    {
                        data = { content: content[target.hostId] ?? '# linux\n' };
                    }
                    if (query.area === 'environment')
                    {
                        data = query.resourceId ? environments[target.hostId].find(item => item.name.toUpperCase() === query.resourceId.toUpperCase()) ?? null : { scope: query.scope, userSid: query.userSid, variables: environments[target.hostId] };
                    }
                    if (query.area === 'firewall')
                    {
                        const state = firewalls[target.hostId];
                        data = { profiles: state.profiles, rules: state.rules.map(item => item.rule), nextOffset: null };
                        if (query.resourceId === 'bulk')
                        {
                            data = { rules: state.rules.filter(item => JSON.parse(query.filter).includes(item.rule.id)) };
                        }
                        if (query.resourceId?.startsWith('rule/'))
                        {
                            data = state.rules.find(item => item.rule.id === query.resourceId.slice(5))?.rule ?? null;
                        }
                        if (query.resourceId?.startsWith('profile/'))
                        {
                            data = state.profiles.find(item => item.name === query.resourceId.slice(8));
                        }
                    }
                    result = { revision: 'revision', data };
                }
                break;
            default:
            {
                throw new Error('Unexpected API ' + path);
            }
        }
        return { ok: true, status: 200,

            json: async () => structuredClone(result) };
    };
    const root = new DomNode('main');
    document.body.append(root);
    t.after(async () =>
    {
        await unmount(root);
        for (const [name, descriptor] of Object.entries(before))
        {
            if (descriptor)
            {
                Object.defineProperty(globalThis, name, descriptor);
            }
            else
            {
                delete globalThis[name];
            }
        }
    });
    await mount(root, Object.fromEntries(textKeys().map(key => [key, key])));
    await tick();

    /** 対象の表示状態を試験用DOMから読み取ります。
     * @returns 呼出し元が使用する値です。
     */
    const visible = element => !element.hidden && (!element.parentElement || visible(element.parentElement));

    /** 試験用の一覧から条件に一致する対象を探します。
     * @returns 呼出し元が使用する値です。
     */
    const find = (tag, name, parent = root) => descendants(parent).find(element => element.tag === tag && visible(element) && (element.getAttribute('aria-label') === name || element.textContent === name));

    /** 操作の実行と処理中表示を持つボタンを構成します。
     * @returns 呼出し元が使用する値です。
     */
    const button = (name, parent = root) =>
    {
        const element = find('button', name, parent);
        assert.ok(element, 'button ' + name);
        return element;
    };

    /** クリック対象を確認して選択または操作を行います。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    const clickAsync = async (name, parent = root) =>
    {
        await button(name, parent).click();
        await tick();
    };

    /** 指定した値を試験対象の入力へ反映します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    const pickAsync = async id =>
    {
        const element = find('input', id);
        assert.ok(element);
        element.checked = !element.checked;
        await element.fire('change');
        await tick();
    };

    /** 指定された試験状態を更新します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    const setAsync = async (tag, label, value, parent = root) =>
    {
        const element = find(tag, label, parent);
        assert.ok(element, label);
        element.value = value;
        await element.fire(tag === 'select' ? 'change' : 'input');
        await tick();
        return element;
    };

    return { root, calls, saved, batches, find, button, click: clickAsync, pick: pickAsync, set: setAsync, switches, network, content, environments, firewalls,

        dialogs: () => descendants(root).filter(element => element.tag === 'dialog' && element.open) };
}

test('multi-host Hosts defaults to other and applies only explicitly changed entries to fresh files', async t =>
{
    const f = await fixtureAsync(t);
    f.content['host-a'] = '# A\n192.0.2.1 app\n192.0.2.2 keep\n';
    f.content['host-b'] = '# B\n# 192.0.2.1 app\n';
    await f.pick('host-a');
    await f.pick('host-b');
    await f.click('Hosts');
    const toggle = f.find('div', '192.0.2.1 · app');
    assert.equal(f.button('other', toggle).getAttribute('aria-checked'), 'true');
    assert.equal(f.button('Preview changes').disabled, true);
    await f.click('enable', toggle);
    f.content['host-b'] += '192.0.2.9 external\n';
    await f.click('Preview changes');
    await f.click('Apply confirmed changes');
    const changes = f.batches[0].request.changes;
    assert.equal(changes.length, 1);
    assert.equal(changes[0].target.hostId, 'host-b');
    assert.equal(changes[0].request.change.content, '# B\n192.0.2.1\tapp\n192.0.2.9 external\n');
    assert.match(f.content['host-a'], /192.0.2.2 keep/);
});
test('other cancels a staged Hosts change and missing definitions remain absent', async t =>
{
    const f = await fixtureAsync(t);
    f.content['host-a'] = '192.0.2.1 app\n';
    f.content['host-b'] = '# keep\n';
    await f.pick('host-a');
    await f.pick('host-b');
    await f.click('Hosts');
    const toggle = f.find('div', '192.0.2.1 · app');
    await f.click('disable', toggle);
    assert.equal(f.button('Preview changes').disabled, false);
    await f.click('other', toggle);
    assert.equal(f.button('Preview changes').disabled, true);
    assert.equal(f.batches.length, 0);
});
test('multi-host PATH changes one entry and keeps different host values and registry types', async t =>
{
    const f = await fixtureAsync(t);
    f.environments['host-b'][0] = { name: 'Path', value: 'D:\\private;%ROOT%', expandable: false };
    await f.pick('host-a');
    await f.pick('host-b');
    await f.click('PATH');
    await f.click('enable', f.find('div', 'C:\\one'));
    await f.click('Preview changes');
    await f.click('Apply confirmed changes');
    const changes = f.batches[0].request.changes;
    assert.equal(changes.length, 1);
    assert.equal(changes[0].target.hostId, 'host-b');
    assert.equal(changes[0].request.change.value, 'D:\\private;%ROOT%;C:\\one');
    assert.equal(changes[0].request.change.expandable, false);
});
test('keeping a PATH draft after cancelled scope change leaves Machine selected and permits a later retry', async t =>
{
    const f = await fixtureAsync(t);
    f.environments['host-b'][0].value = 'D:\\private';
    await f.pick('host-a');
    await f.pick('host-b');
    await f.click('PATH');
    await f.click('enable', f.find('div', 'C:\\one'));
    await f.click('User');
    await f.click('Keep editing');
    assert.equal(f.button('Machine').getAttribute('aria-pressed'), 'true');
    await f.click('User');
    assert.equal(f.dialogs().length, 1);
    await f.click('Discard changes');
    assert.equal(f.button('User').getAttribute('aria-pressed'), 'true');
});
test('NetworkIF direct IPv4 switch changes only that protocol and DNS-none allows gateway-only configuration', async t =>
{
    const f = await fixtureAsync(t);
    f.network.interfaces[0].ipv4 = { ...f.network.interfaces[0].ipv4, dhcp: false, dns: [] };
    await f.pick('host-a');
    await f.button('IPv4 · Ethernet 0 · host-a').click();
    await tick();
    assert.equal(f.dialogs().length, 0);
    assert.equal(f.button('Apply').disabled, false);
    await f.click('Clear all pending changes');
    await f.click('Edit');
    await f.set('select', 'DNS configuration', 'none');
    const replace = f.find('label', 'Replace default gateways');
    const check = descendants(replace).find(item => item.tag === 'input');
    check.checked = true;
    await check.fire('change');
    await f.set('textarea', 'Default gateways', '192.0.2.1');
    await f.click('Preview changes');
    const ack = descendants(f.dialogs().at(-1)).find(item => item.tag === 'input' && item.type === 'checkbox');
    ack.checked = true;
    await ack.fire('change');
    await f.click('Apply confirmed changes');
    assert.deepEqual(f.batches[0].request.changes[0].request.change, { family: 4, automaticDns: false, dns: [], gateways: ['192.0.2.1'] });
});
test('NetworkIF stages both families on multiple hosts and sends one operation per changed NIC after Apply', async t =>
{
    const f = await fixtureAsync(t);
    f.network.interfaces[0].ipv6 = { ...f.network.interfaces[0].ipv4 };
    await f.pick('host-a');
    await f.pick('host-b');
    await f.click('IPv4 · Ethernet 0 · host-a');
    await f.click('IPv6 · Ethernet 0 · host-a');
    await f.click('IPv6 · Ethernet 0 · host-b');
    assert.equal(f.dialogs().length, 0);
    assert.equal(f.batches.length, 0);
    assert.equal(f.button('IPv6 · Ethernet 0 · host-a').dataset.pending, 'true');
    await f.set('input', 'Search interfaces', 'nothing');
    assert.equal(f.button('Apply').disabled, false);
    await f.click('Apply');
    const ack = descendants(f.dialogs().at(-1)).find(e => e.tag === 'input' && e.type === 'checkbox');
    ack.checked = true;
    await ack.fire('change');
    await f.click('Apply confirmed changes');
    const changes = f.batches[0].request.changes;
    assert.equal(changes.length, 2);
    assert.deepEqual(changes[0].request.change, { ipv4Enabled: false, ipv6Enabled: false });
    assert.equal(changes[0].request.action, 'networkProtocols');
    assert.deepEqual(changes[1].request.change, { ipv6Enabled: false });
});
test('NetworkIF restores staged toggles, retains them across tabs, and discards explicitly on host changes', async t =>
{
    const f = await fixtureAsync(t);
    await f.pick('host-a');
    await f.click('IPv4 · Ethernet 0 · host-a');
    await f.click('IPv4 · Ethernet 0 · host-a');
    assert.equal(f.button('Apply').disabled, true);
    await f.click('IPv4 · Ethernet 0 · host-a');
    await f.click('Hosts');
    await f.click('NetworkIF');
    assert.equal(f.button('IPv4 · Ethernet 0 · host-a').getAttribute('aria-checked'), 'false');
    await f.pick('host-b');
    assert.equal(f.dialogs().length, 1);
    await f.click('Keep editing');
    await f.click('Clear all pending changes');
    assert.equal(f.button('Apply').disabled, true);
    assert.equal(f.button('IPv4 · Ethernet 0 · host-a').dataset.pending, 'false');
    assert.equal(f.button('IPv4 · Ethernet 0 · host-a').getAttribute('aria-checked'), 'true');
});
test('NetworkIF rejects stale staged settings before dispatch', async t =>
{
    const f = await fixtureAsync(t);
    await f.pick('host-a');
    await f.click('IPv4 · Ethernet 0 · host-a');
    f.network.interfaces[0].revision = 'changed-elsewhere';
    await f.click('Apply');
    assert.equal(f.dialogs().length, 0);
    assert.equal(f.batches.length, 0);
    assert.match(f.root.textContent, /Settings changed/);
});

/** 比較と変更の検証に使うファイアウォール規則を作成します。
 * @returns 呼出し元が使用する値です。
 */
const firewallRule = (id, enabled = false) => ({ id, name: 'Fixture app', direction: 'Inbound', action: 'Allow', profile: 'Private', protocol: 'TCP', localPorts: ['1234'], remotePorts: ['Any'], localAddresses: ['Any'], remoteAddresses: ['Any'], program: 'Any', enabled, editable: true, revision: 'revision', detailsHash: 'detail' });

test('Firewall three-state selection copies a supported rule to a missing host and retains other profiles', async t =>
{
    const f = await fixtureAsync(t);
    f.firewalls['host-a'].rules = [{ rule: firewallRule('first'), cloneable: true }];
    await f.pick('host-a');
    await f.pick('host-b');
    await f.click('Firewall');
    await tick();
    const toggle = f.find('div', 'Fixture app · TCP · 1234');
    assert.ok(toggle);
    assert.equal(f.button('other', toggle).getAttribute('aria-checked'), 'true');
    await f.click('enable', toggle);
    await f.click('Preview changes');
    assert.equal(f.button('Apply confirmed changes').disabled, true);
    const ack = descendants(f.dialogs().at(-1)).find(item => item.tag === 'input' && item.type === 'checkbox');
    ack.checked = true;
    await ack.fire('change');
    await f.click('Apply confirmed changes');
    const changes = f.batches[0].request.changes;
    assert.equal(changes.length, 2);
    assert.equal(changes[0].request.action, 'firewallRuleEnabled');
    assert.equal(changes[1].request.action, 'firewallRuleAdd');
    assert.equal(changes[1].request.change.rule.enabled, true);
    assert.ok(changes.every(item => item.request.action !== 'firewallProfile'));
});
test('Firewall unsupported conditions on any host disable the shared toggle', async t =>
{
    const f = await fixtureAsync(t);
    f.firewalls['host-a'].rules = [{ rule: firewallRule('first'), cloneable: true }];
    f.firewalls['host-b'].rules = [{ rule: firewallRule('second', true), cloneable: false }];
    await f.pick('host-a');
    await f.pick('host-b');
    await f.click('Firewall');
    await tick();
    const toggle = f.find('div', 'Fixture app · TCP · 1234');
    assert.ok(toggle);
    assert.equal(f.button('enable', toggle).disabled, true);
    assert.equal(f.button('disable', toggle).disabled, true);
    assert.equal(f.button('Preview changes').disabled, true);
});
test('a rule added before preview is toggled instead of copied a second time', async t =>
{
    const f = await fixtureAsync(t);
    f.firewalls['host-a'].rules = [{ rule: firewallRule('first', true), cloneable: true }];
    await f.pick('host-a');
    await f.pick('host-b');
    await f.click('Firewall');
    await tick();
    await f.click('disable', f.find('div', 'Fixture app · TCP · 1234'));
    f.firewalls['host-b'].rules = [{ rule: firewallRule('new', true), cloneable: true }];
    await f.click('Preview changes');
    const ack = descendants(f.dialogs().at(-1)).find(item => item.tag === 'input' && item.type === 'checkbox');
    ack.checked = true;
    await ack.fire('change');
    await f.click('Apply confirmed changes');
    assert.ok(f.batches[0].request.changes.every(item => item.request.action === 'firewallRuleEnabled'));
});
test('host selection reads automatically, filters unsupported OS targets and retains tab state', async t =>
{
    const f = await fixtureAsync(t);
    assert.equal(f.find('input', 'linux-host').disabled, true);
    await f.pick('host-a');
    assert.equal(f.calls.filter(call => call.path === 'read' && call.body.query.area === 'network').length, 1);
    await f.click('Hosts');
    assert.equal(f.find('input', 'linux-host').disabled, false);
    await f.click('Text editor');
    const text = f.find('textarea', 'Replace hosts content');
    assert.equal(text.value, '# keep\r\n127.0.0.1  localhost\r\n');
    await f.set('textarea', 'Replace hosts content', text.value + '192.0.2.20 app.local\r\n');
    await f.click('NetworkIF');
    await f.click('Hosts');
    assert.match(f.find('textarea', 'Replace hosts content').value, /app.local/);
    assert.equal(f.calls.filter(call => call.path === 'read' && call.body.query.area === 'hosts').length, 1);
    await f.pick('host-b');
    assert.equal(f.dialogs().length, 1);
    await f.click('Keep editing');
    assert.equal(f.find('input', 'host-b').checked, false);
    assert.match(f.find('textarea', 'Replace hosts content').value, /app.local/);
});
test('hosts UI and text editing share one draft and apply only after explicit confirmation', async t =>
{
    const f = await fixtureAsync(t);
    await f.pick('host-a');
    await f.click('Hosts');
    await f.click('Add hosts entry');
    await f.set('input', 'IP address', '2001:db8::20');
    await f.set('input', 'Host names and aliases', 'app.local alias.local');
    await f.set('input', 'Comment', 'example');
    await f.click('Use entry');
    await f.click('Text editor');
    const value = f.find('textarea', 'Replace hosts content').value;
    assert.equal(value, '# keep\r\n127.0.0.1  localhost\r\n2001:db8::20\tapp.local alias.local\t# example\r\n');
    await f.click('Preview changes');
    assert.equal(f.batches.length, 0);
    await f.click('Back to editing');
    assert.equal(f.find('textarea', 'Replace hosts content').value, value);
    await f.click('Preview changes');
    await f.click('Apply confirmed changes');
    assert.equal(f.batches.length, 1);
    assert.equal(f.saved[0].operationId, f.batches[0].request.operationId);
    assert.equal(f.batches[0].request.changes[0].request.change.content, value);
});
test('DNS bulk editing preserves pinned NICs and sends no unrelated settings', async t =>
{
    const f = await fixtureAsync(t);
    await f.pick('host-a');
    await f.click('Select interfaces');
    await f.click('Set DNS / gateway');
    await f.set('select', 'DNS configuration', 'false');
    await f.set('textarea', 'DNS servers (in priority order)', '192.0.2.53');
    await f.click('Preview changes');
    assert.equal(f.button('Apply confirmed changes').disabled, true);
    assert.equal(f.batches.length, 0);
    const acknowledgement = descendants(f.dialogs().at(-1)).find(element => element.tag === 'input' && element.type === 'checkbox');
    acknowledgement.checked = true;
    await acknowledgement.fire('change');
    await f.click('Apply confirmed changes');
    const changes = f.batches[0].request.changes;
    assert.deepEqual(changes.map(item => item.request.read.resourceId), ['nic-0', 'nic-1']);
    for (const change of changes)
    {
        assert.deepEqual(change.request.change, { family: 4, automaticDns: false, dns: ['192.0.2.53'] });
    }
});
test('PATH entry reordering keeps variable references and the selected Machine scope', async t =>
{
    const f = await fixtureAsync(t);
    await f.pick('host-a');
    await f.click('PATH');
    await f.click('Path');
    assert.equal(f.find('input', 'Variable name').readOnly, true);
    await f.click('Move up 2');
    await f.click('Preview changes');
    await f.click('Apply confirmed changes');
    const request = f.batches[0].request.changes[0].request;
    assert.equal(request.read.scope, 'Machine');
    assert.equal(request.read.resourceId, 'Path');
    assert.equal(request.change.value, '%ROOT%;C:\\one;C:\\two');
    assert.equal(request.change.expandable, true);
});
test('User environment reads wait for explicit per-host profiles and preserve their SIDs', async t =>
{
    const f = await fixtureAsync(t);
    await f.pick('host-a');
    await f.pick('host-b');
    await f.click('PATH');
    await f.click('User');
    assert.equal(f.calls.some(call => call.path === 'read' && call.body.query.scope === 'User'), false);
    await f.click('Choose users');
    await f.set('select', 'host-a', 'sid-host-a');
    await f.set('select', 'host-b', 'sid-host-b');
    await f.click('Use selected users');
    const reads = f.calls.filter(call => call.path === 'read' && call.body.query.scope === 'User');
    assert.equal(reads.length, 2);
    for (const read of reads)
    {
        assert.equal(read.body.query.userSid, 'sid-' + read.body.target.hostId);
    }
});
test('closing an editor while its preview is reading prevents a late confirmation dialog', async t =>
{
    const f = await fixtureAsync(t);
    await f.pick('host-a');
    await f.click('Select interfaces');
    await f.click('Set DNS / gateway');
    await f.set('select', 'DNS configuration', 'false');
    await f.set('textarea', 'DNS servers (in priority order)', '192.0.2.53');
    const fetch = globalThis.fetch;
    let finish;
    globalThis.fetch = (url, options) => url.endsWith('/read') ? new Promise(resolve =>
    {
        finish = async () => resolve(await fetch(url, options));
    }) : fetch(url, options);
    const pending = f.button('Preview changes').click();
    await tick();
    assert.equal(typeof finish, 'function');
    await f.click('Close', f.dialogs().at(-1));
    await finish();
    await pending;
    await tick();
    assert.equal(f.dialogs().length, 0);
    assert.equal(f.batches.length, 0);
});
test('discovery refresh locks the target selection until the new host identities arrive', async t =>
{
    const f = await fixtureAsync(t);
    await f.pick('host-a');
    const fetch = globalThis.fetch;
    let finish;
    globalThis.fetch = (url, options) => url.endsWith('/hosts') ? new Promise(resolve =>
    {
        finish = async () => resolve(await fetch(url, options));
    }) : fetch(url, options);
    const pending = f.button('Refresh hosts').click();
    await tick();
    assert.equal(f.find('input', 'host-b').disabled, true);
    assert.equal(f.button('Hosts').disabled, true);
    await finish();
    await pending;
    await tick();
    assert.equal(f.find('input', 'host-b').disabled, false);
    assert.equal(f.find('input', 'host-a').checked, true);
});

test('virtual switch creation loads on demand and pins the External adapter and sharing choice', async t =>
{
    const f = await fixtureAsync(t);
    await f.pick('host-a');
    assert.equal(f.calls.some(call => call.body?.query?.resourceId === 'switches'), false);
    await f.click('Virtual switches');
    await f.click('Create virtual switch');
    await f.set('input', 'Name', 'External switch');
    await f.set('select', 'Switch type', 'External');
    const physical = f.find('select', 'Physical interface');
    assert.equal(physical.value, 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');
    assert.equal(physical.children.length, 1);
    await f.click('Preview changes');
    assert.equal(f.batches.length, 0);
    assert.equal(f.button('Apply confirmed changes').disabled, true);
    assert.match(f.dialogs().at(-1).textContent, /Free NIC/);
    const acknowledge = descendants(f.dialogs().at(-1)).find(item => item.tag === 'input' && item.type === 'checkbox');
    acknowledge.checked = true;
    await acknowledge.fire('change');
    await f.click('Apply confirmed changes');
    const request = f.batches[0].request.changes[0].request;
    assert.equal(request.action, 'virtualSwitchCreate');
    assert.equal(request.read.resourceId, 'switches');
    assert.deepEqual(request.change, { name: 'External switch', kind: 'External', physicalInterfaceId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', allowManagementOS: true });
});
test('switching to Private clears External-only fields and deletion previews affected management adapters', async t =>
{
    const f = await fixtureAsync(t);
    await f.pick('host-a');
    await f.click('Virtual switches');
    await f.click('Create virtual switch');
    await f.set('input', 'Name', 'Private switch');
    await f.set('select', 'Switch type', 'External');
    await f.set('select', 'Switch type', 'Private');
    await f.click('Preview changes');
    assert.match(f.dialogs().at(-1).textContent, /Private/);
    await f.click('Back to editing');
    await f.click('Cancel');
    await f.click('Delete');
    assert.match(f.dialogs().at(-1).textContent, /Host adapter/);
    assert.match(f.dialogs().at(-1).textContent, /physical NIC is retained/);
    await f.click('Back to editing');
    assert.equal(f.batches.length, 0);
});
test('new guest connections between listing and preview prevent switch deletion', async t =>
{
    const f = await fixtureAsync(t);
    await f.pick('host-a');
    await f.click('Virtual switches');
    f.switches.switches[0].editable = false;
    f.switches.switches[0].problem = 'Device.VirtualSwitchInUse';
    await f.click('Delete');
    assert.equal(f.dialogs().length, 0);
    assert.match(f.root.textContent, /Device.VirtualSwitchInUse/);
    assert.equal(f.batches.length, 0);
});
test('NetworkIF VLAN chips distinguish Access, Trunk and Untagged while preserving mode text', async t =>
{
    const f = await fixtureAsync(t);
    f.network.interfaces[0].vlanMode = 'Access';
    f.network.interfaces[0].vlanId = 120;
    f.network.interfaces[1].vlanMode = 'Trunk';
    f.network.interfaces.push({ ...f.network.interfaces[0], id: 'nic-2', name: 'Ethernet 2', vlanMode: 'Untagged', vlanId: null });
    await f.pick('host-a');
    const chips = descendants(f.root).filter(item => item.className.includes('hm-badge'));
    assert.ok(chips.some(item => item.textContent === 'Access · VLAN 120' && item.className.includes('vlan-access')));
    assert.ok(chips.some(item => item.textContent === 'Trunk' && item.className.includes('vlan-trunk')));
    assert.ok(chips.some(item => item.textContent === 'Untagged' && item.className.includes('vlan-untagged')));
});
