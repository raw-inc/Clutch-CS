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
