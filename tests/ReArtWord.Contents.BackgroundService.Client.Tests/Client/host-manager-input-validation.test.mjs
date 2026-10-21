import { test } from 'node:test';
import assert from 'node:assert/strict';
import { hasControlCharacters } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/input-validation.js';

test('名前の検証はASCII制御文字を拒否し、日本語・絵文字・通常の空白を保持する', () =>
{
    assert.equal(hasControlCharacters(' 設定名🌐 \u007f'), false);
    assert.equal(hasControlCharacters(undefined), false);
    assert.equal(hasControlCharacters(null), false);
    for (const character of ['\0', '\t', '\n', '\r', '\u001b', '\u001f'])
    {
        assert.equal(hasControlCharacters('前' + character + '後'), true);
    }
});

test('複数行本文はタブと改行を許可しても、他の制御文字とDELの扱いを独立に維持する', () =>
{
    assert.equal(hasControlCharacters('localhost\t127.0.0.1\r\n# 日本語', { allowWhitespace: true, includeDelete: true }), false);
    for (const character of ['\0', '\u000b', '\u000c', '\u001b', '\u001f', '\u007f'])
    {
        assert.equal(hasControlCharacters(character, { allowWhitespace: true, includeDelete: true }), true);
    }
    assert.equal(hasControlCharacters('\u007f', { allowWhitespace: true }), false);
});
