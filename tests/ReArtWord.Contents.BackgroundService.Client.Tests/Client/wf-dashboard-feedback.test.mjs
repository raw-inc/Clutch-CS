import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import vm from 'node:vm';

const source = await fs.readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/browser.js', import.meta.url), 'utf8');

/** 操作完了時だけ呼ばれる描画と、ブラウザーが所有する短いアニメーションを観測します。
 * @returns 画面・設定の変更と、描画呼出しの記録です。
 */
function harness()
{
    const window = {}, motion = { matches: false }, animations = [], calls = [];
    const feedback = {
        getAnimations: () => [...animations],

        /** ブラウザーの再生を記録し、前の演出を取り消せるようにします。 */
        animate(keyframes, options)
        {
            calls.push({ keyframes, options });
            const animation = {
                cancel: () => animations.splice(animations.indexOf(animation), 1)
            };
            animations.push(animation);
        }
    };
    const card = { dataset: { cardId: 'pressed' },

        querySelector: () => feedback };
    const board = { isConnected: true,

        querySelectorAll: () => [card] };
    vm.runInNewContext(source, { window,

        matchMedia: () => motion,

        document: { addEventListener: () =>
        {} } });
    return { motion, board, calls, animations,

        show: id => window.contentsBrowser.dashboardActionFeedback(board, id) };
}

test('feedback targets only the completed card and repeated presses replace the finite animation', () =>
{
    const f = harness();
    f.show('other');
    assert.equal(f.calls.length, 0);
    f.show('pressed');
    assert.equal(f.animations.length, 1);
    const { keyframes, options } = f.calls[0];
    assert.equal(keyframes[0].opacity, 0);
    assert.equal(keyframes.at(-1).opacity, 0);
    assert.ok(Math.max(...keyframes.map(frame => frame.opacity)) <= 0.1, 'confirmation must remain a weak highlight');
    assert.ok((0) < (options.duration) && options.duration <= 600);
    assert.equal(options.fill, undefined, 'the overlay must return to its transparent base style');
    f.show('pressed');
    assert.equal(f.calls.length, 2);
    assert.equal(f.animations.length, 1, 'rapid presses must not accumulate animation or delay another operation');
});

test('reduced motion and a departed dashboard never start feedback', () =>
{
    const f = harness();
    f.motion.matches = true;
    f.show('pressed');
    assert.equal(f.calls.length, 0);
    f.motion.matches = false;
    f.board.isConnected = false;
    f.show('pressed');
    assert.equal(f.calls.length, 0);
});
