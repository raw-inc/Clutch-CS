import { uiIcon } from './ui-icons.js';

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
    const filter = document.createElement('div'), label = document.createElement('label'), search = document.createElement('input'), count = document.createElement('output');
    filter.className = 'license-filter';
    label.htmlFor = search.id = 'license-query';
    label.textContent = ctx.t('Find a component', 'ライブラリーを検索');
    search.type = 'search';
    search.placeholder = ctx.t('e.g. Microsoft.AspNetCore', '例：Microsoft.AspNetCore');
    const licenseLabel = document.createElement('label'), license = document.createElement('select');
    licenseLabel.htmlFor = license.id = 'license-type';
    licenseLabel.textContent = ctx.t('License', 'ライセンス');
    license.add(new Option(ctx.t('All licenses', 'すべてのライセンス'), ''));
    for (const kind of [...new Set(entries.map(entry => entry.license).filter(Boolean))].sort())
    {
        license.add(new Option(kind, kind));
    }
    filter.append(label, search, licenseLabel, license, count);
    root.append(filter);
    const cards = document.createElement('div');
    cards.className = 'license-cards';
    const columns = document.createElement('div');
    columns.className = 'license-columns';
    columns.setAttribute('aria-hidden', 'true');
    for (const label of [ctx.t('Component', 'ライブラリー'), ctx.t('Version', 'バージョン'), ctx.t('License', 'ライセンス'), ctx.t('Authors', '作者')])
    {
        const cell = document.createElement('strong');
        cell.textContent = label;
        columns.append(cell);
    }
    cards.append(columns);
    root.append(cards);
    const components = [];

    /** 絞り込み後の表示件数を読み上げの対象にも反映します。 */
    function showCount(visible)
    {
        count.textContent = ctx.t(visible + ' of ' + entries.length, entries.length + ' 件中 ' + visible + ' 件');
    }

    showCount(entries.length);

    /** 名前とライセンスの両方に一致する項目を表示します。 */
    function applyFilter()
    {
        const query = search.value.trim().toLowerCase();
        let visible = 0;
        for (const component of components)
        {
            component.section.hidden = !component.name.includes(query) || (!!license.value && component.license !== license.value);
            if (!component.section.hidden)
            {
                visible++;
            }
        }
        showCount(visible);
    }

    search.addEventListener('input', applyFilter, { signal: ctx.signal });
    license.addEventListener('change', applyFilter, { signal: ctx.signal });
    for (const entry of entries)
    {
        const section = document.createElement('section'), title = document.createElement('h2'), meta = document.createElement('div');
        const pieces = entry.name.split('.');
        pieces.forEach((piece, index) =>
        {
            if (index)
            {
                title.append('.', document.createElement('wbr'));
            }
            title.append(piece);
        });
        meta.className = 'license-metadata';
        meta.setAttribute('data-card-caption', '');
        for (const [label, value] of [[ctx.t('Version', 'バージョン'), entry.version], [ctx.t('License', 'ライセンス'), entry.license], [ctx.t('Authors', '作者'), entry.authors]])
        {
            const cell = document.createElement('span');
            cell.dataset.label = label;
            cell.textContent = value;
            meta.append(cell);
        }
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
            link.textContent = ctx.t('Website', 'サイト');
            link.append(uiIcon('external-link'));
            link.setAttribute('aria-label', entry.name + ' · ' + ctx.t('Project website (opens in a new tab)', 'プロジェクトサイトを新しいタブで開く'));
            link.target = '_blank';
            link.rel = 'noopener noreferrer';
            section.append(link);
        }
        const notices = document.createElement('div');
        notices.className = 'license-notices';
        section.append(notices);
        for (const notice of entry.notices)
        {
            const details = document.createElement('details'), summary = document.createElement('summary'), text = document.createElement('pre');
            summary.textContent = notice.label;
            summary.setAttribute('aria-label', entry.name + ' · ' + notice.label);
            details.append(summary, text);
            if (!section.querySelector('details'))
            {
                details.setAttribute('data-card-inset', '');
            }
            notices.append(details);
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
        components.push({ name: entry.name.toLowerCase(), license: entry.license, section });
        cards.append(section);
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
