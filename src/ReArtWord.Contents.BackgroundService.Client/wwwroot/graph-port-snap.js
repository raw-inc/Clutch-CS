/** 一回の配線操作で、近いポートの位置と読取り専用の接続可否を保持します。
 * @returns 候補表示・ドロップ確認・破棄の操作です。
 */
export function createPortSnap(svg, origin, canConnectAsync, changed)
{
    const maximumMargin = 6;
    const checks = new Map();
    let geometry = null, highlighted = null, disposed = false;

    /** 反対側かつ別ノードの表示ポートだけを候補にします。
     * @returns 起点とは異なる向きのポートかです。
     */
    const eligible = port => port && svg.contains(port) && port.dataset.wireNode !== origin.node
        && (port.dataset.wireOutput === 'true') !== origin.output;

    /** ポート円の位置をスクロール前の画面座標で保持します。
     * @returns 円の中心と画面上の半径です。
     */
    const measure = port =>
    {
        const circle = port.querySelector('circle'), rect = circle?.getBoundingClientRect();
        if (!rect || rect.width <= 0 || rect.height <= 0)
        {
            return null;
        }
        const radius = Math.min(rect.width, rect.height) / 2;
        const margin = maximumMargin * Math.min(1, radius / circle.r.baseVal.value);
        return { port, circle, x: rect.left + rect.width / 2 + svg.parentElement.scrollLeft,
            y: rect.top + rect.height / 2 + svg.parentElement.scrollTop, radius, margin };
    };

    /** 現在のスクロール位置で円の中心を返します。
     * @returns ブラウザーの画面座標です。
     */
    const center = candidate => ({ x: candidate.x - svg.parentElement.scrollLeft, y: candidate.y - svg.parentElement.scrollTop });

    /** 他の画面や重なるノードの裏側には吸着させません。
     * @returns 円の中心が実際に見えているかです。
     */
    const exposed = candidate =>
    {
        const position = center(candidate);
        return document.elementFromPoint(position.x, position.y)?.closest?.('[data-wire-port]') === candidate.port;
    };

    /** 縮小に比例して余裕を狭め、拡大時も外側6pxまでで曖昧な候補には吸着しません。
     * @returns 一意に近い表示ポート、または候補なしです。
     */
    const nearest = (x, y) =>
    {
        const hit = document.elementFromPoint(x, y);
        if (disposed || !hit || !svg.contains(hit))
        {
            return null;
        }
        const direct = hit.closest?.('[data-wire-port]');
        if (direct)
        {
            const rect = direct.getBoundingClientRect();
            if (eligible(direct) && rect.left <= x && x <= rect.left + rect.width && rect.top <= y && y <= rect.top + rect.height)
            {
                return measure(direct);
            }
            return null;
        }
        geometry ??= [...svg.querySelectorAll('[data-wire-port]')].filter(eligible).map(measure).filter(Boolean);
        const px = x + svg.parentElement.scrollLeft, py = y + svg.parentElement.scrollTop;
        let first = null, firstDistance = Infinity, secondDistance = Infinity;
        for (const candidate of geometry)
        {
            const distance = Math.hypot(candidate.x - px, candidate.y - py);
            if ((candidate.radius + candidate.margin) < (distance))
            {
                continue;
            }
            if ((firstDistance) < (distance))
            {
                secondDistance = Math.min(secondDistance, distance);
                continue;
            }
            secondDistance = firstDistance;
            first = candidate;
            firstDistance = distance;
        }
        if (!first || secondDistance - firstDistance <= first.margin / 4 || !svg.contains(first.port) || !exposed(first))
        {
            return null;
        }
        return first;
    };

    /** 調べた候補を繰り返し問い合わせず、遅い結果はこの操作の表示だけへ返します。
     * @returns 接続可能性の確認状態です。
     */
    const check = candidate =>
    {
        let entry = checks.get(candidate.port);
        if (!entry)
        {
            entry = { accepted: false, promise: null };
            checks.set(candidate.port, entry);
            entry.promise = Promise.resolve().then(() => canConnectAsync(candidate.port.dataset.wireNode, candidate.port.dataset.wirePort)).then(accepted =>
            {
                entry.accepted = accepted === true;
                if (!disposed)
                {
                    changed();
                }
                return entry.accepted;
            }).catch(() => false);
        }
        return entry;
    };

    /** 現在の候補だけに控えめな強調を表示します。 */
    const highlight = circle =>
    {
        if (highlighted !== circle)
        {
            highlighted?.classList.remove('wire-snap-target');
            highlighted = circle;
        }
        highlighted?.classList.add('wire-snap-target');
    };

    return {
        /** 確認済みの候補へ描画先を合わせます。
         * @returns 吸着先の画面座標、または未確認です。
         */
        pick(x, y)
        {
            const candidate = nearest(x, y), accepted = candidate && check(candidate).accepted;
            highlight(accepted ? candidate.circle : null);
            return accepted ? center(candidate) : null;
        },

        /** 素早いドロップでも型確認を待ち、取消済みの操作は確定しません。
         * @returns 接続できるポート、または候補なしです。
         */
        async resolveAsync(x, y)
        {
            const candidate = nearest(x, y);
            if (!candidate || !await check(candidate).promise || disposed || !svg.contains(candidate.port))
            {
                return null;
            }
            return candidate.port;
        },

        /** 再描画やウィンドウ寸法の変更後に円の位置を読み直します。 */
        invalidate()
        {
            geometry = null;
            checks.clear();
        },

        /** 候補の強調と保持を破棄し、遅延した確認結果を無視します。 */
        dispose()
        {
            disposed = true;
            highlight(null);
            geometry = null;
            checks.clear();
        }
    };
}
