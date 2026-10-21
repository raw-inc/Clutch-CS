import { test } from 'node:test';
import assert from 'node:assert/strict';
import { releaseNotes, summarizeTests } from './release-notes.mjs';
import { validationReport } from './validation.mjs';

const passed = Object.fromEntries(validationReport({}).results.map(result => [result.id, 'success']));
const plan = { tag: 'v1.2.3', prerelease: false };
const runUrl = 'https://github.com/owner/project/actions/runs/12345';

test('a group passes only when every included stage succeeds', () => {
    assert.ok(summarizeTests(passed).every(result => result.outcome === 'success'));
    for (const stage of Object.keys(passed)) {
        const results = summarizeTests({ ...passed, [stage]: 'failure' });
        assert.ok(results.some(result => result.outcome === 'failure'), stage);
    }
});

test('mixed, cancelled and incomplete results are never reported as passing', () => {
    for (const [overrides, expected] of [
        [{ client_tests: 'skipped' }, 'partial'],
        [{ unit_tests: 'skipped', client_tests: 'skipped', installer_tests: 'skipped' }, 'skipped'],
        [{ client_tests: 'cancelled' }, 'cancelled'],
        [{ client_tests: undefined }, 'unknown'],
        [{ client_tests: 'unrecognized' }, 'unknown'],
        [{ unit_tests: 'failure', client_tests: 'skipped' }, 'failure'],
        [{ unit_tests: 'failure', client_tests: 'cancelled' }, 'failure'],
        [{ unit_tests: 'failure', client_tests: undefined }, 'failure'],
    ]) {
        assert.equal(summarizeTests({ ...passed, ...overrides })[0].outcome, expected);
    }
    assert.ok(summarizeTests().every(result => result.outcome === 'unknown'));
    assert.ok(summarizeTests(null).every(result => result.outcome === 'unknown'));
});

test('release notes show actual outcomes, matching downloads, CI details and generated changes', () => {
    const notes = releaseNotes(plan, 'Generated changes\n\n- A change', {
        outcomes: { ...passed, integration_tests: 'failure', performance_tests: 'skipped' }, runUrl,
    });
    assert.match(notes, /\| 結合テスト \| ❌ 失敗あり \|/u);
    assert.match(notes, /\| パフォーマンステスト \| ➖ 未実行 \|/u);
    assert.ok(notes.includes(runUrl));
    assert.ok(notes.includes(`Clutch-CS-Setup-${plan.tag}-win-x64.zip`));
    assert.ok(notes.includes(`Clutch-CS-Source-${plan.tag}.zip`));
    assert.ok(notes.includes('Generated changes\n\n- A change'));
});

test('missing results stay unconfirmed and prereleases are identified', () => {
    const notes = releaseNotes({ tag: 'v1.2.3-alpha', prerelease: true }, 'Changes');
    assert.match(notes, /プレリリース/u);
    assert.match(notes, /❔ 未確認/u);
    assert.doesNotMatch(notes, /✅ 通過/u);
    assert.doesNotMatch(releaseNotes(plan, 'Changes', { outcomes: passed }), /プレリリース/u);
});
