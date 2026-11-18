(() =>
{
    const key = 'clutch.display.theme';
    const system = 'system';
    const themes = ['light', 'dark'];

    /** 配色とブラウザーのタイトルバーを合わせます。端末設定に従うときはCSSのメディア条件に任せます。 */
    function apply(value)
    {
        const explicit = themes.includes(value);
        if (explicit)
        {
            document.documentElement.dataset.theme = value;
        }
        else
        {
            delete document.documentElement.dataset.theme;
        }
        for (const theme of themes)
        {
            const meta = document.querySelector(`meta[name="theme-color"][data-theme="${theme}"]`);
            if (meta)
            {
                meta.media = explicit ? (value === theme ? 'all' : 'not all') : `(prefers-color-scheme: ${theme})`;
            }
        }
        window.dispatchEvent(new Event('clutch-theme-change'));
    }

    window.contentsTheme = {
        /** 設定画面を開いた時点の選択を返します。 */
        get: () => document.documentElement.dataset.theme ?? system,

        /** このタブに反映し、次回起動用の設定を保存します。 */
        set(value)
        {
            apply(value);
            localStorage.setItem(key, themes.includes(value) ? value : system);
        }
    };
    try
    {
        apply(localStorage.getItem(key));
    }
    catch
    {
        // 保存領域が使えないブラウザーでは端末の配色で開きます。
        apply(system);
    }
    window.addEventListener('storage', event =>
    {
        if (event.storageArea === localStorage && (event.key === key || event.key === null))
        {
            apply(event.newValue);
        }
    });
})();
