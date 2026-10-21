import test from 'node:test';
import assert from 'node:assert/strict';
import { aggregateHosts, patchHosts } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/hosts-bulk-model.js';
import { aggregatePaths, patchPaths, aggregateVariables } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/environment-bulk-model.js';
import { aggregateFirewall, firewallChanges } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/firewall-bulk-model.js';
import { aggregateState } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/tri-state.js';

test('three states require every host to define and agree on a setting', () =>
{
    assert.equal(aggregateState([true, true]), 'enable');
    assert.equal(aggregateState([false, false]), 'disable');
    assert.equal(aggregateState([true, false]), 'other');
    assert.equal(aggregateState([false, null]), 'other');
    assert.equal(aggregateState([]), 'other');
});
test('hosts aggregate normalized addresses and alias sets without treating missing rows as disabled', () =>
{
    const groups = aggregateHosts(['2001:db8::1 app alias # first\r\n# 192.0.2.1 off\r\n', '2001:0db8:0:0:0:0:0:1 ALIAS APP\n']);
    assert.equal(groups.length, 2);
    assert.equal(groups[0].state, 'enable');
    assert.equal(groups[1].state, 'other');
    assert.deepEqual(groups[1].states, ['disable', 'missing']);
});
test('hosts patches preserve other rows, comments, formatting and a fresh external change', () =>
{
    const content = '# local comment\r\n# 192.0.2.1\tapp # per-host comment\r\n192.0.2.2    keep\r\n192.0.2.9 external\r\n';
    const entries = aggregateHosts([content, '192.0.2.1 app\n192.0.2.2 keep\n']);
    const result = patchHosts(content, [{ entry: entries.find(item => item.entry.names === 'app').entry, state: 'enable' }, { entry: entries.find(item => item.entry.names === 'keep').entry, state: 'other' }]);
    assert.equal(result, '# local comment\r\n192.0.2.1\tapp\t# per-host comment\r\n192.0.2.2    keep\r\n192.0.2.9 external\r\n');
    assert.equal(patchHosts(content, entries.map(item => ({ entry: item.entry, state: 'other' }))), content);
});
test('explicitly disabling creates a commented definition on missing hosts and handles duplicate definitions', () =>
{
    const entry = { address: '192.0.2.1', names: 'app', comment: 'common', enabled: true };
    assert.equal(patchHosts('# retain\n', [{ entry, state: 'disable' }]), '# retain\n# 192.0.2.1\tapp\t# common\n');
    assert.equal(aggregateHosts(['192.0.2.1 app\n# 192.0.2.1 app\n', '192.0.2.1 app\n'])[0].state, 'other');
    assert.equal(patchHosts('192.0.2.1 app\n# 192.0.2.1 app\n', [{ entry, state: 'enable' }]), '192.0.2.1 app\n192.0.2.1\tapp\n');
});
test('PATH patches preserve order, empty slots, duplicates and the original registry type', () =>
{
    const variable = { value: 'C:\\one;;%ROOT%;C:\\one;', expandable: false };
    assert.deepEqual(patchPaths(variable, [{ value: 'C:\\one', state: 'other' }, { value: 'C:\\new', state: 'enable' }]), { value: 'C:\\one;;%ROOT%;C:\\one;;C:\\new', expandable: false, delete: false });
    assert.deepEqual(patchPaths(variable, [{ value: 'C:\\one', state: 'disable' }]), { value: ';%ROOT%;', expandable: false, delete: false });
    assert.equal(patchPaths(null, [{ value: 'C:\\one', state: 'disable' }]), null);
    assert.deepEqual(patchPaths(null, [{ value: '%ROOT%', state: 'enable' }]), { value: '%ROOT%', expandable: true, delete: false });
    assert.equal(aggregatePaths([variable, null])[0].state, 'other');
});
test('environment variables agree only when value, presence and expansion kind agree', () =>
{
    const a = { name: 'TEMP', value: 'same', expandable: false };
    assert.equal(aggregateVariables([[a], [{ ...a, name: 'temp' }]])[0].common, true);
    assert.equal(aggregateVariables([[a], []])[0].common, false);
    assert.equal(aggregateVariables([[a], [{ ...a, expandable: true }]])[0].common, false);
});

/** 試験に使用する規則の値を構成します。
 * @returns 呼出し元が使用する値です。
 */
const rule = (id, enabled = true) => ({ id, name: 'App', direction: 'Inbound', action: 'Allow', profile: 'Private', protocol: 'TCP', localPorts: ['1234'], remotePorts: ['Any'], localAddresses: ['Any'], remoteAddresses: ['Any'], program: 'Any', enabled, editable: true, revision: 'rule-rev', detailsHash: 'details' });

test('firewall copies missing definitions with new owned IDs and only changes enabled on existing rules', () =>
{
    const groups = aggregateFirewall([[{ rule: rule('a', false), cloneable: true }], []]);
    assert.equal(groups[0].state, 'other');
    const changes = firewallChanges([{ target: { hostId: 'a' } }, { target: { hostId: 'b' } }], groups, new Map([[groups[0].key, 'enable']]));
    assert.equal(changes.length, 2);
    assert.equal(changes[0].action, 'firewallRuleEnabled');
    assert.deepEqual(changes[0].change, { enabled: true });
    assert.equal(changes[1].action, 'firewallRuleAdd');
    assert.equal(changes[1].change.rule.id, 'Contents.HostManager.' + changes[1].operationId.replaceAll('-', ''));
    assert.equal(changes[1].change.rule.localPorts[0], '1234');
    assert.equal(changes[1].change.rule.enabled, true);
    assert.deepEqual(firewallChanges([], groups, new Map([[groups[0].key, 'other']])), []);
});
test('firewall refuses partially uncopyable groups and separates same-name rules with different conditions', () =>
{
    const groups = aggregateFirewall([[{ rule: rule('a'), cloneable: true }], [{ rule: rule('b', false), cloneable: false }]]);
    assert.equal(groups[0].supported, false);
    assert.throws(() => firewallChanges([{}, {}], groups, new Map([[groups[0].key, 'disable']])));
    assert.equal(aggregateFirewall([[{ rule: rule('a'), cloneable: true }], [{ rule: { ...rule('b'), localPorts: ['4321'] }, cloneable: true }]]).length, 2);
});
