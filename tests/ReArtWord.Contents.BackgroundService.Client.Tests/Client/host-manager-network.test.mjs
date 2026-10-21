import test from 'node:test';
import assert from 'node:assert/strict';
import { subnet, canGroup, validateNetwork, parseAddresses } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/network-model.js';
import { vlanLabel } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/vlan.js';
import { previewValues } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/operations.js';

test('access VLAN, untagged and unavailable VLAN metadata have distinct previews', () =>
{
    assert.equal(vlanLabel('Access', 120), 'Access · VLAN 120');
    assert.equal(vlanLabel('Untagged', null), 'Untagged');
    assert.equal(vlanLabel(null, null), 'Unconfirmed');
    assert.equal(vlanLabel('Trunk', null), 'Trunk');
});
test('an untagged VLAN change stays visible in the confirmation even though VLAN ID is null', () =>
{
    const [before, after] = previewValues({ action: 'virtualNicVlan', before: { data: { name: 'management', switchName: 'trunk', vlanMode: 'Access', vlanId: 120 } }, change: { vlanId: null } });
    assert.match(before, /Access · VLAN 120/);
    assert.match(after, /Untagged/);
    assert.doesNotMatch(after, /120/);
});
test('network confirmation shows only the requested fields and deletions are explicit', () =>
{
    const [before, after] = previewValues({ action: 'network', before: { data: { ipv4: { dhcp: true, automaticDns: true, dns: ['10.0.0.1'], metric: 25 } } }, change: { family: 4, automaticDns: false, dns: ['10.0.0.53'] } });
    assert.doesNotMatch(before + after, /DHCP|Metric/);
    assert.match(after, /10.0.0.53/);
    assert.equal(previewValues({ action: 'virtualNicDelete', before: { data: { name: 'one' } }, change: {} })[1], 'Delete');
});

test('network candidates preserve arbitrary IPv4 and IPv6 prefixes', () =>
{
    assert.equal(subnet('10.0.10.121', 18, 4), '10.0.0.0/18');
    assert.equal(subnet('2001:db8:abcd:1234::1', 56, 6), '2001:db8:abcd:1200:0:0:0:0/56');
    for (const [address, family] of [['169.254.1.2', 4], ['127.0.0.1', 4], ['fe80::1', 6], ['::1', 6], ['ff02::1', 6]])
    {
        assert.equal(canGroup(address, family), false);
    }
    assert.equal(canGroup('192.168.10.20', 4), true);
});
test('DNS-only changes leave DHCP unchanged and gateway changes require explicit manual mode', () =>
{
    const current = { ipv4: { dhcp: true, addresses: [{ address: '10.0.0.2', prefixLength: 24 }] } };
    validateNetwork({ family: 4, automaticDns: false, dns: ['10.0.0.53'] }, current);
    assert.throws(() => validateNetwork({ family: 4, gateways: ['10.0.0.1'] }, current));
    validateNetwork({ family: 4, dhcp: false, gateways: ['10.0.0.1'] }, current);
    assert.throws(() => validateNetwork({ family: 4, dhcp: false, gateways: ['10.1.0.1'] }, current));
    assert.throws(() => parseAddresses('10.0.0.1/33', 4));
    assert.throws(() => parseAddresses('10.0.0.1', 4));
});
test('IPv6 link-local gateway retains explicit interface scope and requires router discovery off', () =>
{
    const current = { ipv6: { dhcp: false, routerDiscovery: true, addresses: [] } };
    assert.throws(() => validateNetwork({ family: 6, gateways: ['fe80::1'] }, current));
    validateNetwork({ family: 6, routerDiscovery: false, gateways: ['fe80::1'] }, current);
    assert.throws(() => validateNetwork({ family: 6, routerDiscovery: false, gateways: ['fe80::1%2'] }, current));
});
