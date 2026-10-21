import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { parse } from 'parse5';

/** ブラウザーで検査する、所有境界を含む代表的なコンポーネント構造です。 */
export const fixturePath = fileURLToPath(new URL('./components.html', import.meta.url));

/** ビルドしたSDK出力と実際の読み込み順から、スタイルとスコープ対応を取得します。
 * @returns 配信されるCSSと、Razorが要素へ付けるスコープです。
 */
export async function loadStylesAsync(webRoot, bundlePath)
{
    const links = [];

    /** 入口HTMLで宣言したスタイルシートを、配信順に収集します。 */
    function visit(node)
    {
        const attributes = new Map(node.attrs?.map(attribute => [attribute.name, attribute.value]));
        if (node.tagName === 'link' && attributes.get('rel') === 'stylesheet')
        {
            links.push(attributes.get('href'));
        }
        for (const child of node.childNodes ?? [])
        {
            visit(child);
        }
    }

    visit(parse(await readFile(path.join(webRoot, 'index.html'), 'utf8')));
    const sheets = [];
    let bundle;
    for (const link of [...links, 'host-manager/style.css'])
    {
        const content = await readFile(link.endsWith('.styles.css') ? bundlePath : path.join(webRoot, link), 'utf8');
        sheets.push(content);
        if (link.endsWith('.styles.css'))
        {
            bundle = content;
        }
    }
    const scopes = {};
    for (const match of bundle.matchAll(/\/\* \/([^*]+)\.razor\.rz\.scp\.css \*\/([\s\S]*?)(?=\/\* \/|$)/gu))
    {
        scopes[match[1]] = match[2].match(/\[(b-[a-z0-9]+)\]/u)?.[1];
    }
    return { css: sheets.join('\n'), scopes };
}

/** 検証対象は、配置・入力・視認性に影響する実際の算出値です。 */
const properties = ['display', 'color', 'backgroundColor', 'width', 'height', 'minWidth', 'maxWidth', 'minHeight', 'fontSize', 'fontWeight', 'lineHeight', 'padding', 'margin', 'borderWidth', 'borderColor', 'borderRadius', 'position', 'gap', 'gridTemplateColumns', 'fill', 'stroke', 'strokeWidth', 'overflow', 'whiteSpace', 'resize', 'zIndex', 'visibility', 'opacity', 'animationDuration'];

/** 子コンポーネントとJS生成要素を区別して、実ブラウザーの描画結果を取得します。
 * @returns 検査対象の算出スタイルです。
 */
export async function captureStylesAsync(browser, styles, width, reducedMotion = 'no-preference')
{
    const page = await browser.newPage({ viewport: { width, height: 1000 }, reducedMotion });
    try
    {
        await page.setContent(await readFile(fixturePath, 'utf8'));
        await page.evaluate(scopes =>
        {
            for (const element of document.querySelectorAll('body *'))
            {
                const owner = element.closest('[data-owner]')?.getAttribute('data-owner');
                if (scopes[owner])
                {
                    element.setAttribute(scopes[owner], '');
                }
            }
        }, styles.scopes);
        await page.addStyleTag({ content: styles.css });
        await page.mouse.move(width - 1, 999);
        return await page.evaluate(properties => Object.fromEntries([...document.querySelectorAll('[data-check]')].map(element =>
        {
            const style = getComputedStyle(element);
            return [element.getAttribute('data-check'), Object.fromEntries(properties.map(property => [property, style[property]]))];
        })), properties);
    }
    finally
    {
        await page.close();
    }
}
