import { test } from 'node:test';
import assert from 'node:assert/strict';
import { selectedWakeInterfaces, wakePreview } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/wake-on-lan.js';

test('physical defaults include new physical adapters while an explicit empty selection remains empty', () =>
{
    const inventory = { interfaces: [{ id: 'A', name: 'Ethernet A', physical: true, macAddress: '02-11-22-33-44-55' },
        { id: 'B', name: 'Ethernet B', physical: true, macAddress: '02-22-33-44-55-66' }, { id: 'V', name: 'Virtual', physical: false, macAddress: '02-AA-BB-CC-DD-EE' }] };
    assert.deepEqual(selectedWakeInterfaces(inventory, { usePhysicalDefaults: true, interfaceIds: [] }).map(item => item.id), ['A', 'B']);
    assert.deepEqual(selectedWakeInterfaces(inventory, { usePhysicalDefaults: false, interfaceIds: [] }), []);
    assert.deepEqual(selectedWakeInterfaces(inventory, { usePhysicalDefaults: false, interfaceIds: ['a'] }).map(item => item.id), ['A']);
    assert.deepEqual(selectedWakeInterfaces(inventory, { usePhysicalDefaults: false, interfaceIds: ['removed'] }), []);
    const values = wakePreview({ before: { data: { inventory, selection: { usePhysicalDefaults: true, interfaceIds: [] } } }, change: { usePhysicalDefaults: false, interfaceIds: ['b'] } });
    assert.match(values[0], /Ethernet A.*02-11-22-33-44-55/);
    assert.match(values[1], /Ethernet B.*02-22-33-44-55-66/);
    assert.doesNotMatch(values[1], /Ethernet A|Virtual/);
});
