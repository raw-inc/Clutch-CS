import assert from 'node:assert/strict';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { chromium, firefox } from 'playwright';

const root = new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/', import.meta.url);

for (const engine of [chromium, firefox])
{
    test(engine.name() + ': 状態タグは近い文字幅を揃え、長い状態名の追加・削除に追従する', async context =>
    {
        const browser = await engine.launch();
        context.after(() => browser.close());
        const page = await browser.newPage();
        await page.setContent('<style>span{display:inline-flex;font:16px/1.4 monospace;white-space:nowrap}</style><clutch-status-labels><div><span data-status-label>A</span><span data-status-label>12345678</span></div></clutch-status-labels><clutch-status-labels><span data-status-label>A</span></clutch-status-labels>');
        await page.addStyleTag({ path: fileURLToPath(new URL('styles/status.css', root)) });
        await page.addScriptTag({ path: fileURLToPath(new URL('status-labels.js', root)) });

        const tags = page.locator('[data-status-label]');
        await page.waitForFunction(() =>
        {
            const [first, second, separate] = [...document.querySelectorAll('[data-status-label]')].map(e => e.offsetWidth);
            return first === second && separate < first;
        });
        await tags.nth(1).evaluate(e =>
        {
            e.textContent = 'Awaiting recovery confirmation';
        });
        await page.waitForFunction(() =>
        {
            const [first, second, separate] = [...document.querySelectorAll('[data-status-label]')].map(e => e.offsetWidth);
            return first === separate && first * 2 < second;
        });
        await tags.nth(1).evaluate(e =>
        {
            e.textContent = '12345678';
        });
        await page.waitForFunction(() =>
        {
            const tags = document.querySelectorAll('[data-status-label]');
            return tags[0].offsetWidth === tags[1].offsetWidth;
        });
        assert.equal(await tags.count(), 3);
    });
}
