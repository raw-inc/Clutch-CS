/** Razorと同じ図柄を、JavaScriptで生成する操作ボタンにも使用します。
 * @returns 操作名を持つ要素に添える装飾用アイコンです。
 */
export function uiIcon(name)
{
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', 'ui-icon');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('fill', 'currentColor');
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS(svg.namespaceURI, 'use');
    use.setAttribute('href', '/icons/controls.svg#' + name);
    svg.append(use);
    return svg;
}
