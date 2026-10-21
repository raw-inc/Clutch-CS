// Cluster and browser panes share selection, navigation and drag semantics.
/** ファイル一覧の行を、選択と移動の操作に接続します。 */
export function appendFileRows(tree, entries, ctx, options)
{
    for (const entry of [...entries].sort((a, b) => Number(b.isDirectory) - Number(a.isDirectory) || a.name.localeCompare(b.name, undefined, { numeric: true })))
    {
        const row = document.createElement('div');
        row.className = 'file-row';
        row.draggable = options.selectable;
        const check = document.createElement('input');
        check.type = 'checkbox';
        check.setAttribute('aria-label', ctx.t('Select ', '選択: ') + entry.name);
        check.disabled = !options.selectable;
        check.checked = options.selected.has(entry.path);
        row.classList.toggle('selected', check.checked);

        /** 新しい値を保持し、変更を呼出し元へ通知します。 */
        const change = () =>
        {
            if (options.busy?.())
            {
                check.checked = options.selected.has(entry.path);
                return;
            }
            if (check.checked)
            {
                options.selected.add(entry.path);
            }
            else
            {
                options.selected.delete(entry.path);
            }
            row.classList.toggle('selected', check.checked);
            options.changed();
        };

        check.onchange = change;
        const name = document.createElement('button');
        name.type = 'button';
        name.className = 'file-name';
        name.textContent = entry.name;
        name.prepend(ctx.icon(!options.selectable ? 'drive' : entry.isDirectory ? 'folder' : 'file'));
        name.title = entry.path;
        name.onclick = event =>
        {
            if ((1) < (event.detail) || options.busy?.())
            {
                return;
            }
            if (options.selectable)
            {
                check.checked = !check.checked;
                change();
            }
            else
            {
                for (const item of tree.children)
                {
                    item.classList.remove('selected');
                }
                row.classList.add('selected');
            }
        };
        name.ondblclick = () =>
        {
            if (entry.isDirectory && !options.busy?.())
            {
                void ctx.guard(() => options.open(entry.path));
            }
        };
        name.onkeydown = event =>
        {
            const expectedEnter = 'Enter';

            if (event.key === expectedEnter && entry.isDirectory && !options.busy?.())
            {
                event.preventDefault();
                void ctx.guard(() => options.open(entry.path));
            }
        };
        row.append(check, name);
        const kind = document.createElement('span');
        kind.className = 'file-kind';
        kind.textContent = !options.selectable ? ctx.t('Drive', 'ドライブ') : entry.isDirectory ? ctx.t('Folder', 'フォルダー') : ctx.t('File', 'ファイル');
        row.append(kind);
        if (options.drag)
        {
            row.ondragstart = event =>
            {
                if (options.busy?.())
                {
                    event.preventDefault();
                    return;
                }
                options.drag(event, options.selected.has(entry.path) ? [...options.selected] : [entry.path]);
            };
            row.ondragend = options.dragEnd;
        }
        if (entry.isDirectory)
        {
            options.drop?.(row, entry.path);
        }
        options.decorate?.(row, entry);
        tree.append(row);
    }
}
