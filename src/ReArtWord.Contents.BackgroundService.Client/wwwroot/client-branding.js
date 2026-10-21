window.contentsBranding = {
    /** すべての起動画面で一つのfaviconリンクを更新し、複数候補の選択をブラウザーへ委ねません。
     * @contract MainLayoutとui-update.jsから、設定済み画像の同一origin URLを渡します。
     */
    setFavicon(source)
    {
        const path = typeof source === 'string' && source.startsWith('/client/branding/') ? source : '/favicon.svg';
        const icon = document.querySelector('link[rel="icon"]');
        if (icon && icon.getAttribute('href') !== path)
        {
            icon.setAttribute('href', path);
        }
    }
};
