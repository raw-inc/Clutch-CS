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
    const searchField = document.createElement('span');
    searchField.className = 'search-field';
    searchField.append(uiIcon('search'), search);
    label.append(searchField);
    licenseLabel.append(license);
    filter.append(label, licenseLabel, count);
    root.append(filter);
    filter.id = 'license-filters';
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
            appendLicenseMetadata(cell, value ?? '');
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
            link.className = 'license-website';
            link.href = entry.project;
            link.textContent = ctx.t('Website', 'サイト');
            link.append(uiIcon('external-link'));
            link.setAttribute('aria-label', entry.name + ' · ' + ctx.t('Project website (opens in a new tab)', 'プロジェクトサイトを新しいタブで開く'));
            link.target = '_blank';
            link.rel = 'noopener noreferrer';
            meta.lastElementChild.append(link);
        }
        if (entry.notices.length)
        {
            appendNotices(section, entry, ctx, components.length);
        }
        components.push({ name: entry.name.toLowerCase(), license: entry.license, section });
        cards.append(section);
    }
    const back = document.createElement('a');
    back.className = 'license-back';
    back.href = '#license-filters';
    back.textContent = ctx.t('Back to filters', '検索・絞り込みへ戻る');
    root.append(back);

    return {
        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {} };
}

/** メールアドレス付きの作者名は、名前とアドレスの間でも折り返せるようにします。 */
function appendLicenseMetadata(cell, value)
{
    for (const part of value.split(/(<[^<>\s]+@[^<>\s]+>)/u))
    {
        if (part.startsWith('<') && part.endsWith('>'))
        {
            const address = document.createElement('span');
            address.className = 'license-author-address';
            address.textContent = part;
            cell.append(address);
        }
        else
        {
            cell.append(part);
        }
    }
}

/** ライブラリーごとに開閉位置を固定し、本文は開いたときに読み込みます。 */
function appendNotices(section, entry, ctx, index)
{
    const toggle = document.createElement('button'), content = document.createElement('div'), actions = document.createElement('div');
    const caption = entry.notices.length === 1 ? noticeCaption(entry.notices[0].label, ctx) : ctx.t('Licenses and notices', 'ライセンス・通知');
    toggle.type = 'button';
    toggle.className = 'license-toggle';
    toggle.textContent = caption;
    toggle.append(uiIcon('chevron'));
    toggle.setAttribute('aria-label', entry.name + ' · ' + caption);
    toggle.setAttribute('aria-expanded', 'false');
    content.id = 'license-notices-' + index;
    content.className = 'license-notices';
    content.hidden = true;
    toggle.setAttribute('aria-controls', content.id);
    actions.className = 'license-notice-actions';
    const close = document.createElement('button');
    close.type = 'button';
    close.textContent = ctx.t('Close notice', '本文を閉じる');
    actions.append(close);
    content.append(actions);
    section.append(toggle, content);

    const notices = entry.notices.map(notice =>
    {
        const title = document.createElement('h3'), text = document.createElement('div'), source = document.createElement('a');
        title.textContent = noticeCaption(notice.label, ctx);
        text.className = 'license-notice-text';
        source.textContent = ctx.t('Original text', '原文を開く');
        source.href = notice.href;
        source.target = '_blank';
        source.rel = 'noopener noreferrer';
        const noticeBody = document.createElement('div');
        noticeBody.className = 'license-notice';
        noticeBody.append(title, source, text);
        content.append(noticeBody);
        return { notice, text, loaded: false };
    });

    /** 本文を閉じたら、一覧で同じライブラリーの開閉ボタンに戻ります。 */
    function collapse()
    {
        content.hidden = true;
        toggle.setAttribute('aria-expanded', 'false');
        toggle.focus({ preventScroll: true });
        toggle.scrollIntoView({ block: 'nearest' });
    }

    toggle.addEventListener('click', () =>
    {
        if (!content.hidden)
        {
            collapse();
            return;
        }
        content.hidden = false;
        toggle.setAttribute('aria-expanded', 'true');
        for (const item of notices.filter(item => !item.loaded))
        {
            loadNoticeAsync(entry.name, item, ctx).catch(() =>
            {
                item.loaded = false;
                if (!ctx.signal.aborted)
                {
                    item.text.textContent = ctx.t('Could not load the notice. Close and reopen to retry.', '本文を読み込めませんでした。閉じて開き直すと再試行できます。');
                }
            });
        }
    }, { signal: ctx.signal });
    close.addEventListener('click', collapse, { signal: ctx.signal });
}

/** 取得できなかった本文は、開き直したときに取得できるようにします。
 * @returns 通知本文の表示が完了するまでの処理です。
 */
async function loadNoticeAsync(component, item, ctx)
{
    item.loaded = true;
    item.text.textContent = ctx.t('Loading…', '読み込み中…');
    const response = await fetch(item.notice.href, { signal: ctx.signal });
    if (!response.ok)
    {
        throw new Error(String(response.status));
    }
    const source = await response.text();
    if (!ctx.signal.aborted)
    {
        renderNotice(item.text, component, source);
    }
}

/** 同梱エディターの通知では、配布ファイルのパッケージ名と版を見出しとして表示します。 */
function renderNotice(container, component, source)
{
    const bundledEditor = '@codemirror/autocomplete';
    const blocks = component === bundledEditor ? source.split(/^([@a-z][@a-z0-9_./-]* \d+\.\d+\.\d+)(?=\r?$)/gmu) : [source];
    container.replaceChildren();
    for (const [index, block] of blocks.entries())
    {
        if (index % 2)
        {
            const title = document.createElement('h4');
            title.textContent = block;
            container.append(title);
            continue;
        }
        if (!block.trim())
        {
            continue;
        }
        const text = document.createElement('pre');
        for (const paragraph of block.trim().split(/(\r?\n(?:[ \t]*\r?\n)+)/u))
        {
            if (/^(?:Permission is hereby granted, free of charge|The above copyright notice and this permission notice|THE SOFTWARE IS PROVIDED "AS IS")/u.test(paragraph))
            {
                const prose = document.createElement('span');
                prose.className = 'license-prose';
                prose.textContent = paragraph;
                text.append(prose);
            }
            else
            {
                text.append(paragraph);
            }
        }
        container.append(text);
    }
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

/** 配布元の文書名を残し、文書の種類が分かる見出しを表示します。
 * @returns 通知を開くときに表示する名前です。
 */
function noticeCaption(label, ctx)
{
    switch (label)
    {
        case 'License':
            return ctx.t('License text', 'ライセンス本文');
        case 'Third-party notices':
            return ctx.t('Third-party notices', '第三者のソフトウェアに関する通知');
        case 'Copyright notice':
            return ctx.t('Copyright notice', '著作権表示');
        default:
            return ctx.t(label + ' notice', label + ' のライセンス・通知');
    }
}
