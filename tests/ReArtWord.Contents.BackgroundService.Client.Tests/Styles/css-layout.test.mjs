import { before, after, test } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { readFile } from 'node:fs/promises';
import { parse, serialize } from 'parse5';
import { chromium } from 'playwright';
import { loadStylesAsync, captureStylesAsync } from './css-browser.mjs';

/** SDKが生成したCSSのスコープとブラウザーを試験間で共有します。 */
let styles;
let browser;

before(async () =>
{
    const client = fileURLToPath(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/', import.meta.url));
    const configuration = process.env.CONTENTS_TEST_CONFIGURATION ?? 'Debug';
    styles = await loadStylesAsync(path.join(client, 'wwwroot'), path.join(client, 'obj', configuration, 'net10.0/scopedcss/bundle/ReArtWord.Contents.BackgroundService.Client.styles.css'));
    browser = await chromium.launch({ headless: true });
});

after(async () =>
{
    await browser?.close();
});

/** 画面幅によらず維持する、コンポーネント境界の表示契約です。 */
const expected = {
    'inspector-title': { fontSize: '14px' },
    'inspector-input': { fontSize: '11px' },
    'inline-field': { height: '64px' },
    'inline-group': { height: '20px' },
    'inline-input': { height: '29px', fontSize: '11px' },
    'node-box': { stroke: 'rgb(141, 181, 207)', strokeWidth: '1.8px' },
    'catalog-icon': { width: '14px' },
    'dialog-textarea': { minHeight: '180px' },
    'code-textarea': { padding: '8px', fontSize: '12px', minHeight: '0px', backgroundColor: 'rgba(0, 0, 0, 0)', color: 'rgba(0, 0, 0, 0)' },
    'code-input': { minHeight: '80px' },
    'keyword': { color: 'rgb(168, 188, 237)' },
    'desktop-viewport': { position: 'relative' },
    'annotations': { position: 'absolute' },
    'preset': { width: '220px' },
    'hidden': { display: 'none' },
    'host-link': { padding: '0px', borderWidth: '0px' },
    'host-history': { padding: '0px 14px' },
    'host-activity': { padding: '10px 16px', borderRadius: '0px' },
    'skeleton': { animationDuration: '0s' }
};

for (const width of [1440, 900, 640])
{
    test(`${width}pxでも子コンポーネントとJS生成要素に必要なCSSが届く`, async () =>
    {
        const actual = await captureStylesAsync(browser, styles, width, 'reduce');
        for (const [element, properties] of Object.entries(expected))
        {
            for (const [property, value] of Object.entries(properties))
            {
                assert.equal(actual[element][property], value, `${element}.${property}`);
            }
        }
        assert.equal(actual.navigation.padding, width <= 1100 ? '0px 12px' : '0px 15px');
        assert.equal(actual.navigation.fontSize, width < 750 ? '11px' : '12px');
        assert.equal(actual.workspace.display, width < 750 ? 'block' : 'grid');
        assert.equal(actual.studio.height, width <= 900 ? '804px' : '878px');
        assert.equal(actual['wire'].animationDuration, '0s');
        assert.equal(actual['peer-health'].opacity, '1');
        assert.equal(actual['overview-health'].opacity, '0');
    });
}

test('Background Syncの進捗はホバーなしで表示され、通常Readyだけが省略される', async () =>
{
    for (const width of [1440, 900, 640])
    {
        const actual = await captureStylesAsync(browser, styles, width, 'reduce');
        assert.equal(actual['background-health'].opacity, '1');
        assert.equal(actual['background-health'].maxWidth, 'none');
        assert.equal(actual['overview-health'].opacity, '0');
    }
});

test('通常設定では分離したキーフレームが接続線と待機表示へ適用される', async () =>
{
    const actual = await captureStylesAsync(browser, styles, 1440);
    assert.notEqual(actual.wire.animationDuration, '0s');
    assert.notEqual(actual.skeleton.animationDuration, '0s');
});

for (const file of ['ui-start.html', 'ui-update.html'])
{
    test(`${file}でも起動画面のロゴと状態表示が保たれる`, async () =>
    {
        const root = fileURLToPath(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/', import.meta.url));
        const document = parse(await readFile(path.join(root, file), 'utf8'));

        /** 起動処理を実行せず、実際の画面HTMLとCSSだけを検証します。 */
        function removeScripts(node)
        {
            node.childNodes = node.childNodes?.filter(child => !['script', 'link'].includes(child.tagName));
            for (const child of node.childNodes ?? [])
            {
                removeScripts(child);
            }
        }

        removeScripts(document);
        const page = await browser.newPage();
        try
        {
            await page.setContent(serialize(document));
            await page.addStyleTag({ content: await readFile(path.join(root, 'ui-update.css'), 'utf8') });
            await page.locator('#splash').evaluate(element =>
            {
                element.hidden = false;
            });
            assert.equal(await page.locator('#brand-mark').evaluate(element => getComputedStyle(element).width), '42px');
            assert.equal(await page.locator('#brand-mark svg').evaluate(element => getComputedStyle(element).height), '42px');
            assert.equal(await page.locator('#status').evaluate(element => getComputedStyle(element).fontSize), '11px');
        }
        finally
        {
            await page.close();
        }
    });
}
