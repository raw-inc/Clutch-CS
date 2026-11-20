/** 同じ行のカードだけを比較する。極端に長いカードの隣に大きな空白を作らない。 */
class CardRows extends HTMLElement
{
    #frame = 0;
    #observed = new Set();
    #resize = new ResizeObserver(() => this.#schedule());
    #changes = new MutationObserver(() => this.#schedule());

    /** 挿入されたカードや、開閉・折り返しによる高さの変化を監視する。 */
    connectedCallback()
    {
        this.#changes.observe(this, { subtree: true, childList: true, attributes: true, attributeFilter: ['open', 'hidden'] });
        this.#schedule();
    }

    /** 他の画面へ移った後は測定を続けない。 */
    disconnectedCallback()
    {
        this.#changes.disconnect();
        this.#resize.disconnect();
        this.#observed.clear();
        cancelAnimationFrame(this.#frame);
        this.#frame = 0;
    }

    /** 同じ描画フレームの変更はまとめて反映する。 */
    #schedule()
    {
        if (this.isConnected && !this.#frame)
        {
            this.#frame = requestAnimationFrame(() =>
            {
                this.#frame = 0;
                this.#align();
            });
        }
    }

    /** カード自体が伸びたままでも本文の折り返しを検出できるよう、直下の内容も監視する。 */
    #watch(grid, cards)
    {
        const current = new Set([grid]);
        for (const card of cards)
        {
            current.add(card);
            for (const element of card.children)
            {
                current.add(element);
            }
        }
        for (const element of this.#observed)
        {
            if (!current.has(element))
            {
                this.#resize.unobserve(element);
            }
        }
        for (const element of current)
        {
            if (!this.#observed.has(element))
            {
                this.#resize.observe(element);
            }
        }
        this.#observed = current;
    }

    /** 自然な高さをまとめて読んでから、同じ行の要素へだけ結果を反映する。 */
    #align()
    {
        const grid = this.firstElementChild;
        if (!grid)
        {
            return;
        }
        const cards = [...grid.children];
        this.#watch(grid, cards);
        this.setAttribute('data-measuring', '');
        const rows = new Map();
        for (const card of cards)
        {
            const box = card.getBoundingClientRect();
            if (!box.width || !box.height)
            {
                continue;
            }
            const top = box.top;
            const row = rows.get(top) ?? [];
            const header = card.querySelector(':scope > [data-card-header]');
            const inset = card.querySelector(':scope > [data-card-inset]');
            const caption = inset && card.querySelector('[data-card-caption]');
            const lines = caption ? caption.getBoundingClientRect().height / parseFloat(getComputedStyle(caption).lineHeight) : Infinity;
            row.push({ card, height: box.height, header, headerHeight: header?.getBoundingClientRect().height ?? 0, inset, detail: inset?.getBoundingClientRect(), lines });
            rows.set(top, row);
        }
        for (const row of rows.values())
        {
            alignRow(row, this.hasAttribute('data-balance-short-cards'));
        }
        this.removeAttribute('data-measuring');
    }
}

/** 入れ子の上端は、本文を動かさず、内側の面の直前に余白を足して揃える。 */
function alignRow(row, balanceShortCards)
{
    const heights = row.map(item => item.height);
    const stretch = 1 < row.length && Math.max(...heights) < 2 * Math.min(...heights);
    const shortest = Math.min(...heights);
    const shorterHeight = balanceShortCards && !stretch ? Math.max(...heights.filter(height => height < 2 * shortest)) : 0;

    // 一部のカードに警告が出ても、同じ行のグラフを同じ高さから比較できるようにする。
    const headerHeight = stretch ? Math.max(...row.map(item => item.headerHeight)) : 0;
    const details = row.map(item => item.detail).filter(box => box?.height);
    const sizes = details.map(box => box.height);
    const alignInsets = 1 < row.length && details.length === row.length
        && (Math.max(...sizes) < 1.5 * Math.min(...sizes) || row.every(item => item.lines < 4));
    const top = alignInsets ? Math.max(...details.map(box => box.top)) : 0;
    for (const item of row)
    {
        item.card.toggleAttribute('data-stretch', stretch);
        item.header?.style.setProperty('--card-header-height', headerHeight + 'px');
        if (balanceShortCards)
        {
            item.card.style.setProperty('--comparable-card-height', (item.height <= shorterHeight ? shorterHeight : 0) + 'px');
        }
        if (item.inset)
        {
            const offset = alignInsets ? top - item.detail.top : 0;
            item.inset.style.setProperty('--card-inset-offset', offset + 'px');
        }
    }
}

customElements.define('clutch-card-rows', CardRows);
