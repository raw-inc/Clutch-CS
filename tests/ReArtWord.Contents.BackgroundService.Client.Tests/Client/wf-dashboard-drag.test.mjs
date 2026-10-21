import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import vm from 'node:vm';

const source = await fs.readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/browser.js', import.meta.url), 'utf8');

/** 実際のブラウザー処理に、カードの描画位置とネイティブイベントを渡します。
 * @returns 表示位置からの挿入先とドラッグ開始処理です。
 */
function harness(rectangles)
{
    const handlers = new Map(), window = {};
    vm.runInNewContext(source, { window, document: { addEventListener: (name, callback) => handlers.set(name, callback) } });
    const board = { querySelectorAll: () => rectangles.map(([id, left, top, width, height]) => ({
        dataset: { cardId: id },

        getBoundingClientRect: () => ({ left, top, width, height, bottom: top + height })
    })) };
    return {
        target: (x, y) => window.contentsBrowser.dashboardDropTarget(board, x, y),

        drag: handlers.get('dragstart'),
        enter: handlers.get('dragenter'),
        over: handlers.get('dragover')
    };
}

test('body, horizontal gaps, row gaps and trailing space all select insertion positions', () =>
{
    const { target } = harness([['a', 20, 30, 120, 60], ['b', 160, 30, 120, 120], ['c', 300, 30, 120, 80], ['d', 20, 170, 120, 100], ['e', 160, 170, 120, 60]]);
    assert.equal(target(40, 80), 'a');
    assert.equal(target(270, 80), 'c');
    assert.equal(target(150, 50), 'b');
    assert.equal(target(500, 157), 'd');
    assert.equal(target(30, 165), 'd');
    assert.equal(target(440, 200), null);
    assert.equal(target(30, 300), null);
    assert.equal(target(0, 0), 'a');
});

test('one-column layout inserts before or after the vertical card midpoint', () =>
{
    const { target } = harness([['a', 20, 30, 200, 60], ['b', 20, 110, 200, 120]]);
    assert.equal(target(150, 40), 'a');
    assert.equal(target(20, 70), 'b');
    assert.equal(target(210, 100), 'b');
    assert.equal(target(150, 150), 'b');
    assert.equal(target(20, 200), null);
});

test('a full-width line break remains an insertion boundary between grid rows', () =>
{
    const { target } = harness([['a', 20, 30, 120, 60], ['b', 160, 30, 120, 80], ['break', 20, 130, 500, 24], ['c', 20, 170, 120, 100]]);
    assert.equal(target(10, 142), 'break');
    assert.equal(target(400, 142), 'c');
    assert.equal(target(30, 210), 'c');
});

test('scrolled cards use viewport coordinates and an empty board has no target', () =>
{
    const { target } = harness([['a', 20, -90, 120, 60], ['b', 160, -90, 120, 120], ['c', 20, 50, 120, 100]]);
    assert.equal(target(150, 0), 'b');
    assert.equal(target(0, 45), 'c');
    assert.equal(harness([]).target(50, 50), null);
});

test('card drag advertises a native move while catalog drag keeps its copy payload', () =>
{
    const { drag } = harness([]), data = new Map(), dataTransfer = { setData: (type, value) => data.set(type, value) };
    drag({ target: { closest: selector => selector.includes('dashboard-card') ? { dataset: { contentsDashboardCard: 'card-1' } } : null }, dataTransfer });
    assert.equal(dataTransfer.effectAllowed, 'move');
    assert.equal(data.get('application/x-contents-dashboard-card'), 'card-1');
    data.clear();
    drag({ target: { closest: selector => selector.includes('node-type') ? { dataset: { contentsNodeType: 'value.constant' } } : null }, dataTransfer });
    assert.equal(dataTransfer.effectAllowed, 'copy');
    assert.equal(data.get('application/x-contents-node'), 'value.constant');
    drag({ target: { closest: () => null }, dataTransfer: null });
});

test('entering a gap accepts a card immediately without waiting for the next dragover or render', () =>
{
    const { enter, over } = harness([]);
    for (const accept of [enter, over])
    {
        let prevented = false;

        /** ブラウザー既定動作を同期的に取り消したことを記録します。 */
        const preventDefault = () =>
        {
            prevented = true;
        };

        const dataTransfer = { types: ['application/x-contents-dashboard-card'], dropEffect: 'none' };
        accept({ target: { closest: () => ({}) }, dataTransfer, preventDefault });
        assert.equal(prevented, true);
        assert.equal(dataTransfer.dropEffect, 'move');
        prevented = false;
        accept({ target: { closest: () => null }, dataTransfer, preventDefault });
        assert.equal(prevented, false);
        accept({ target: { closest: () => ({}) }, dataTransfer: { types: ['text/plain'] }, preventDefault });
        assert.equal(prevented, false);
    }
});
