/** 同じ一覧の状態表示を3文字幅ずつに区切る。長い状態名が混在するときは短いタグまで広げない。 */
class StatusLabels extends HTMLElement
{
    #changes = new MutationObserver(() => this.#schedule());
    #frame = 0;
    #host;
    #size = new ResizeObserver(() => this.#schedule());

    #schedule = () =>
    {
        if (this.isConnected && !this.#frame)
        {
            this.#frame = requestAnimationFrame(() =>
            {
                this.#frame = 0;
                this.#align();
            });
        }
    };

    /** 翻訳や一覧の更新後は、表示している文字の幅で再計算する。 */
    connectedCallback()
    {
        this.#changes.observe(this, { childList: true, characterData: true, subtree: true, attributes: true, attributeFilter: ['hidden'] });
        document.fonts.addEventListener('loadingdone', this.#schedule);
        this.#schedule();
    }

    /** 画面を離れた一覧の監視を終了する。 */
    disconnectedCallback()
    {
        this.#changes.disconnect();
        document.fonts.removeEventListener('loadingdone', this.#schedule);
        this.#size.disconnect();
        this.#host = null;
        cancelAnimationFrame(this.#frame);
        this.#frame = 0;
    }

    /** 隣り合う幅の段階は大きい方に揃え、大きく異なる場合は各ラベルの幅を使う。 */
    #align()
    {
        const host = this.querySelector('table, ul, .hm-profile-grid') ?? this.firstElementChild;
        if (host !== this.#host)
        {
            this.#size.disconnect();
            this.#host = host;
            if (host)
            {
                this.#size.observe(host);
            }
        }
        const tags = [];
        const range = document.createRange();
        for (const label of this.querySelectorAll('[data-status-label]'))
        {
            if (!label.getClientRects().length)
            {
                continue;
            }
            range.selectNodeContents(label);
            const textWidth = range.getBoundingClientRect().width;
            const step = Math.max(1, Math.ceil(textWidth / (3 * parseFloat(getComputedStyle(label).fontSize))));
            tags.push({ label, step });
        }
        const steps = tags.map(tag => tag.step), widest = Math.max(...steps);
        const sameWidth = widest - Math.min(...steps) <= 1;
        for (const { label, step } of tags)
        {
            label.style.setProperty('--status-width', 3 * (sameWidth ? widest : step) + 'em');
        }
    }
}

customElements.define('clutch-status-labels', StatusLabels);
