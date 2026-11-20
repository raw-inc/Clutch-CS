// 起動時に図柄を読み込み、ダイアログを開いたときに取得を待たずに表示します。
const response = await fetch('/icons/controls.svg', { signal: AbortSignal.timeout(10_000) }).catch(() => null);
if (response?.ok)
{
    const source = new DOMParser().parseFromString(await response.text(), 'image/svg+xml');
    const sprite = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    sprite.style.display = 'none';
    sprite.setAttribute('aria-hidden', 'true');
    for (const symbol of source.querySelectorAll('symbol[id]'))
    {
        symbol.id = 'clutch-icon-' + symbol.id;
        sprite.append(symbol);
    }
    document.body.prepend(sprite);
}
