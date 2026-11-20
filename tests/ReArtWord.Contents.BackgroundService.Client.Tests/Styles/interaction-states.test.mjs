import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { chromium } from 'playwright';
import { loadStylesAsync } from './css-browser.mjs';

let browser, styles;
before(async () =>
{
    const client = fileURLToPath(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/', import.meta.url));
    const config = process.env.CONTENTS_TEST_CONFIGURATION ?? 'Debug';
    styles = await loadStylesAsync(path.join(client, 'wwwroot'), path.join(client, 'obj', config, 'net10.0/scopedcss/bundle/ReArtWord.Contents.BackgroundService.Client.styles.css'));
    browser = await chromium.launch();
});
after(async () => browser?.close());

test('設定取得中は一周で二度加減速し、動きを減らす設定では文言と静止した図を保つ', async context =>
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.setContent('<div class="host-manager"><div role="status" class="hm-loading"><span class="hm-spinner"></span><span>設定を取得中 · 1 / 2</span></div></div>');
    await page.addStyleTag({ content: styles.css });
    const arc = await page.locator('.hm-spinner').evaluate(e =>
    {
        const animation = e.getAnimations()[0];
        return [animation.effect.getTiming().duration, ...animation.effect.getKeyframes().map(frame => [frame.offset, frame.easing])];
    });
    assert.equal(arc[0], 1520);
    assert.deepEqual(arc.slice(1).map(frame => frame[0]), [0, 0.5, 1]);
    assert.notEqual(arc[1][1], 'linear');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    assert.equal(await page.locator('.hm-spinner').evaluate(e => e.getAnimations().length), 0);
    assert.equal(await page.getByRole('status').innerText(), '設定を取得中 · 1 / 2');
});

test('キーボードで詳細を開閉し、閉じかけても開き直せる', async context =>
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.setContent('<main class="page"><details class="secondary-details"><summary>詳細</summary><button>内容を確認</button></details></main>');
    await page.addStyleTag({ content: styles.css });
    const summary = page.locator('summary'), action = page.getByRole('button');
    for (const reducedMotion of ['no-preference', 'reduce'])
    {
        await page.emulateMedia({ reducedMotion });
        await summary.focus();
        await page.keyboard.press('Enter');
        await page.keyboard.press('Enter');
        await page.keyboard.press('Enter');
        await page.keyboard.press('Tab');
        await action.waitFor();
        assert.ok(await action.evaluate(e => document.activeElement === e));
        await summary.focus();
        await page.keyboard.press('Enter');
        await page.waitForFunction(() => !document.querySelector('details').open);
    }
});

test('ホスト設定の編集可能な欄はフォーカス前でも隣接する面と区別できる', async context =>
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.setContent('<main class="page host-manager"><section style="background:var(--panel)"><label class="hm-field">Time zone<input value="UTC"></label><label class="hm-field">Action<select><option>Wake</option></select></label></section></main>');
    await page.addStyleTag({ content: styles.css });
    for (const colorScheme of ['light', 'dark'])
    {
        await page.emulateMedia({ colorScheme });
        const ratios = await page.locator('input, select').evaluateAll(fields =>
        {
            /** ブラウザーが解決したsRGB色を相対輝度に変換する。
             * @returns 黒を0、白を1とした相対輝度。
             */
            function luminance(color)
            {
                const [r, g, b] = color.match(/[\d.]+/gu).map(n => Number(n) / 255).map(n => n <= 0.04045 ? n / 12.92 : ((n + 0.055) / 1.055) ** 2.4);
                return 0.2126 * r + 0.7152 * g + 0.0722 * b;
            }

            return fields.map(field =>
            {
                const border = luminance(getComputedStyle(field).borderBottomColor), panel = luminance(getComputedStyle(field.closest('section')).backgroundColor);
                return (Math.max(border, panel) + 0.05) / (Math.min(border, panel) + 0.05);
            });
        });
        assert.ok(ratios.every(ratio => 3 <= ratio), JSON.stringify(ratios));
    }
});

test('通常の操作ボタンは面と見分けられ、操作中と無効状態も区別できる', async context =>
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.setContent(`
        <section class="card">
            <button>操作</button>
            <button class="primary">保存</button>
            <button class="quiet">閉じる</button>
            <button class="danger">削除</button>
            <button disabled>準備中</button>
            <button aria-disabled="true">電源操作</button>
            <div class="terminal-workspace"><div class="terminal-toolbar"><button class="terminal-action">コピー</button></div></div>
            <div class="file-pane"><div class="file-navigation"><button>ドライブ</button></div></div>
            <span class="file-picker-control"><span>ファイルを選ぶ</span><input type="file" aria-label="ファイル"></span>
            <div class="desktop-workspace"><div class="desktop-quick-controls"><button class="desktop-action quiet" aria-label="全画面"></button></div></div>
        </section>
    `);
    await page.addStyleTag({ content: styles.css });
    const action = page.getByRole('button', { name: '操作', exact: true });

    /** 現在の状態で描画される色を読み取る。
     * @returns ブラウザーが適用した背景色。
     */
    const background = element => element.evaluate(e => getComputedStyle(e).backgroundColor);

    for (const colorScheme of ['light', 'dark'])
    {
        await page.emulateMedia({ colorScheme });
        await action.blur();
        await page.mouse.move(0, 0);
        const face = await background(action);
        assert.notEqual(face, await background(page.locator('.card')));
        assert.notEqual(face, await background(page.getByRole('button', { name: '保存' })));
        assert.notEqual(face, await background(page.getByRole('button', { name: '閉じる' })));
        assert.notEqual(face, await background(page.getByRole('button', { name: '削除' })));
        const disabled = await background(page.getByRole('button', { name: '準備中' }));
        assert.notEqual(face, disabled);
        assert.equal(disabled, await background(page.getByRole('button', { name: '電源操作' })));
        const toolFaces = await page.locator('.terminal-action, .file-navigation button, .file-picker-control > span, .desktop-action').evaluateAll(elements =>
            elements.map(e => getComputedStyle(e).backgroundColor));
        assert.ok(toolFaces.every(color => color === face), JSON.stringify(toolFaces));

        await action.hover();
        const hovered = await background(action);
        assert.notEqual(face, hovered);
        await page.mouse.down();
        assert.notEqual(hovered, await background(action));
        await page.mouse.up();
        await page.mouse.move(0, 0);
        await page.keyboard.press('Tab');
        await action.focus();
        assert.ok(await action.evaluate(e => e.matches(':focus-visible') && getComputedStyle(e).boxShadow.includes('inset')));
        assert.notEqual(face, await background(action));
    }
});
