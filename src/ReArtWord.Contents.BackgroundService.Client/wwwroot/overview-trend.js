// Own the series and its offset together so a render never paints a new path with an old origin.
const charts = new Map();
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
const epoch = Date.now() - performance.now();
let frame;
let lastDraw = -Infinity;

/** 線幅の15倍を影の減衰範囲とし、カードの高さにかかわらず同じ画面上の幅にします。 */
function resizeShadow(chart, state)
{
    const { width, height } = chart.getBoundingClientRect();
    if (height <= 0 || width <= 0 || !state.blur || !state.layers.length)
    {
        return;
    }
    const lineWidth = Number.parseFloat(getComputedStyle(state.line).strokeWidth);
    const shadowDepth = lineWidth * 15;
    const depth = shadowDepth / state.layers.length * chart.viewBox.baseVal.height / height;

    // 細い帯の境界だけを半画素程度なじませ、線の傾きによって影の濃さを変えません。
    state.blur.setAttribute("stdDeviation", `${0.6 * chart.viewBox.baseVal.width / width} ${0.6 * chart.viewBox.baseVal.height / height}`);
    if (depth !== state.depth)
    {
        state.depth = depth;
        state.layers.forEach((layer, index) => layer.setAttribute("transform", `translate(0 ${index * depth})`));
        if (state.path !== undefined)
        {
            state.band?.setAttribute("d", shadowGeometry(state.path, depth, state.bottom).band);
        }
    }
}

/** 対象の配置位置を計算して反映します。 */
function position(state, now)
{
    // A peer clock can lead this browser. A signed offset keeps existing points fixed at a rebase.
    const seconds = (0) < (state.origin) && !reducedMotion.matches ? (epoch + now - state.origin) / 1000 : 0;
    state.series.setAttribute("transform", `translate(${-seconds} 0)`);
}

/** 重複した描画要求を次の一回にまとめます。 */
function schedule()
{
    if (frame === undefined && charts.size && !reducedMotion.matches)
    {
        frame = requestAnimationFrame(draw);
    }
}

/** 準備された値を現在の描画領域へ反映します。 */
function draw()
{
    const now = performance.now();
    if (now - lastDraw < 33)
    {
        frame = requestAnimationFrame(draw);
        return;
    }
    lastDraw = now;
    frame = undefined;
    for (const [chart, state] of charts)
    {
        if (!chart.isConnected)
        {
            state.resize.disconnect();
            charts.delete(chart);
            continue;
        }
        if (chart.closest("[hidden]"))
        {
            continue;
        }
        position(state, now);
    }
    schedule();
}

/** 点を結ぶ線全体から下向きに一定幅の帯を作り、実際の欠測区間だけを分離します。
 * @returns 連続する線の帯と、線から表示底面までの切抜きです。
 */
function shadowGeometry(path, depth, bottom)
{
    const bands = [];
    const clips = [];
    for (const segment of path.split("M"))
    {
        const points = Array.from(segment.matchAll(/(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)/g));
        if (points.length < 2)
        {
            continue;
        }
        let band = `M${segment} `;
        for (let index = points.length - 1; 0 <= index; index--)
        {
            band += `L${points[index][1]},${Number(points[index][2]) + depth} `;
        }
        bands.push(`${band}Z`);
        clips.push(`M${segment} L${points.at(-1)[1]},${bottom} L${points[0][1]},${bottom} Z`);
    }
    return { band: bands.join(" "), clip: clips.join(" ") };
}

/** 現在の設定と状態から表示を更新します。 */
export function update(chart, path, origin)
{
    if (!chart?.isConnected)
    {
        return;
    }
    let state = charts.get(chart);
    if (!state)
    {
        const series = chart.querySelector("[data-series]");
        state = { series, line: series.querySelector("path"), blur: chart.querySelector("[data-shadow-blur]"),
            band: chart.querySelector("[data-shadow-band]"), layers: Array.from(chart.querySelectorAll("[data-shadow-layer]")), depth: 0,
            clip: chart.querySelector("[data-area-clip]"), resize: new ResizeObserver(() => resizeShadow(chart, state)),
            bottom: chart.viewBox.baseVal.y + chart.viewBox.baseVal.height, path: undefined, origin: 0 };
        charts.set(chart, state);
        state.resize.observe(chart);
        resizeShadow(chart, state);
    }
    state.origin = Number(origin) || 0;
    if (state.path !== path)
    {
        state.line.setAttribute("d", path);
        const area = shadowGeometry(path, state.depth, state.bottom);
        state.band?.setAttribute("d", area.band);
        state.clip?.setAttribute("d", area.clip);
        state.path = path;
    }
    position(state, performance.now());
    schedule();
}

/** 対象の監視を解除します。 */
export function unobserve(chart)
{
    charts.get(chart)?.resize.disconnect();
    charts.delete(chart);
    for (const [element, state] of charts)
    {
        if (!element.isConnected)
        {
            state.resize.disconnect();
            charts.delete(element);
        }
    }
    if (!charts.size && frame !== undefined)
    {
        cancelAnimationFrame(frame);
        frame = undefined;
    }
}

reducedMotion.addEventListener("change", () =>
{
    if (frame !== undefined)
    {
        cancelAnimationFrame(frame);
        frame = undefined;
    }
    const now = performance.now();
    for (const state of charts.values())
    {
        position(state, now);
    }
    schedule();
});
