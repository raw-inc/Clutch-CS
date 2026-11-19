import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { chromium, firefox } from 'playwright';

const root = new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/', import.meta.url);

for (const engine of [chromium, firefox])
{
    let browser;
    before(async () =>
    {
        browser = await engine.launch();
    });
    after(async () =>
    {
        await browser.close();
    });

    test(engine.name() + ': 同じ行だけを比較し、2倍の境界・幅と本文の変化に追従する', async context =>
    {
        const page = await browser.newPage({ viewport: { width: 600, height: 500 } });
        context.after(() => page.close());
        await page.setContent('<style>body{margin:0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:8px}</style><clutch-card-rows><div class="grid"><article><div style="height:50px"></div></article><article><div style="height:99px"></div></article><article><div style="height:200px"></div></article></div></clutch-card-rows>');
        await page.addStyleTag({ path: fileURLToPath(new URL('styles/controls.css', root)) });
        await page.addScriptTag({ path: fileURLToPath(new URL('card-rows.js', root)) });
        const first = page.locator('article').first(), secondBody = page.locator('article > div').nth(1);
        await page.waitForFunction(() => document.querySelector('article').offsetHeight === 99);
        await secondBody.evaluate(e =>
        {
            e.style.height = '100px';
        });
        await page.waitForFunction(() => document.querySelector('article').offsetHeight === 50);
        await secondBody.evaluate(e =>
        {
            e.style.height = '80px';
        });
        await page.waitForFunction(() => document.querySelector('article').offsetHeight === 80);
        await page.setViewportSize({ width: 360, height: 500 });
        await page.waitForFunction(() => document.querySelector('article').offsetHeight === 50);
        assert.equal((await first.boundingBox()).height, 50);
    });

    test(engine.name() + ': 入れ子の内容は伸ばさず、説明の長さと高さの比に応じて上端を揃える', async context =>
    {
        const page = await browser.newPage();
        context.after(() => page.close());
        await page.setContent('<style>.grid{display:grid;grid-template-columns:repeat(2,1fr)}p{margin:0;line-height:20px}</style><clutch-card-rows><div class="grid"><article><p data-card-caption>One</p><div data-card-inset style="height:40px"></div></article><article><p data-card-caption>First<br>Second</p><div data-card-inset style="height:60px"></div></article></div></clutch-card-rows>');
        await page.addStyleTag({ path: fileURLToPath(new URL('styles/controls.css', root)) });
        await page.addScriptTag({ path: fileURLToPath(new URL('card-rows.js', root)) });

        /** 入れ子の上端が指定した配置になるまで描画を待つ。
         * @returns 配置を確認する待機処理。
         */
        const atSameTop = same => page.waitForFunction(expected =>
        {
            const [a, b] = document.querySelectorAll('[data-card-inset]');
            return (a.getBoundingClientRect().top === b.getBoundingClientRect().top) === expected;
        }, same);

        await atSameTop(true);
        await page.locator('p').last().evaluate(p =>
        {
            p.innerHTML = 'One<br>Two<br>Three<br>Four';
        });
        await atSameTop(false);
        await page.locator('[data-card-inset]').last().evaluate(e =>
        {
            e.style.height = '59px';
        });
        await atSameTop(true);
        assert.deepEqual(await page.locator('[data-card-inset]').evaluateAll(es => es.map(e => e.offsetHeight)), [40, 59]);
    });
}
