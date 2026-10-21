const quote = '"', escape = '\\', open = '(', close = ')', andOperator = 'AND', orOperator = 'OR', termKind = 'term';

/** 一つの検索語を、引用符内の空白や予約語を保って読み取ります。
 * @returns 次の位置と検索語です。
 */
function readTerm(query, index)
{
    const quoted = query[index] === quote;
    let value = '', closed = false;
    if (quoted)
    {
        index++;
    }
    while (index < query.length)
    {
        const character = query[index];
        if (quoted && character === quote)
        {
            index++;
            closed = true;
            break;
        }
        if (!quoted && (character === open || character === close || /\s/u.test(character)))
        {
            break;
        }
        if (quoted && character === escape && (query[index + 1] === quote || query[index + 1] === escape))
        {
            index++;
        }
        value += query[index++];
    }
    if (quoted && !closed)
    {
        throw new Error('Close the quoted search text.');
    }
    return { index, value, quoted };
}

/** 引用符、括弧、AND/ORを有界な字句列へ分解します。
 * @returns 検索語と演算子です。
 */
function tokenize(query)
{
    if ((2048) < (query.length))
    {
        throw new Error('Search is limited to 2048 characters.');
    }
    const tokens = [];
    for (let index = 0; index < query.length;)
    {
        if (/\s/u.test(query[index]))
        {
            index++;
            continue;
        }
        if (query[index] === open || query[index] === close)
        {
            tokens.push({ kind: query[index++] });
            continue;
        }
        const start = index, parsed = readTerm(query, index);
        const { value, quoted } = parsed;
        index = parsed.index;
        if (!value)
        {
            throw new Error('Search text cannot be empty.');
        }
        const upper = value.toUpperCase(), separated = (start === 0 || /\s/u.test(query[start - 1])) && (index === query.length || /\s/u.test(query[index]));
        tokens.push({ kind: !quoted && separated && ['AND', 'OR'].includes(upper) ? upper : 'term', value: value.toLowerCase() });
        if ((256) < (tokens.length))
        {
            throw new Error('Search has too many terms.');
        }
    }
    return tokens;
}

/** 正規表現やevalを使わず、ANDをORより優先して検索式を組み立てます。
 * @returns 通常検索と取得側の検索で共有する式です。
 */
export function parseScopeSearch(query)
{
    const tokens = tokenize(query.trim());
    let position = 0;

    /** 一つの語または括弧を解釈します。
     * @returns 語または括弧の式です。
     */
    function term(depth)
    {
        if ((32) < (depth))
        {
            throw new Error('Search parentheses are nested too deeply.');
        }
        const token = tokens[position++];
        if (token?.kind === termKind)
        {
            return { term: token.value };
        }
        if (token?.kind === open)
        {
            const match = expression(depth + 1);
            if (tokens[position++]?.kind !== close)
            {
                throw new Error('Close the search parentheses.');
            }
            return match;
        }
        throw new Error('A search term is required here.');
    }

    /** 空白で連ねた語もANDとして扱います。
     * @returns 全条件の式です。
     */
    function conjunction(depth)
    {
        const terms = [term(depth)];
        while (tokens[position] && tokens[position].kind !== close && tokens[position].kind !== orOperator)
        {
            if (tokens[position].kind === andOperator)
            {
                position++;
            }
            terms.push(term(depth));
        }
        return combine('and', terms);
    }

    /** ORで区切った条件を連結します。
     * @returns いずれかの条件の式です。
     */
    function expression(depth)
    {
        const alternatives = [conjunction(depth)];
        while (tokens[position]?.kind === orOperator)
        {
            position++;
            alternatives.push(conjunction(depth));
        }
        return combine('or', alternatives);
    }

    if (!tokens.length)
    {
        return { term: '' };
    }
    const match = expression(0);
    if (position !== tokens.length)
    {
        throw new Error('Unexpected search parenthesis.');
    }
    return match;
}

/** 長い条件列も平衡化し、取得側へ深い式を送りません。
 * @returns 有界な二分木です。
 */
function combine(op, terms)
{
    if (terms.length === 1)
    {
        return terms[0];
    }
    const middle = terms.length >>> 1;
    return { op, left: combine(op, terms.slice(0, middle)), right: combine(op, terms.slice(middle)) };
}

/** 一覧用の小文字索引だけを、ネットワーク待ちなく判定します。
 * @returns 通常検索の判定関数です。
 */
export function compileScopeSearch(query)
{
    /** 解析済みの式を一度だけ関数へ変換します。
     * @returns 語・論理式の判定です。
     */
    function compile(expression)
    {
        if (expression.term !== undefined)
        {
            return text => text.includes(expression.term);
        }
        const left = compile(expression.left), right = compile(expression.right), and = 'and';
        return expression.op === and ? text => left(text) && right(text) : text => left(text) || right(text);
    }

    return compile(parseScopeSearch(query));
}
