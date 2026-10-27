import { test } from 'node:test';
import assert from 'node:assert/strict';
import { experimentalEnabled, readExperimentalPreference, saveExperimentalPreference } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/experimental.js';

/** ブラウザーの保存領域とURLだけを差し替えます。
 * @returns 再読取りと故障を確認するための保存値です。
 */
function browser(t)
{
    const saved = ['localStorage', 'location'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]);
    const values = new Map();
    Object.defineProperty(globalThis, 'localStorage', { value: { getItem: key => values.get(key) ?? null,

        setItem: (key, value) => values.set(key, value) }, configurable: true });
    Object.defineProperty(globalThis, 'location', { value: { search: '' }, configurable: true });
    t.after(() =>
    {
        for (const [key, value] of saved)
        {
            if (value)
            {
                Object.defineProperty(globalThis, key, value);
            }
            else
            {
                delete globalThis[key];
            }
        }
    });
    return values;
}

test('browser preference survives module reloads and switching it off restores URL opt-in', async t =>
{
    const values = browser(t);
    assert.equal(experimentalEnabled(), false);
    saveExperimentalPreference(true);
    assert.equal(experimentalEnabled(), true);
    location.search = '?experimental=false';
    assert.equal(experimentalEnabled(), true);
    const reopened = await import('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/experimental.js?reopened');
    assert.equal(reopened.readExperimentalPreference(), true);
    reopened.saveExperimentalPreference(false);
    assert.equal(experimentalEnabled(), false);
    location.search = '?experimental=true';
    assert.equal(experimentalEnabled(), true);
    assert.equal(readExperimentalPreference(), false);
    assert.deepEqual([...values], [['contents.ui.experimental', 'false']]);
});

test('only an explicit saved true enables features; legacy numeric query remains SignalScope-only', t =>
{
    const values = browser(t);
    for (const saved of ['false', '1', 'True', '"true"', '{broken', ''])
    {
        values.set('contents.ui.experimental', saved);
        assert.equal(experimentalEnabled(), false);
    }
    for (const query of ['?experimental=false', '?experimental=True', '?extra=true', '?experimental=1'])
    {
        location.search = query;
        assert.equal(experimentalEnabled(), false);
    }
    assert.equal(experimentalEnabled(true), true);
});

test('blocked storage does not break pages or URL opt-in and saving reports failure', t =>
{
    browser(t);
    Object.defineProperty(globalThis, 'localStorage', { get: () =>
    {
        throw new Error('storage blocked');
    },

    configurable: true });
    assert.equal(experimentalEnabled(), false);
    assert.throws(readExperimentalPreference, /storage blocked/);
    assert.equal(readExperimentalPreference(true), false);
    assert.throws(() => saveExperimentalPreference(true), /storage blocked/);
    location.search = '?experimental=true';
    assert.equal(experimentalEnabled(), true);
});
