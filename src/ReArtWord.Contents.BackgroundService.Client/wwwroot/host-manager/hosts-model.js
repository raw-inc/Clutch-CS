import { hasControlCharacters } from './input-validation.js';
import { addressBytes } from './network-model.js';

/** hostsのIPアドレス・名前・コメントを検証します。
 * @returns namesを含む結果です。
 */
export function validateHostsEntry(entry)
{
    try
    {
        addressBytes(entry.address, entry.address.includes(':') ? 6 : 4);
    }
    catch
    {
        throw new Error('Enter a valid IP address and at least one host name.');
    }
    const names = entry.names.trim().split(/[\s,;]+/).filter(Boolean);
    if (!names.length || names.some(name => !/^[a-z0-9._-]{1,253}$/i.test(name) || !/[a-z0-9]/i.test(name)) || hasControlCharacters(entry.comment))
    {
        throw new Error('Enter a valid IP address and at least one host name.');
    }
    return { ...entry, names: names.join(' ') };
}

/** コメントと改行を保ってhostsの行を解析します。
 * @returns lines・newlineを含む結果です。
 */
export function parseHosts(text)
{
    const lines = (text.match(/[^\r\n]*(?:\r\n|\r|\n|$)/g) ?? []).filter(Boolean).map(value =>
    {
        const ending = value.match(/(?:\r\n|\r|\n)$/)?.[0] ?? '', raw = ending ? value.slice(0, -ending.length) : value;
        const match = raw.match(/^([ \t]*)(#[ \t]*)?(\S+)[ \t]+([^#]*?)(?:[ \t]*#(.*))?$/);
        let entry = null;
        if (match)
        {
            try
            {
                if (/[;,]/.test(match[4]))
                {
                    throw new Error('Unrecognized hosts line');
                }
                entry = validateHostsEntry({ enabled: !match[2], address: match[3], names: match[4].trim(), comment: (match[5] ?? '').trim() });
            }
            catch
            { /* 解釈できない行とコメントは、元のテキストのまま保持します。 */ }
        }
        return { raw, ending, indent: match?.[1] ?? '', entry };
    });
    return { lines, newline: lines.find(line => line.ending)?.ending ?? '\n' };
}

/** 保持した各行からhostsの本文を復元します。
 * @returns 呼出し元が使用する値です。
 */
export function hostsText(document)
{
    return document.lines.map(line => line.raw + line.ending).join('');
}

/** 指定したhosts項目だけを書き換えます。 */
export function setHostsEntry(document, index, value)
{
    const entry = validateHostsEntry(value), previous = document.lines[index];
    if (previous?.entry && ['enabled', 'address', 'names', 'comment'].every(key => previous.entry[key] === entry[key]))
    {
        return;
    }
    const raw = (previous?.indent ?? '') + (entry.enabled ? '' : '# ') + entry.address + '\t' + entry.names + (entry.comment ? '\t# ' + entry.comment : '');
    if (previous)
    {
        previous.raw = raw;
        previous.entry = entry;
        return;
    }
    const last = document.lines.at(-1), ending = last && !last.ending ? '' : document.newline;
    if (last && !last.ending)
    {
        last.ending = document.newline;
    }
    document.lines.push({ raw, ending, indent: '', entry });
}

/** 選択したhosts項目を編集内容から除きます。 */
export function removeHostsEntry(document, index)
{
    document.lines.splice(index, 1);
}

/** hosts本文のサイズと各行の形式を検証します。
 * @returns 呼出し元が使用する値です。
 */
export function validateHostsText(text)
{
    if ((131072) < (new TextEncoder().encode(text).length) || hasControlCharacters(text, { allowWhitespace: true, includeDelete: true }))
    {
        throw new Error('Validation failed. Check the input values.');
    }
    const document = parseHosts(text);
    if (document.lines.some(line => line.raw.trim() && !line.raw.trim().startsWith('#') && !line.entry))
    {
        throw new Error('Enter a valid IP address and at least one host name.');
    }
    return text;
}
