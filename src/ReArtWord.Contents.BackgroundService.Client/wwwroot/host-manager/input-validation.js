/** 許可していないASCII制御文字が入力に含まれるか確認します。
 * @returns 対象範囲の制御文字を含む場合はtrueです。
 */
export function hasControlCharacters(value, { allowWhitespace = false, includeDelete = false } = {})
{
    for (const character of String(value))
    {
        const code = character.charCodeAt(0);
        if (allowWhitespace && (code === 9 || code === 10 || code === 13))
        {
            continue;
        }
        if (code < 32 || (includeDelete && code === 127))
        {
            return true;
        }
    }
    return false;
}
