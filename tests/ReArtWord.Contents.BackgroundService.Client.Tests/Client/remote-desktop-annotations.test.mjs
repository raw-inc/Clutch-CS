import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAnnotationState, createAnnotationPointer, drawAnnotations } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-annotations.js';

test('repeated snapshots retain each point birth time and expired cursors disappear', () =>
{
    const state = createAnnotationState(), first = { duration: 4000, cursors: [[0xff0000, -100, 50]], points: [[1, 0xff0000, 1, -100, 50, 1000]] };
    state.receive(first, 2000);
    state.receive({ ...first, points: [[1, 0xff0000, 1, -100, 50, 1000], [2, 0xff0000, 1, -50, 50, 0]] }, 3000);
    const read = state.read(4000);
    assert.equal(read.alpha(read.points[0]), 0.25);
    assert.equal(read.alpha(read.points[1]), 0.75);
    assert.equal(state.read(5001).points.length, 1);
    assert.equal(state.read(5001).cursors.length, 0);
    assert.equal(state.read(7001).points.length, 0);
    state.clear();
    assert.deepEqual(state.read(7001).points, []);
});

test('pointer traffic is bounded, view-only gated, and consists only of annotation packets', () =>
{
    /** 試験で送信されたデータを確認できる形へ取り出します。 */
    const sent = [];

    /** 試験対象の画面から条件に一致する値を取得します。
     * @returns 表示と入力の検証に使用する値です。
     */
    const locate = e => e.clientX < 0 ? null : [0, e.clientX, e.clientY];

    let viewing = true;
    const pointer = createAnnotationPointer((...args) => sent.push(args), locate, () => viewing);

    /** 試験対象へ渡す入力イベントを構成します。
     * @returns clientX・clientY・buttonを含む結果です。
     */
    const event = (x, button = 0) => ({ clientX: x, clientY: 40, button });

    pointer.move(event(10));
    pointer.tick(100);
    assert.deepEqual(sent[0], [13, 0, 10, 40, 1, 0]);
    assert.equal(pointer.down(event(20), 110), true);
    const firstLine = sent.at(-1)[5];
    for (let n = 0;n < 100;n++)
    {
        pointer.move(event(n));
    }
    pointer.tick(120);
    assert.equal(sent.length, 2);
    pointer.tick(160);
    assert.equal(sent.at(-1)[2], 99);
    assert.equal(sent.at(-1)[4], 2);
    pointer.up(event(100), 170);
    assert.equal(sent.at(-1)[4], 1);
    pointer.leave();
    assert.deepEqual(sent.at(-1), [13]);
    pointer.down(event(30), 200);
    assert.notEqual(sent.at(-1)[5], firstLine);
    pointer.move(event(-1));
    pointer.tick(250);
    assert.deepEqual(sent.at(-1), [13]);
    viewing = false;
    const before = sent.length;
    pointer.move(event(10));
    pointer.down(event(10), 300);
    pointer.tick(350);
    assert.equal(sent.length, before);
    assert.ok(sent.every(packet => packet[0] === 13));
});

test('malformed and unbounded remote annotation snapshots are rejected', () =>
{
    const state = createAnnotationState(), base = { duration: 4000, cursors: [], points: [] };
    for (const bad of [{ ...base, duration: Infinity }, { ...base, cursors: [[1, NaN, 0]] }, { ...base, points: [[1, 1, 1, 0, 0, -1]] }, { ...base, points: Array.from({ length: 513 }, (_, i) => [i + 1, 1, 1, 0, 0, 0]) }])
    {
        assert.throws(() => state.receive(bad, 1));
    }
});

test('rendering offsets negative monitor coordinates and fades each segment endpoint independently', () =>
{
    const state = createAnnotationState();
    state.receive({ duration: 4000, cursors: [[0xff0000, -100, 20]], points: [[1, 0xff0000, 1, -100, 20, 2000], [2, 0xff0000, 1, -80, 20, 0]] }, 3000);
    const positions = [], colors = [];
    const context = {
        /** 試験用canvasの消去要求を記録します。 */
        clearRect()
        {},

        /** 試験用canvasの描画経路を開始します。 */
        beginPath()
        {},

        /** 試験用canvasの円弧描画を受け付けます。 */
        arc()
        {},

        /** 試験用canvasの塗りつぶしを受け付けます。 */
        fill()
        {},

        /** 試験用canvasの描画経路を閉じます。 */
        closePath()
        {},

        /** 試験用canvasの線描画を受け付けます。 */
        stroke()
        {},

        /** 試験用canvasの描画開始位置を設定します。 */
        moveTo(x, y)
        {
            positions.push([x, y]);
        },

        /** 試験用canvasの線の接続先を設定します。 */
        lineTo()
        {},

        /** 試験用canvasのグラデーションを作成します。
         * @returns addColorStopを含む結果です。
         */
        createLinearGradient()
        {
            return {
                /** 試験用グラデーションの色指定を受け付けます。 */
                addColorStop(n, color)
                {
                    colors.push(color);
                } };
        } };
    assert.equal(drawAnnotations(context, { left: -200, top: -20, width: 400, height: 200 }, state, 4000), true);
    assert.deepEqual(positions[0], [100, 40]);
    assert.match(colors[0], /0\.1475/);
    assert.match(colors[1], /0\.442/);
});
