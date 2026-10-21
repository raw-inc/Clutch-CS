// Read general_profile_tier_level from an Annex B SPS. Only the fixed prefix
// is needed; avoid retaining/copying the full access unit for configuration.
/** HEVCの構成データからブラウザー用のコーデック名を取得します。
 * @returns 呼出し元が使用する値です。
 */
export function hevcCodec(payload)
{
    for (let i = 0; i + 5 < payload.length; i++)
    {
        let start = 0;
        if (payload[i] === 0 && payload[i + 1] === 0 && payload[i + 2] === 1)
        {
            start = i + 3;
        }
        else if (payload[i] === 0 && payload[i + 1] === 0 && payload[i + 2] === 0 && payload[i + 3] === 1)
        {
            start = i + 4;
        }
        if (!start || ((payload[start] >> 1) & 63) !== 33)
        {
            continue;
        }
        const prefix = readProfilePrefix(payload, start);
        if (!prefix)
        {
            return null;
        }
        const space = prefix[1] >> 6, profile = prefix[1] & 31, tier = prefix[1] & 32 ? 'H' : 'L';
        let compatibility = 0;
        for (let bit = 0; bit < 32; bit++)
        {
            if (prefix[2 + (bit >> 3)] & (128 >> (bit & 7)))
            {
                compatibility = (compatibility | (1 << bit)) >>> 0;
            }
        }
        let end = 12;
        while ((6) < (end) && prefix[end - 1] === 0)
        {
            end--;
        }
        const constraints = Array.from(prefix.subarray(6, end), value => value.toString(16).toUpperCase()).join('.');
        return 'hvc1.' + (space ? String.fromCharCode(64 + space) : '') + profile + '.' + compatibility.toString(16).toUpperCase() + '.' + tier + prefix[12] + (constraints ? '.' + constraints : '');
    }
    return null;
}

/** SPSからエミュレーション防止バイトを除き、必要な固定長の接頭部だけを読みます。
 * @returns 完全な接頭部、または途中で終わる場合はnullです。
 */
function readProfilePrefix(payload, start)
{
    const prefix = new Uint8Array(13);
    let length = 0, zeros = 0;
    for (let index = start + 2; index < payload.length && length < prefix.length; index++)
    {
        const value = payload[index];
        if (2 <= zeros && value === 3)
        {
            zeros = 0;
            continue;
        }
        if (2 <= zeros && value <= 1)
        {
            return null;
        }
        prefix[length++] = value;
        zeros = value === 0 ? zeros + 1 : 0;
    }
    return length === prefix.length ? prefix : null;
}
