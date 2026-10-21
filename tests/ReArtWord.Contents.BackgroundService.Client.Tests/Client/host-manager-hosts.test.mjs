import test from 'node:test';
import assert from 'node:assert/strict';
import { parseHosts, hostsText, setHostsEntry, removeHostsEntry, validateHostsText } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/hosts-model.js';

test('opening the entry editor preserves comments, whitespace, blank lines and line endings', () =>
{
    for (const text of ['', '# comment\r\n\r\n  127.0.0.1\tlocalhost  alias  # keep spacing\r\n# ::1 localhost\r\n', '# header\n192.0.2.1 app.local', 'unknown line\r\n# note\n'])
    {
        assert.equal(hostsText(parseHosts(text)), text);
    }
});
test('editing one entry retains all other lines and recognizes disabled IPv6 mappings', () =>
{
    const text = '# keep this\r\n\r\n127.0.0.1  localhost\r\n# ::1 localhost alias # IPv6\r\n';
    const document = parseHosts(text);
    assert.equal(document.lines[3].entry.enabled, false);
    setHostsEntry(document, 3, { ...document.lines[3].entry, enabled: true, names: 'localhost v6.local' });
    assert.equal(hostsText(document), '# keep this\r\n\r\n127.0.0.1  localhost\r\n::1\tlocalhost v6.local\t# IPv6\r\n');
    assert.equal(validateHostsText(hostsText(document)), hostsText(document));
});
test('an unchanged entry is byte-preserved and adding after an unterminated line stays separate', () =>
{
    const text = '  192.0.2.3    app.local alias.local # note';
    const document = parseHosts(text);
    setHostsEntry(document, 0, { ...document.lines[0].entry });
    assert.equal(hostsText(document), text);
    setHostsEntry(document, document.lines.length, { enabled: true, address: '2001:db8::2', names: 'v6.local, v6-alias', comment: '' });
    assert.equal(hostsText(document), text + '\n2001:db8::2\tv6.local v6-alias');
    removeHostsEntry(document, 1);
    assert.equal(hostsText(document), text + '\n');
});
test('UI and text views reject invalid addresses and host names without dropping unfamiliar text', () =>
{
    for (const text of ['bad line', '192.0.2.1 bad/name', '192.0.2.1 a,b', '192.0.2.999 app.local', '192.0.2.1', '127.0.0.1 foo\0'])
    {
        assert.equal(hostsText(parseHosts(text)), text);
        assert.throws(() => validateHostsText(text));
    }
    assert.equal(validateHostsText('# arbitrary comment\n\n::1 localhost _alias\n'), '# arbitrary comment\n\n::1 localhost _alias\n');
    const document = parseHosts('127.0.0.1 localhost\n');
    assert.throws(() => setHostsEntry(document, 0, { enabled: true, address: '::1', names: 'bad/name', comment: '' }));
    assert.equal(hostsText(document), '127.0.0.1 localhost\n');
});
