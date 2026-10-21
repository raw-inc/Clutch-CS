// Exercise the shipped xterm bundle in a real browser. CDP compositions are native Chrome
// events; the synchronous fixtures cover event orders without depending on an installed IME.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createServer } = require('node:http');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const playwright = require(process.env.CONTENTS_TEST_PLAYWRIGHT || 'playwright');
const root = join(__dirname, '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot');

test('terminal committed input preserves order and intentional repetition', async t =>
{
    const server = createServer((req, res) =>
    {
        if (req.url === '/xterm.js')
        {
            res.setHeader('Content-Type', 'text/javascript');
            res.end(readFileSync(join(root, 'vendor/xterm/xterm.js')));
            return;
        }
        res.setHeader('Content-Type', 'text/html');
        res.end('<div id="terminal"></div><script src="/xterm.js"></script><script>window.reset=()=>{window.term?.dispose();window.term=new Terminal();term.open(document.querySelector("#terminal"));term.focus();window.data=[];term.onData(value=>data.push(value));};reset();</script>');
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
    const engine = process.env.CONTENTS_TEST_BROWSER === 'firefox' ? 'firefox' : 'chromium';
    const browser = await playwright[engine].launch({ headless: true, ...(engine === 'chromium' ? { channel: process.env.CONTENTS_TEST_BROWSER || 'chrome' } : {}) });
    t.after(() => browser.close());
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}`);

    /** 入力された項目を値の一覧へ変換します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function valuesAsync()
    {
        await page.waitForTimeout(30);
        return page.evaluate(() => window.data);
    }

    /** 一時状態を初期値へ戻します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function resetAsync()
    {
        await page.evaluate(() => window.reset());
    }

    if (engine === 'chromium')
    {
        const cdp = await page.context().newCDPSession(page);

        /** 試験で使用する文字入力イベントを組み立てます。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async function composeAsync(reading, commit)
        {
            await cdp.send('Input.imeSetComposition', { text: reading, selectionStart: reading.length, selectionEnd: reading.length });
            await cdp.send('Input.insertText', { text: commit });
        }

        await t.test('native Chrome composition follows historical ASCII and precedes Enter', async() =>
        {
            await resetAsync();
            await page.keyboard.type('ABC DEF ');
            await composeAsync('にほん', '日本');
            await page.keyboard.type('x');
            await page.keyboard.press('Enter');
            assert.equal((await valuesAsync()).join(''), 'ABC DEF 日本x\r');
        });
        await t.test('native Chrome repeated commits, full width spaces and Backspace', async() =>
        {
            await resetAsync();
            await composeAsync('おなじ', '同じ');
            await composeAsync('おなじ', '同じ');
            await composeAsync('　', '　');
            await page.keyboard.press('Backspace');
            assert.equal((await valuesAsync()).join(''), '同じ同じ　\x7f');
        });
        await t.test('native Chrome cancelled composition sends no preedit', async() =>
        {
            await resetAsync();
            await cdp.send('Input.imeSetComposition', { text: 'とりけし', selectionStart: 4, selectionEnd: 4 });
            await cdp.send('Input.imeSetComposition', { text: '', selectionStart: 0, selectionEnd: 0 });
            assert.equal((await valuesAsync()).join(''), '');
        });
    }
    for (const reader of [false, true])
    {
        await t.test(`rapid compositions and the following key remain ordered (screen reader ${reader})`, async() =>
        {
            await resetAsync();
            await page.evaluate(reader =>
            {
                window.term.options.screenReaderMode = reader;
                const area = window.term.textarea;
                area.value = 'old ';
                area.setSelectionRange(4, 4);
                for (const value of ['日', '本', '本'])
                {
                    area.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }));
                    area.dispatchEvent(new CompositionEvent('compositionupdate', { data: value, bubbles: true }));
                    area.setRangeText(value, area.selectionStart, area.selectionEnd, 'end');
                    area.dispatchEvent(new CompositionEvent('compositionend', { data: value, bubbles: true }));
                }
                area.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true, cancelable: true }));
            }, reader);
            assert.deepEqual(await valuesAsync(), ['日', '本', '本', '\r']);
        });
    }
    for (const replaceAll of [false, true])
    {
        await t.test(`commit tracks the selected range when TSF replaces ${replaceAll ? 'the full value' : 'only the composition'}`, async() =>
        {
            await resetAsync();
            await page.evaluate(replaceAll =>
            {
                const area = window.term.textarea;
                area.value = 'before OLD after';
                area.setSelectionRange(7, 10);
                area.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }));
                area.dispatchEvent(new CompositionEvent('compositionupdate', { data: '日本', bubbles: true }));
                area.value = replaceAll ? '日本' : 'before 日本 after';
                area.dispatchEvent(new CompositionEvent('compositionend', { data: '日本', bubbles: true }));
            }, replaceAll);
            assert.deepEqual(await valuesAsync(), ['日本']);
        });
    }
    await t.test('cancel, then a fresh composition before timers fire', async() =>
    {
        await resetAsync();
        await page.evaluate(() =>
        {
            const area = window.term.textarea;
            area.value = 'old';
            area.setSelectionRange(3, 3);
            area.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }));
            area.dispatchEvent(new CompositionEvent('compositionend', { data: '', bubbles: true }));
            area.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }));
            area.value = 'old次';
            area.dispatchEvent(new CompositionEvent('compositionend', { data: '次', bubbles: true }));
        });
        assert.deepEqual(await valuesAsync(), ['次']);
    });
    await t.test('229 insertions and control inputs are not diffed against history', async() =>
    {
        await resetAsync();
        await page.evaluate(() =>
        {
            const area = window.term.textarea;
            area.value = 'history';
            for (const [inputType, value] of [['insertText', 'a'], ['insertText', 'a'], ['deleteContentBackward', null], ['insertLineBreak', null]])
            {
                area.dispatchEvent(new KeyboardEvent('keydown', { key: 'Process', keyCode: 229, bubbles: true, cancelable: true }));
                area.value += value || '';
                area.dispatchEvent(new InputEvent('input', { inputType, data: value, bubbles: true }));
            }
        });
        assert.deepEqual(await valuesAsync(), ['a', 'a', '\x7f', '\r']);
    });
    await t.test('ordinary emoji input and repeated bracketed paste stay intact', async() =>
    {
        await resetAsync();
        await page.evaluate(async() =>
        {
            await new Promise(resolve =>
            {
                window.term.write('\x1b[?2004h', resolve);
            });
            const area = window.term.textarea;
            area.value = '😀';
            area.dispatchEvent(new InputEvent('input', { inputType: 'insertText', data: '😀', bubbles: true }));
            window.term.paste('日本');
            window.term.paste('日本');
        });
        assert.deepEqual(await valuesAsync(), ['😀', '\x1b[200~日本\x1b[201~', '\x1b[200~日本\x1b[201~']);
    });
    assert.deepEqual(errors, []);
});
