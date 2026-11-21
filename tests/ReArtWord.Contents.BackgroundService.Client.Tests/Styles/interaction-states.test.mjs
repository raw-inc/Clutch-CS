import { after, before, test } from 'node:test';
import { readFile } from 'node:fs/promises';
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

test.skip('通常の操作ボタンは面と見分けられ、操作中と無効状態も区別できる（旧仕様では全画面切替にも通常ボタンの面を使う）', async context =>
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
            <div class="desktop-workspace">
                <div class="desktop-targets"><button class="desktop-action desktop-labeled quiet">接続を解除</button></div>
                <div class="desktop-quick-controls"><button class="desktop-action quiet" aria-label="全画面"></button></div>
            </div>
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

test('副ボタンと無効な操作を面と影で区別し、選択や危険操作を示す', { skip: '確認ボタンはクラスで指定する仕様へ変更したため、フッターの位置で判定していた検証を終了します。' }, async context =>
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.setContent(`
        <section class="card">
            <button>操作</button><button disabled>準備中</button>
            <button aria-disabled="true">電源操作</button><button class="primary">保存</button>
            <div class="desktop-workspace">
                <div class="desktop-quick-controls"><button class="desktop-action quiet" aria-label="全画面"></button></div>
                <div class="desktop-targets"><button class="desktop-action desktop-labeled quiet">接続を解除</button></div>
            </div>
            <span class="file-picker-control"><span>ファイルを選ぶ</span><input aria-label="ファイル" type="file"></span>
        </section>
        <div class="pc-display-toggle"><button class="active" aria-pressed="true">概要</button><button aria-pressed="false">詳細</button></div>
        <section class="dialog"><footer><button>キャンセル</button><button class="danger">停止する</button></footer></section>
    `);
    await page.addStyleTag({ content: styles.css });
    const action = page.getByRole('button', { name: '操作', exact: true });

    /** 面・文字色・影をブラウザーの算出結果で読む。
     * @returns 表示中のスタイルです。
     */
    const style = element => element.evaluate(e =>
    {
        const current = getComputedStyle(e);
        return { face: current.backgroundColor, ink: current.color, shadow: current.boxShadow };
    });

    for (const colorScheme of ['light', 'dark'])
    {
        await page.emulateMedia({ colorScheme });
        await action.blur();
        await page.mouse.move(0, 0);
        const enabled = await style(action);
        const disabled = await style(page.getByRole('button', { name: '準備中' }));
        assert.notEqual(enabled.face, disabled.face);
        assert.notEqual(enabled.face, (await style(page.locator('.card'))).face);
        assert.notEqual(enabled.shadow, 'none');
        assert.equal(disabled.shadow, 'none');
        assert.deepEqual(disabled, await style(page.getByRole('button', { name: '電源操作' })));
        assert.equal(enabled.face, (await style(page.getByRole('button', { name: '接続を解除' }))).face);
        assert.deepEqual(enabled, await style(page.locator('.file-picker-control > span')));
        assert.notEqual(enabled.face, (await style(page.getByRole('button', { name: '保存' }))).face);
        assert.equal((await style(page.getByRole('button', { name: '全画面' }))).shadow, 'none');
        assert.equal((await style(page.getByRole('button', { name: '詳細' }))).face, 'rgba(0, 0, 0, 0)');
        assert.notEqual((await style(page.getByRole('button', { name: '概要' }))).face, 'rgba(0, 0, 0, 0)');
        assert.notEqual((await style(page.getByRole('button', { name: '停止する' }))).face,
            (await style(page.getByRole('button', { name: 'キャンセル' }))).face);

        await action.hover();
        assert.notEqual((await style(action)).face, enabled.face);
        await page.mouse.down();
        assert.equal((await style(action)).shadow, 'none');
        await page.mouse.up();
        await page.mouse.move(0, 0);
        await page.keyboard.press('Tab');
        await action.focus();
        assert.ok((await style(action)).shadow.includes('inset'));
    }
});

test('副ボタンと無効状態を区別し、危険操作は入口と確定で面を切り替える', async context =>
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.setContent(`
        <section class="card">
            <button>操作</button><button disabled>準備中</button>
            <button aria-disabled="true">電源操作</button><button class="primary">保存</button>
            <div class="desktop-workspace">
                <div class="desktop-quick-controls"><button class="desktop-action quiet" aria-label="全画面"></button></div>
                <div class="desktop-targets"><button class="desktop-action desktop-labeled quiet">接続を解除</button></div>
            </div>
            <span class="file-picker-control"><span>ファイルを選ぶ</span><input aria-label="ファイル" type="file"></span>
        </section>
        <div class="pc-display-toggle"><button class="active" aria-pressed="true">概要</button><button aria-pressed="false">詳細</button></div>
        <section class="dialog">
            <button class="danger">停止の確認</button><button class="restore-action">復元の確認</button>
            <footer><button>キャンセル</button><button class="danger confirm-action">停止する</button><button class="restore-action confirm-action">復元を開始</button></footer>
        </section>
    `);
    await page.addStyleTag({ content: styles.css });
    const action = page.getByRole('button', { name: '操作', exact: true });

    /** 面・文字色・影をブラウザーの算出結果で読む。
     * @returns 表示中のスタイルです。
     */
    const style = element => element.evaluate(e =>
    {
        const current = getComputedStyle(e);
        return { face: current.backgroundColor, ink: current.color, shadow: current.boxShadow };
    });

    for (const colorScheme of ['light', 'dark'])
    {
        await page.emulateMedia({ colorScheme });
        await action.blur();
        await page.mouse.move(0, 0);
        const enabled = await style(action);
        const disabled = await style(page.getByRole('button', { name: '準備中' }));
        assert.notEqual(enabled.face, disabled.face);
        assert.notEqual(enabled.face, (await style(page.locator('.card'))).face);
        assert.notEqual(enabled.shadow, 'none');
        assert.equal(disabled.shadow, 'none');
        assert.deepEqual(disabled, await style(page.getByRole('button', { name: '電源操作' })));
        assert.equal(enabled.face, (await style(page.getByRole('button', { name: '接続を解除' }))).face);
        assert.deepEqual(enabled, await style(page.locator('.file-picker-control > span')));
        assert.notEqual(enabled.face, (await style(page.getByRole('button', { name: '保存' }))).face);
        assert.equal((await style(page.getByRole('button', { name: '全画面' }))).shadow, 'none');
        assert.equal((await style(page.getByRole('button', { name: '詳細' }))).face, 'rgba(0, 0, 0, 0)');
        assert.notEqual((await style(page.getByRole('button', { name: '概要' }))).face, 'rgba(0, 0, 0, 0)');
        for (const [entryName, confirmName] of [['停止の確認', '停止する'], ['復元の確認', '復元を開始']])
        {
            const entry = await style(page.getByRole('button', { name: entryName, exact: true }));
            const confirm = await style(page.getByRole('button', { name: confirmName, exact: true }));
            assert.equal(entry.face, enabled.face);
            assert.equal(entry.shadow, enabled.shadow);
            assert.notEqual(entry.ink, enabled.ink);
            assert.notEqual(confirm.face, entry.face);
            assert.notEqual(confirm.ink, entry.ink);
            assert.equal(confirm.shadow, 'none');
        }

        await action.hover();
        assert.notEqual((await style(action)).face, enabled.face);
        await page.mouse.down();
        assert.equal((await style(action)).shadow, 'none');
        await page.mouse.up();
        await page.mouse.move(0, 0);
        await page.keyboard.press('Tab');
        await action.focus();
        assert.ok((await style(action)).shadow.includes('inset'));
    }
});

test('スクロールできる方向にだけ続きの影を表示する', async context =>
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.setContent('<clutch-scroll-area style="height: 140px"><div><div id="content">確認</div></div></clutch-scroll-area>');
    await page.addStyleTag({ content: styles.css });
    const script = new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/scroll-area.js', import.meta.url);
    await page.addScriptTag({ content: await readFile(script, 'utf8') });
    const area = page.locator('clutch-scroll-area'), content = page.locator('#content');
    await area.evaluate(e => customElements.whenDefined(e.localName));
    assert.equal(await area.getAttribute('data-scroll-before'), null);
    assert.equal(await area.getAttribute('data-scroll-after'), null);

    await content.evaluate(e =>
    {
        e.style.height = '600px';
        e.textContent = '長い本文';
    });
    await page.locator('clutch-scroll-area[data-scroll-after]:not([data-scroll-before])').waitFor();
    await area.locator('div').first().evaluate(e =>
    {
        e.scrollTop = e.scrollHeight;
    });
    await page.locator('clutch-scroll-area[data-scroll-before]:not([data-scroll-after])').waitFor();

    await content.evaluate(e =>
    {
        e.style.height = '';
        e.textContent = '確認';
    });
    await page.locator('clutch-scroll-area:not([data-scroll-before], [data-scroll-after])').waitFor();
});

test('横スクロールの端と一覧の行ボタンを通常操作と区別する', async context =>
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.setContent(`
        <clutch-scroll-area data-scroll-axis="inline" style="width: 180px"><nav><div style="width: 540px">
            <button class="row-button">一覧の行</button><button>操作</button>
        </div></nav></clutch-scroll-area>
    `);
    await page.addStyleTag({ content: styles.css });
    const script = new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/scroll-area.js', import.meta.url);
    await page.addScriptTag({ content: await readFile(script, 'utf8') });
    const area = page.locator('clutch-scroll-area');
    await page.locator('clutch-scroll-area[data-scroll-after]:not([data-scroll-before])').waitFor();

    const row = page.getByRole('button', { name: '一覧の行' });
    const action = page.getByRole('button', { name: '操作' });
    assert.equal(await row.evaluate(element => getComputedStyle(element).boxShadow), 'none');
    assert.notEqual(await action.evaluate(element => getComputedStyle(element).boxShadow), 'none');

    await area.locator('nav').evaluate(element =>
    {
        element.scrollLeft = element.scrollWidth / 3;
    });
    await page.locator('clutch-scroll-area[data-scroll-before][data-scroll-after]').waitFor();

    await area.locator('nav').evaluate(element =>
    {
        element.scrollLeft = element.scrollWidth;
    });
    await page.locator('clutch-scroll-area[data-scroll-before]:not([data-scroll-after])').waitFor();
});

test('両方向に送れる一覧は固定見出しの下に上下・左右の続きがあることを示す', async context =>
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.setContent(`
        <clutch-scroll-area data-scroll-axis="both" style="width: 180px; height: 120px">
            <div><table style="width: 400px">
                <thead style="position: sticky; top: 0"><tr><th>項目</th></tr></thead>
                <tbody><tr><td style="height: 280px">データ</td></tr></tbody>
            </table></div>
        </clutch-scroll-area>
    `);
    await page.addStyleTag({ content: styles.css });
    const script = new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/scroll-area.js', import.meta.url);
    await page.addScriptTag({ content: await readFile(script, 'utf8') });
    const area = page.locator('clutch-scroll-area');
    await area.locator('.scroll-inline-after').waitFor();
    const port = area.locator(':scope > div');
    await port.evaluate(e => e.scrollTo(e.scrollWidth / 4, e.scrollHeight / 4));
    await area.locator('.scroll-inline-before').waitFor();
    const edges = await area.evaluate(e =>
    {
        const header = e.querySelector('thead').getBoundingClientRect();
        const hint = e.querySelector('.scroll-inline-before').getBoundingClientRect();
        return { top: getComputedStyle(e, '::before').top, headerHeight: header.height, headerBottom: header.bottom, hintTop: hint.top };
    });
    assert.equal(parseFloat(edges.top), Math.round(edges.headerHeight));
    assert.ok(edges.headerBottom <= edges.hintTop);
    await port.evaluate(e => e.scrollTo(e.scrollWidth, e.scrollHeight));
    await area.locator('.scroll-inline-after').waitFor({ state: 'hidden' });
    await page.locator('clutch-scroll-area[data-scroll-before]:not([data-scroll-after])').waitFor();
});

test('長いJSON本文では左右のスクロールの手がかりが切り替わる', async context =>
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.setContent('<clutch-scroll-area data-scroll-axis="both" style="width: 180px"><pre></pre></clutch-scroll-area>');
    await page.addStyleTag({ content: styles.css });
    const script = new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/scroll-area.js', import.meta.url);
    await page.addScriptTag({ content: await readFile(script, 'utf8') });
    const port = page.locator('pre');
    await port.evaluate(element =>
    {
        element.textContent = JSON.stringify({ message: 'example-'.repeat(60) });
    });
    const before = page.locator('.scroll-inline-before'), after = page.locator('.scroll-inline-after');
    await after.waitFor();
    assert.equal(await before.isVisible(), false);
    await port.evaluate(element =>
    {
        element.scrollLeft = element.scrollWidth;
    });
    await before.waitFor();
    await after.waitFor({ state: 'hidden' });
});

test('ワークフローのタブ列が狭くなっても選択中のタブを表示する', async context =>
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.setContent(`
        <div id="strip-host" style="width: 440px">
            <nav class="editor-workflow-tabs" style="display: flex; overflow: auto">
                <button style="flex: 0 0 140px">音響</button>
                <button style="flex: 0 0 140px">映像</button>
                <button aria-pressed="true" style="flex: 0 0 140px">ステージキュー</button>
            </nav>
        </div>
    `);
    const script = new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/browser.js', import.meta.url);
    await page.addScriptTag({ content: await readFile(script, 'utf8') });
    await page.evaluate(() => window.contentsBrowser.revealTab(document.querySelector('nav')));
    await page.locator('#strip-host').evaluate(host =>
    {
        host.style.width = '260px';
    });
    await page.waitForFunction(() =>
    {
        const strip = document.querySelector('nav'), selected = strip.querySelector('[aria-pressed="true"]');
        const limits = strip.getBoundingClientRect(), box = selected.getBoundingClientRect();
        return limits.left <= box.left && box.right <= limits.right;
    });
    const strip = page.locator('nav');
    assert.ok(0 < await strip.evaluate(element => element.scrollLeft));
    await strip.evaluate(element =>
    {
        element.scrollLeft = 0;
    });
    await page.evaluate(() => new Promise(resolve =>
    {
        requestAnimationFrame(() => requestAnimationFrame(resolve));
    }));
    assert.equal(await strip.evaluate(element => element.scrollLeft), 0);
});

test('Terminal の補助キーを上下二段に保ち、狭い幅ではキー列だけを送れる', async context =>
{
    const page = await browser.newPage({ viewport: { width: 320, height: 720 } });
    context.after(() => page.close());
    await page.setContent(`
        <clutch-scroll-area data-scroll-axis="inline" class="terminal-key-scroll"><div class="terminal-keys" role="toolbar">
            <div>${['Ctrl', 'Alt', 'Shift', 'Esc', 'Tab', 'Del', 'Backspace', 'Enter'].map(text => '<button>' + text + '</button>').join('')}</div>
            <div>${['Left', 'Up', 'Down', 'Right', 'Home', 'End', 'PgUp', 'PgDn'].map(text => '<button>' + text + '</button>').join('')}</div>
        </div></clutch-scroll-area>
    `);
    await page.addStyleTag({ content: styles.css });
    const toolbar = page.getByRole('toolbar');
    const dimensions = await toolbar.evaluate(element =>
    {
        const [upper, lower] = [...element.children].map(row => [...row.children].map(button => button.getBoundingClientRect()));
        const joined = [...upper, ...lower];
        return { lines: new Set(joined.map(box => box.top)).size,
            aligned: upper.every((box, index) => box.left === lower[index].left && box.width === lower[index].width),
            keyHeight: Math.min(...joined.map(box => box.height)),
            keyOverflow: element.scrollWidth - element.clientWidth,
            pageOverflow: document.documentElement.scrollWidth - innerWidth };
    });
    assert.equal(dimensions.lines, 2);
    assert.ok(dimensions.aligned);
    assert.ok(44 <= dimensions.keyHeight);
    assert.ok(0 < dimensions.keyOverflow);
    assert.equal(dimensions.pageOverflow, 0);
    await toolbar.getByRole('button', { name: 'PgDn' }).focus();
    assert.ok(0 < await toolbar.evaluate(element => element.scrollLeft));
});

test('複数の変更対象がある確認画面では、本文が長くても選択欄の2行を表示する', async context =>
{
    const page = await browser.newPage({ viewport: { width: 390, height: 700 } });
    context.after(() => page.close());
    const value = Array.from({ length: 22 }, (_, index) => `変更前の値 ${index + 1}`).join('\n');
    await page.setContent(`
        <div class="host-manager">
            <dialog class="dialog hm-dialog hm-preview-dialog" open>
                <header class="dialog-heading"><h2>変更内容を確認</h2></header>
                <clutch-scroll-area><div class="dialog-content hm-dialog-body">
                    <p>2台のホスト</p>
                    <p>変更前と変更後の値を確認してください。</p>
                    <div class="hm-preview-layout">
                        <div class="hm-preview-targets">
                            <button aria-pressed="true"><strong>Control-01</strong><small>仮想スイッチを削除</small></button>
                            <button aria-pressed="false"><strong>Render-02</strong><small>仮想スイッチを削除</small></button>
                        </div>
                        <section class="hm-preview">
                            <h3>仮想スイッチを削除</h3>
                            <div class="hm-comparison"><div><h4>変更前</h4><pre>${value}</pre></div><div><h4>変更後</h4><pre>削除</pre></div></div>
                        </section>
                    </div>
                </div></clutch-scroll-area>
                <footer class="toolbar hm-dialog-actions hm-confirm-footer">
                    <label><input type="checkbox">対象の通信経路を確認しました。</label>
                    <div class="hm-actions"><button>キャンセル</button><button>削除</button></div>
                </footer>
            </dialog>
        </div>`);
    await page.addStyleTag({ content: styles.css });
    const bounds = await page.locator('.hm-preview-targets').evaluate(targets =>
    {
        const rect = targets.getBoundingClientRect(), body = targets.closest('.hm-dialog-body');
        const button = targets.querySelector('button'), label = button.querySelector('small').getBoundingClientRect();
        return { targetBottom: rect.top + targets.clientHeight, labelBottom: label.bottom, scrollable: body.clientHeight < body.scrollHeight };
    });
    assert.ok(bounds.scrollable && bounds.labelBottom <= bounds.targetBottom, JSON.stringify(bounds));
});

test('ツールごとの見出しと共通ダイアログの見出しをそれぞれの大きさで表示する', async context =>
{
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    context.after(() => page.close());
    await page.setContent(`
        <main class="remote-tools">
            <section class="transfer-jobs"><h2>転送キュー</h2><p>転送はありません</p></section>
            <div class="terminal-sidebar-heading"><h2>共有セッション</h2></div>
            <div class="scope-packet-heading"><h2>取得したパケット</h2></div>
            <div class="scope-details"><h2>パケット詳細</h2></div>
            <div class="desktop-popover"><header><h2>接続の詳細</h2></header></div>
            <div class="license-list"><h2>ライブラリー名</h2></div>
            <dialog class="dialog dialog-compact" open><header class="dialog-heading"><h2>終了するセッション</h2></header></dialog>
        </main>`);
    await page.addStyleTag({ content: styles.css });
    const sizes = await page.locator('h2').evaluateAll(headings => headings.map(heading => getComputedStyle(heading).fontSize));
    assert.deepEqual(sizes, ['18px', '13px', '16px', '12px', '12px', '18px', '19px']);
});

test('ページの見出し帯やHostManagerの中でも確認ダイアログの操作サイズを揃える', async context =>
{
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    context.after(() => page.close());
    await page.setContent(`
        <main class="page-title">
            <button>設定を開く</button>
            <dialog class="dialog dialog-compact" open>
                <header class="dialog-heading"><h2>保持期間の確認</h2><button class="quiet icon-button" aria-label="閉じる">×</button></header>
                <clutch-scroll-area><div class="dialog-content"><p>既存の記録にも適用します。</p></div></clutch-scroll-area>
                <footer class="toolbar"><button>確定</button></footer>
            </dialog>
        </main>
        <div class="host-manager">
            <dialog class="dialog hm-dialog" open>
                <header class="dialog-heading"><h2>変更内容の確認</h2><button class="quiet icon-button" aria-label="閉じる">×</button></header>
                <clutch-scroll-area><div class="dialog-content hm-dialog-body"><p class="hm-muted">選択したホストに適用します。</p></div></clutch-scroll-area>
                <footer class="toolbar hm-confirm-footer"><div class="hm-actions"><button>適用</button></div></footer>
            </dialog>
        </div>`);
    await page.addStyleTag({ content: styles.css });

    /** テーマや配置に関係なく、見出し・本文・確定操作の寸法を取得します。
     * @returns 開いているダイアログごとの寸法です。
     */
    const measure = () => page.locator('.dialog').evaluateAll(dialogs => dialogs.map(dialog =>
    {
        const heading = dialog.querySelector('header');
        const copy = dialog.querySelector('.dialog-content > p');
        const action = dialog.querySelector('footer button');
        return { heading: heading.getBoundingClientRect().height, size: getComputedStyle(copy).fontSize,
            action: action.getBoundingClientRect().height,
            bottom: dialog.querySelector('footer').getBoundingClientRect().top - copy.getBoundingClientRect().bottom };
    }));

    const [retention, host] = await measure();
    assert.ok(Math.abs(retention.heading - host.heading) <= 2);
    assert.equal(retention.size, '13px');
    assert.equal(host.size, retention.size);
    assert.equal(retention.action, host.action);
    assert.equal(retention.action, 37);
    assert.ok(Math.abs(retention.bottom - host.bottom) <= 1);
    await page.setViewportSize({ width: 390, height: 844 });
    for (const metrics of await measure())
    {
        assert.equal(metrics.action, 44);
    }
});
