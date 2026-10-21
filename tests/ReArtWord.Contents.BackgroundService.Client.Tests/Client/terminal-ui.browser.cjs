// Real DOM, Fullscreen API, xterm rendering and resize messages; the remote peer is an isolated fixture.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createServer } = require('node:http');
const { readFileSync, existsSync } = require('node:fs');
const { resolve } = require('node:path');
const { chromium } = require(process.env.CONTENTS_TEST_PLAYWRIGHT || 'playwright');
const root = resolve(__dirname, '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot');

test('terminal native fullscreen modes, controls, resize and Core policy', { timeout: 45000 }, async t =>
{
    const peer = { clusterId: 'fixture', peerId: 'peer', hostId: 'host', instanceId: 'one' };
    const sessions = [];
    let settings = { strictUserToken: false, version: null };
    const operations = [];
    const server = createServer(async(req, res) =>
    {
        const url = new URL(req.url, 'http://localhost');
        if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/client/'))
        {
            let text = '';
            for await (const chunk of req)
            {
                text += chunk;
            }
            const body = text ? JSON.parse(text) : undefined;
            operations.push({ path: url.pathname, body });
            let value;
            switch (url.pathname)
            {
                case '/client/terminal/sessions':value = [{ clusterId: peer.clusterId, peer, sessions, confirmed: true }];
                    break;
                case '/api/terminal/shells':value = [{ name: 'pwsh', path: 'C:/pwsh.exe' }];
                    break;
                case '/api/terminal/capabilities':value = { userAvailable: true, administratorAvailable: false, administratorReason: 'CoreAdministratorRequired', settings };
                    break;
                case '/api/terminal/settings/save':settings = { ...body, version: 'saved' };
                    value = settings;
                    break;
                case '/api/terminal/create':value = { ...body, shell: body.shell || 'C:/pwsh.exe', exited: false, sequence: 0 };
                    sessions.push(value);
                    break;
                case '/api/terminal/claim':value = { ...sessions.find(s => s.sessionId === body.sessionId), owner: body.owner };
                    break;
                default:res.statusCode = 404;
                    value = { error: 'unexpected' };
            }
            res.setHeader('Content-Type', 'application/json');
            res.end(JSON.stringify(value));
            return;
        }
        if (url.pathname === '/terminal')
        {
            res.setHeader('Content-Type', 'text/html');
            res.end(String.raw`<link rel="stylesheet" href="/app.css"><link rel="stylesheet" href="/remote-tools.css"><link rel="stylesheet" href="/vendor/xterm/xterm.css">
<div style="height:90px"></div><main id="app" class="remote-tools" style="padding:12px"></main><footer class="app-statusbar" style="height:26px"></footer>
<script src="/vendor/xterm/xterm.js"></script><script src="/vendor/xterm/addon-fit.js"></script>
<script>
window.inputs=[];window.WebSocket=class{
 static OPEN=1;
 constructor(url){const p=new URL(url).searchParams;this.session={sessionId:p.get('session'),owner:p.get('owner'),sequence:0,exited:false};this.readyState=1;this.bufferedAmount=0;setTimeout(()=>this.receive({type:'ready',session:this.session}),0);}
 receive(value){this.onmessage?.({data:JSON.stringify(value)});}
 send(text){const value=JSON.parse(text);if(value.type!=='input')return;inputs.push(value.request);this.session.sequence=value.request.sequence+1;setTimeout(()=>{this.receive({type:'ack',session:this.session});if(!this.output){this.output=true;const text='Clutch-CS Terminal test peer\r\nPS > ';this.receive({type:'output',output:{session:this.session,offset:0,nextOffset:text.length,data:btoa(text)}});}},0);}
 close(){this.readyState=3;}
};
</script><script type="module">import{mount,unmount}from'/remote-tools.js';window.unmount=()=>unmount(document.querySelector('#app'));await mount(document.querySelector('#app'),'terminal','ja');window.mounted=true;</script>`);
            return;
        }
        const file = resolve(root, '.' + url.pathname);
        if (!file.startsWith(root + require('node:path').sep) || !existsSync(file))
        {
            res.statusCode = 404;
            res.end();
            return;
        }
        res.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'application/octet-stream');
        res.end(readFileSync(file));
    });
    await new Promise(resolve =>
    {
        server.listen(0, '127.0.0.1', resolve);
    });
    t.after(() => new Promise(resolve =>
    {
        server.closeAllConnections();
        server.close(resolve);
    }));
    const browser = await chromium.launch({ channel: process.env.CONTENTS_TEST_BROWSER || 'chrome', headless: true });
    t.after(() => browser.close());
    const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
    page.setDefaultTimeout(8000);
    const errors = [];
    page.on('pageerror', e =>
    {
        errors.push(e.message);
        process.stderr.write(e.message + '\n');
    });
    const base = `http://127.0.0.1:${server.address().port}`;
    await page.goto(base + '/terminal?experimental=true');
    await page.waitForFunction(() => window.mounted);

    /** 操作の実行と処理中表示を持つボタンを構成します。
     * @returns 呼出し元が使用する値です。
     */
    const button = name => page.getByRole('button', { name, exact: true });

    await button('host / peer').click();
    assert(await button('＋ 管理者実行').isDisabled());
    await button('＋ ユーザー実行').click();
    await page.waitForFunction(() => (0) < (window.inputs.length));

    /** 試験用の端末寸法を返します。
     * @returns 呼出し元が使用する値です。
     */
    const dimensions = () => page.evaluate(() => ({ last: window.inputs.at(-1), height: document.querySelector('.terminal-workspace').getBoundingClientRect().height, fullscreen: document.fullscreenElement?.className || null }));

    const initial = await dimensions();
    await t.test('disconnect is beside creation; policy saves without disconnecting the current shell', async() =>
    {
        const order = await page.locator('.terminal-toolbar').evaluate(el => [...el.children].map(c => c.textContent));
        assert(order.indexOf('接続解除') === order.indexOf('＋ 管理者実行') + 1);
        await button('厳密な非昇格: OFF').click();
        await button('厳密な非昇格: ON').waitFor();
        assert.equal(sessions.length, 1);
        assert.equal(settings.strictUserToken, true);
    });
    await t.test('workspace fullscreen keeps controls and updates PTY rows', async() =>
    {
        await button('全画面表示').click();
        await page.waitForFunction(() => document.fullscreenElement?.classList.contains('terminal-workspace'));
        await page.waitForFunction(rows => (rows) < (window.inputs.at(-1).rows), initial.last.rows);
        assert(await button('接続解除').isVisible());
        assert.equal((await dimensions()).height, 800);
    });
    await t.test('screen-only fullscreen expands columns and contains its context menu', async() =>
    {
        const previous = (await dimensions()).last.columns;
        await button('端末のみ全画面').click();
        await page.waitForFunction(() => document.fullscreenElement?.classList.contains('terminal-screen'));
        await page.waitForFunction(columns => (columns) < (window.inputs.at(-1).columns), previous);
        await page.locator('.terminal-screen').click({ button: 'right', position: { x: 100, y: 100 } });
        assert(await page.getByRole('menu').isVisible());
        assert(await page.getByRole('menu').evaluate(el => document.fullscreenElement.contains(el)));
        await page.keyboard.press('Escape');
        await page.waitForFunction(() => !document.fullscreenElement);
        assert.equal(await page.evaluate(() => window.inputs.map(v => atob(v.data)).join('')), '');
    });
    await t.test('disconnect exits fullscreen and leaves the shared shell available for takeover', async() =>
    {
        await button('全画面表示').click();
        await button('接続解除').click();
        await page.waitForFunction(() => !document.fullscreenElement);
        assert.equal(sessions.length, 1);
        assert(await button('全画面表示').isDisabled());
        await button('接続を引き継ぐ').click();
        await button('全画面表示').click();
        await page.evaluate(() => window.unmount());
        await page.waitForFunction(() => !document.fullscreenElement);
        assert.equal(operations.filter(o => o.path.endsWith('/close')).length, 0);
    });
    await t.test('saved policy stays effective when the experimental control is hidden', async() =>
    {
        await page.goto(base + '/terminal');
        await page.waitForFunction(() => window.mounted);
        await button('host / peer').click();
        assert.equal(await page.getByRole('button', { name: /厳密な非昇格/ }).count(), 0);
        assert.equal(settings.strictUserToken, true);
        if (process.env.CONTENTS_TEST_TERMINAL_SCREENSHOT)
        {
            await button('接続を引き継ぐ').click();
            await page.locator('.xterm-screen').waitFor();
            await page.waitForFunction(() => (0) < (window.inputs.length));
            await page.screenshot({ path: process.env.CONTENTS_TEST_TERMINAL_SCREENSHOT });
        }
    });
    assert.deepEqual(errors, []);
});
