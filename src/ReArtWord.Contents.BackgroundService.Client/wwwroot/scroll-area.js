/** スクロール領域に、画面外に続く方向を表示します。 */
class ScrollArea extends HTMLElement
{
    #frame = 0;
    #resize = new ResizeObserver(() => this.#schedule());
    #content = new MutationObserver(() => this.#schedule());

    #onScroll = () => this.#schedule();

    /** 矢印を押したときは、直前に表示されていたタブが少し残る幅だけ送る。 */
    #onClick = (event) =>
    {
        const control = event.target.closest('button[data-scroll-control]');
        const port = this.firstElementChild;
        if (control?.parentElement !== this || !port)
        {
            return;
        }

        const towardStart = control.dataset.scrollControl === 'before';
        const direction = towardStart ? -1 : 1;
        port.scrollBy({ left: direction * Math.max(32, port.clientWidth - 32) });
    };

    /** スクロール位置と内容の変化から、続きがある端だけを示す。 */
    connectedCallback()
    {
        this.addEventListener('scroll', this.#onScroll, true);
        if (this.hasAttribute('data-scroll-controls'))
        {
            this.addEventListener('click', this.#onClick);
        }
        this.#resize.observe(this);
        const combinedAxis = 'both';
        if (this.dataset.scrollAxis === combinedAxis)
        {
            const header = this.querySelector('thead');
            if (header)
            {
                this.#resize.observe(header);
            }
        }
        this.#content.observe(this, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['hidden', 'open'] });
        this.#schedule();
    }

    /** 閉じた領域の測定を解除する。 */
    disconnectedCallback()
    {
        this.removeEventListener('scroll', this.#onScroll, true);
        this.removeEventListener('click', this.#onClick);
        this.#resize.disconnect();
        this.#content.disconnect();
        cancelAnimationFrame(this.#frame);
        this.#frame = 0;
    }

    /** 同じフレーム内の変化をまとめ、端の影をスクロール量に合わせる。 */
    #schedule()
    {
        if (!this.isConnected || this.#frame)
        {
            return;
        }
        this.#frame = requestAnimationFrame(() =>
        {
            this.#frame = 0;
            const selector = this.dataset.scrollPort;
            const port = selector ? this.querySelector(selector) : this.firstElementChild;
            if (!port)
            {
                return;
            }

            this.#update(port);
        });
    }

    /** 両方向の表では固定見出しを避け、データ行に続きがあることを示す。 */
    #update(port)
    {
        const axis = this.dataset.scrollAxis;
        if (this.hasAttribute('data-scroll-controls'))
        {
            this.toggleAttribute('data-scroll-overflow', 1 < port.scrollWidth - this.clientWidth);
        }

        const inlineAxis = 'inline', combinedAxis = 'both';
        const inline = axis === inlineAxis;
        this.#setEdges(port, inline, 'data-scroll-');
        if (axis === combinedAxis)
        {
            this.#ensureInlineEdges();
            const header = port.querySelector('thead');
            this.style.setProperty('--scroll-edge-block-start', (header?.offsetHeight ?? 0) + 'px');
            this.#setEdges(port, true, 'data-scroll-inline-');
        }
    }

    /** 縦横に送れる領域では、上下の影に加えて左右の続きも表示する。 */
    #ensureInlineEdges()
    {
        if (this.querySelector(':scope > .scroll-inline-edge'))
        {
            return;
        }
        for (const edge of ['before', 'after'])
        {
            const indicator = document.createElement('span');
            indicator.className = 'scroll-inline-edge scroll-inline-' + edge;
            indicator.setAttribute('aria-hidden', 'true');
            this.append(indicator);
        }
    }

    /** スクロール位置から、各方向へ進めるかどうかを更新する。 */
    #setEdges(port, inline, prefix)
    {
        const size = inline ? port.clientWidth : port.clientHeight;
        const scrollable = inline ? port.scrollWidth : port.scrollHeight;
        const position = inline ? Math.abs(port.scrollLeft) : port.scrollTop;
        const before = 0 < size && 1 < position;
        const after = 0 < size && 1 < scrollable - size - position;
        this.toggleAttribute(prefix + 'before', before);
        this.toggleAttribute(prefix + 'after', after);
        if (!this.hasAttribute('data-scroll-controls'))
        {
            return;
        }
        for (const control of this.querySelectorAll(':scope > button[data-scroll-control]'))
        {
            const towardStart = control.dataset.scrollControl === 'before';
            control.disabled = !(towardStart ? before : after);
        }
    }
}

customElements.define('clutch-scroll-area', ScrollArea);
