/** 利用しているライブラリーの通知とライセンスを表示します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function licensesView(root, ctx)
{
    root.className = 'license-list';
    const description = document.createElement('p');
    description.className = 'page-description';
    description.textContent = ctx.t('Open-source components used by this application. Copyright and license terms belong to their respective authors. FFmpeg is supplied separately by the administrator; its build-specific license and codec notices are provided with that distribution.', '本アプリケーションが使用するオープンソースの構成要素です。各著作権およびライセンス条件は各権利者に帰属します。FFmpeg は管理者が別途用意するため、そのビルドのライセンスとコーデックに関する表記は配布元の資料をご確認ください。');
    root.append(description);
    const entries = await ctx.api('/licenses/notices.json');
    if (ctx.signal.aborted)
    {
        return {
            /** 所有する監視・接続・表示資源を解放します。 */
            dispose()
            {} };
    }
    entries.sort((left, right) => left.name.localeCompare(right.name, 'en', { sensitivity: 'base' }) || left.version.localeCompare(right.version, 'en', { numeric: true }));
    for (const entry of entries)
    {
        const section = document.createElement('section'), title = document.createElement('h2'), meta = document.createElement('p');
        title.textContent = entry.name;
        meta.className = 'small muted';
        meta.textContent = `${entry.version} · ${entry.license} · ${entry.authors}`;
        section.append(title, meta);
        if (entry.selectedLicense)
        {
            appendLicenseSelection(section, entry, ctx);
        }
        for (const selection of entry.licenseSelections ?? [])
        {
            appendLicenseSelection(section, selection, ctx, selection.name);
        }
        if (entry.copyright)
        {
            const copyright = document.createElement('p');
            copyright.textContent = entry.copyright;
            section.append(copyright);
        }
        if (entry.project)
        {
            const link = document.createElement('a');
            link.href = entry.project;
            link.textContent = ctx.t('Project website', 'プロジェクトサイト');
            link.target = '_blank';
            link.rel = 'noopener noreferrer';
            section.append(link);
        }
        for (const notice of entry.notices)
        {
            const details = document.createElement('details'), summary = document.createElement('summary'), text = document.createElement('pre');
            summary.textContent = notice.label;
            details.append(summary, text);
            section.append(details);
            let loaded = false;

            /** 開いた通知本文を取得し、取消し後には表示を更新しません。
             * @returns 通知本文の取得と表示の完了です。
             */
            async function loadNoticeAsync()
            {
                if (!details.open || loaded)
                {
                    return;
                }
                loaded = true;
                try
                {
                    const response = await fetch(notice.href, { signal: ctx.signal });
                    if (!response.ok)
                    {
                        throw new Error('License could not be loaded.');
                    }
                    text.textContent = await response.text();
                }
                catch(error)
                {
                    loaded = false;
                    if (!ctx.signal.aborted)
                    {
                        text.textContent = error.message;
                    }
                }
            }

            details.addEventListener('toggle', () =>
            {
                loadNoticeAsync().catch(error =>
                {
                    if (!ctx.signal.aborted)
                    {
                        text.textContent = error.message;
                    }
                });
            }, { signal: ctx.signal });
        }
        root.append(section);
    }
    return {
        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {} };
}

/** 複数のライセンスから採用した条件を表示します。 */
function appendLicenseSelection(section, selection, ctx, component = '')
{
    const text = document.createElement('p');
    const prefix = component ? `${component}: ` : '';
    text.textContent = `${prefix}${ctx.t('Selected license', '採用ライセンス')}: ${selection.selectedLicense}`;
    if (component)
    {
        text.textContent += ` (${selection.license})`;
    }
    section.append(text);
}
