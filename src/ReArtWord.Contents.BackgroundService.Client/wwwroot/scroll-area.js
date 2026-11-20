/** スクロール領域に、画面外に続く方向を表示します。 */
class ScrollArea extends HTMLElement
{
    #frame = 0;
    #resize = new ResizeObserver(() => this.#schedule());
    #content = new MutationObserver(() => this.#schedule());

    #onScroll = () => this.#schedule();

    /** スクロール位置と内容の変化から、続きがある端だけを示す。 */
    connectedCallback()
    {
        this.addEventListener('scroll', this.#onScroll, true);
        this.#resize.observe(this);
        this.#content.observe(this, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['hidden', 'open'] });
        this.#schedule();
    }

    /** 閉じた領域の測定を解除する。 */
    disconnectedCallback()
    {
        this.removeEventListener('scroll', this.#onScroll, true);
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
            const port = this.firstElementChild;
            const inline = this.dataset.scrollAxis === 'inline';
            const size = inline ? port?.clientWidth : port?.clientHeight;
            const scrollable = (inline ? port?.scrollWidth : port?.scrollHeight) ?? 0;
            const position = (inline ? Math.abs(port?.scrollLeft ?? 0) : port?.scrollTop) ?? 0;
            this.toggleAttribute('data-scroll-before', 0 < size && 1 < position);
            this.toggleAttribute('data-scroll-after', 0 < size && 1 < scrollable - size - position);
        });
    }
}

customElements.define('clutch-scroll-area', ScrollArea);
