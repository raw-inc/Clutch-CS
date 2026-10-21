import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import vm from 'node:vm';

const source = await fs.readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/browser-startup.js', import.meta.url), 'utf8');

test('外部起動スクリプトはPWAの登録を一度だけ開始する', async () =>
{
    let calls = 0;
    const context = { window: { contentsBrowser: { registerPwa: () =>
    {
        calls++;
        return Promise.resolve();
    } } } };
    await vm.runInNewContext(source, context);
    assert.equal(calls, 1);
});

test('PWAの起動失敗を未処理Promiseにせず、既存のエラー欄へ文字列として表示する', async () =>
{
    const notice = { hidden: true, textContent: '' };
    const failure = new Error('<unavailable>');
    const context = {
        window: { contentsBrowser: { registerPwa: () => Promise.reject(failure) } },
        document: { getElementById: id =>
        {
            assert.equal(id, 'blazor-error-ui');
            return notice;
        } }
    };
    await vm.runInNewContext(source, context);
    assert.equal(notice.hidden, false);
    assert.equal(notice.textContent, failure.message);
});
