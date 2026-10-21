import { validFolderName } from './file-mutations.js';

/** 二種類のファイル画面で、対象の確認・実行中のロック・一覧更新を揃えます。
 * @returns ボタンと操作状態、画面終了時の解放処理です。
 */
export function fileActionControls(root, ctx, actions)
{
    let busy = false, disposed = false, dialog;
    const create = ctx.button(ctx.t('New folder', 'フォルダー作成'), () => runAsync(true));
    const remove = ctx.button(ctx.t('Delete selected', '選択項目を削除'), () => runAsync(false));
    create.prepend(ctx.icon('folder'));
    remove.prepend(ctx.icon('trash'));
    remove.className = 'danger';

    /** 確認中のPCやフォルダーが変わった場合は操作を開始しません。
     * @returns 現在も同じ表示対象かです。
     */
    function current(snapshot)
    {
        const latest = actions.read();
        return !disposed && !ctx.signal?.aborted && latest.generation === snapshot.generation && latest.directory === snapshot.directory
            && latest.peer === snapshot.peer && latest.root === snapshot.root;
    }

    /** 入力を確定した一回だけ実行し、部分完了と未確認を表示します。
     * @returns 操作と該当一覧の更新完了です。
     */
    async function runAsync(creating)
    {
        const snapshot = actions.read();
        if (busy || !snapshot.available || !creating && !snapshot.entries.length)
        {
            return;
        }
        busy = true;
        actions.changed();
        let attempted = false, completed = 0;
        try
        {
            const name = await askAsync(creating, snapshot);
            if (name === null || !current(snapshot))
            {
                return;
            }
            if (creating)
            {
                attempted = true;
                await actions.create(snapshot, name);
                ctx.say(ctx.t('Folder created: ', 'フォルダーを作成しました: ') + name);
            }
            else
            {
                for (const entry of snapshot.entries)
                {
                    if (!current(snapshot))
                    {
                        throw new Error(ctx.t('The view changed. Remaining items were not deleted.', '表示対象が変わりました。残りの項目は削除していません。'));
                    }
                    attempted = true;
                    await actions.remove(snapshot, entry);
                    completed++;
                    ctx.say(ctx.t(`Deleted ${completed} of ${snapshot.entries.length} items.`, `${snapshot.entries.length}件中${completed}件を削除しました。`));
                }
            }
        }
        catch (error)
        {
            if (!disposed)
            {
                ctx.say(creating ? error.message : ctx.t(`${completed} deleted. `, `${completed}件を削除済みです。`) + error.message);
            }
        }
        finally
        {
            busy = false;
            if (attempted && current(snapshot))
            {
                await ctx.guard(() => actions.refresh(snapshot));
            }
            actions.changed();
        }
    }

    /** 操作先と項目を示し、取消しでは何も変更しません。
     * @returns 作成名または削除の確認、取り消した場合はnullです。
     */
    function askAsync(creating, snapshot)
    {
        return new Promise(resolve =>
        {
            dialog = document.createElement('dialog');
            dialog.className = 'file-action-dialog';
            const form = document.createElement('form'), title = document.createElement('h2'), location = document.createElement('p');
            title.textContent = creating ? ctx.t('New folder', 'フォルダー作成') : ctx.t('Delete selected items', '選択項目を削除');
            title.id = 'file-action-' + crypto.randomUUID();
            dialog.setAttribute('aria-labelledby', title.id);
            location.className = 'file-action-location';
            location.textContent = snapshot.label + ' · ' + (snapshot.directory || '/');
            form.append(title, location);
            const name = document.createElement('input');
            if (creating)
            {
                const label = document.createElement('label');
                label.textContent = ctx.t('Folder name', 'フォルダー名');
                name.setAttribute('aria-label', label.textContent);
                name.required = true;
                name.maxLength = 255;
                name.autofocus = true;
                name.oninput = () => name.setCustomValidity('');
                label.append(name);
                form.append(label);
            }
            else
            {
                const warning = document.createElement('p'), list = document.createElement('ul');
                warning.textContent = ctx.t('Permanently delete these files and folders, including folder contents? They will not be moved to the recycle bin.', '選択したファイル・フォルダーを完全に削除しますか？ フォルダーの内容も削除され、ゴミ箱には移動しません。');
                list.className = 'file-action-items';
                for (const entry of snapshot.entries)
                {
                    const item = document.createElement('li');
                    item.textContent = entry.name + (entry.isDirectory ? '/' : '');
                    list.append(item);
                }
                form.append(warning, list);
            }
            const footer = document.createElement('div'), cancel = document.createElement('button'), submit = document.createElement('button');
            footer.className = 'toolbar';
            cancel.type = 'button';
            cancel.autofocus = !creating;
            cancel.textContent = ctx.t('Cancel', 'キャンセル');
            cancel.onclick = () => dialog.close();
            submit.type = 'submit';
            submit.className = creating ? 'primary' : 'danger';
            submit.textContent = creating ? ctx.t('Create folder', '作成') : ctx.t('Delete permanently', '完全に削除');
            form.onsubmit = event =>
            {
                event.preventDefault();
                if (creating && !validFolderName(name.value))
                {
                    name.setCustomValidity(ctx.t('Enter one folder name without path separators or reserved characters.', 'パス区切りや予約文字を含まないフォルダー名を入力してください。'));
                    name.reportValidity();
                    return;
                }
                dialog.close('apply');
            };
            footer.append(cancel, submit);
            form.append(footer);
            dialog.append(form);
            dialog.onclose = () =>
            {
                const apply = 'apply', accepted = dialog.returnValue === apply;
                dialog.remove();
                dialog = null;
                resolve(accepted ? creating ? name.value : '' : null);
            };
            root.append(dialog);
            dialog.showModal();
        });
    }

    return { create, remove,

        /** ナビゲーションや転送との重複を防ぐ操作状態です。
         * @returns 確認または変更処理中かです。
         */
        get busy()
        {
            return busy;
        },

        /** 閲覧状態と選択に応じて操作ボタンを切り替えます。 */
        update()
        {
            const state = actions.read();
            create.disabled = busy || !state.available;
            remove.disabled = busy || !state.available || !state.entries.length;
        },

        /** 閉じた画面で確認待ちの操作を実行しません。 */
        dispose()
        {
            disposed = true;
            dialog?.close();
        } };
}
