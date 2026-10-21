/** 指定されたアイコンのSVG要素を作成します。
 * @returns 呼出し元が使用する値です。
 */
export function uiIcon(name)
{
    const paths = { network: 'M8 3h8v6H8z M3 17h6v4H3z M15 17h6v4h-6z M12 9v4 M6 17v-4h12v4', shield: 'M12 2l8 3v6c0 5-4 8-8 11-4-3-8-6-8-11V5z M8 12l3 3 5-6', code: 'M8 6l-6 6 6 6 M16 6l6 6-6 6 M14 3l-4 18', history: 'M3 5v6h6 M3 11a9 9 0 1 1 2 8 M12 7v6l4 2', search: 'M10 3a7 7 0 1 0 0 14 7 7 0 0 0 0-14 M15 15l6 6', plus: 'M12 4v16 M4 12h16', close: 'M5 5l14 14 M19 5L5 19', check: 'M4 12l5 5L20 6', trash: 'M3 6h18 M8 6V3h8v3 M5 6l1 15h12l1-15 M10 10v7 M14 10v7', up: 'M5 15l7-7 7 7', down: 'M5 9l7 7 7-7', folder: 'M3 7V4h6l2 3h10v13H3z', file: 'M6 3h8l4 4v14H6z M14 3v5h4',
        drive: 'M5 4h14l3 12v4H2v-4z M2 16h20 M6 18h1 M10 18h1', monitor: 'M3 4h18v13H3z M8 21h8 M12 17v4',
        keyboard: 'M2 6h20v12H2z M5 9h1 M9 9h1 M13 9h1 M17 9h2 M5 12h1 M9 12h1 M13 12h1 M17 12h2 M6 15h12',
        settings: 'M4 6h16 M4 12h16 M4 18h16 M8 3v6 M16 9v6 M10 15v6', info: 'M12 10v7 M12 6v1 M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20', user: 'M16 7a4 4 0 1 1-8 0a4 4 0 1 1 8 0 M4 21v-3a8 8 0 0 1 16 0v3',
        statistics: 'M4 20V11h4v9z M10 20V4h4v16z M16 20V8h4v12z',
        play: 'M8 4l12 8-12 8z', stop: 'M6 6h12v12H6z', refresh: 'M20 10a8 8 0 1 0-2 8 M20 4v6h-6', power: 'M12 2v10 M6 5a9 9 0 1 0 12 0',
        copy: 'M8 8h12v13H8z M16 8V3H3v13h5', clipboard: 'M9 4H5v17h14V4h-4 M9 2h6v5H9z',
        volume: 'M3 9h4l5-4v14l-5-4H3z M16 8a6 6 0 0 1 0 8 M19 5a10 10 0 0 1 0 14',
        'volume-off': 'M3 9h4l5-4v14l-5-4H3z M16 9l6 6 M22 9l-6 6',
        'one-to-one': 'M3 8l3-2v12 M3 18h6 M12 9v1 M12 14v1 M15 8l3-2v12 M15 18h6',
        'follow-pointer': 'M12 2v5 M9 5l3-3 3 3 M22 12h-5 M19 9l3 3-3 3 M12 22v-5 M9 19l3 3 3-3 M2 12h5 M5 9l-3 3 3 3 M10 10h4v4h-4z',
        pointer: 'M5 3l14 9-7 1-3 7z M12 13l5 8',
        'view-only': 'M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0',
        fullscreen: 'M9 3H3v6 M15 3h6v6 M21 15v6h-6 M9 21H3v-6',
        'video-fullscreen': 'M3 8V3h5 M16 3h5v5 M21 16v5h-5 M8 21H3v-5 M7 7h10v10H7z',
        windows: 'M3 4l8-1v8H3z M14 3l7-1v9h-7z M3 14h8v8l-8-1z M14 14h7v9l-7-1z' };
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    for (const [key, value] of Object.entries({ viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', 'stroke-width': '1.6', 'aria-hidden': 'true', class: 'ui-icon' }))
    {
        svg.setAttribute(key, value);
    }
    const path = document.createElementNS(svg.namespaceURI, 'path');
    path.setAttribute('d', paths[name] || paths.file);
    svg.append(path);
    return svg;
}
