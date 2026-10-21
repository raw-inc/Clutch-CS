/**
 * 前後の空白、空要素、重複を取り除きます。
 * @param {string[]} values 入力する文字列の配列。
 * @returns {string[]} 最初に現れた順に並ぶ文字列の配列。
 */
export function run(values)
{
    const trimmed = values.map(value => value.trim()).filter(value => 0 < value.length);
    return [...new Set(trimmed)];
}
