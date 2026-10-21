// Large editor data uses committed IndexedDB transactions. Preferences stay in Web Storage.
window.contentsEditorStore = (() =>
{
    const prefixes = ['contents.blazor.draft.', 'contents.blazor.editor.index.', 'contents.editor.saved.',
        'contents.editor.references.', 'contents.draft.', 'contents.blazor.force-save.backup', 'contents.internal-data.browser.'];

    /** 保存キーがこの領域の所有範囲かを判定します。
     * @returns 呼出し元が使用する判定結果です。
     */
    const owns = key => prefixes.some(prefix => key.startsWith(prefix));

    const claims = new Map();

    /** 指定した対象を開き、表示状態を更新します。
     * @returns 呼出し元が使用する値です。
     */
    function open()
    {
        return new Promise((resolve, reject) =>
        {
            const request = indexedDB.open('contents.editor', 1);
            let abandoned = false;
            request.onupgradeneeded = () =>
            {
                request.result.createObjectStore('values');
                request.result.createObjectStore('recovery', { autoIncrement: true });
            };
            request.onsuccess = () => abandoned ? request.result.close() : resolve(request.result);
            request.onerror = () => reject(request.error);
            request.onblocked = () =>
            {
                abandoned = true;
                reject(new Error('Editor storage is blocked. Existing drafts have been preserved.'));
            };
        });
    }

    /** IndexedDBの確定・中断をPromiseの結果に対応付けます。
     * @returns 呼出し元が使用する値です。
     */
    function transaction(database, mode, action)
    {
        return new Promise((resolve, reject) =>
        {
            const tx = database.transaction(['values', 'recovery'], mode);
            let value, failure;
            tx.oncomplete = () => resolve(value);
            tx.onabort = () => reject(failure ?? tx.error ?? new Error('Editor storage transaction aborted.'));
            try
            {
                action(tx.objectStore('values'), tx.objectStore('recovery'), result =>
                {
                    value = result;
                });
            }
            catch (error)
            {
                failure = error;
                tx.abort();
            }
        });
    }

    /** 旧保存値を確定してから元の値を移行します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function migrateAsync(database)
    {
        // LEGACY-FORMAT: editor-web-storage-v1 — 全草稿の移行確認後にWeb Storageからの取込みを撤去します。
        const sources = Object.keys(localStorage).filter(owns).map(key => ({ key, text: localStorage.getItem(key) }));
        if (!sources.length)
        {
            return;
        }
        await transaction(database, 'readwrite', (store, recovery) =>
        {
            for (const source of sources)
            {
                if (source.text === null)
                {
                    continue;
                }
                const request = store.get(source.key);
                request.onsuccess = () =>
                {
                    if (request.result === undefined)
                    {
                        store.put(source.text, source.key);
                    }

                    // An older UI can still write localStorage. Never overwrite a newer IndexedDB draft.
                    else if (request.result !== source.text)
                    {
                        recovery.add(source);
                    }
                };
            }
        });

        // Commit first; a failed migration or an old tab's concurrent edit keeps the source intact.
        for (const source of sources)
        {
            if (localStorage.getItem(source.key) === source.text)
            {
                localStorage.removeItem(source.key);
            }
        }
    }

    /** 必要な保存領域を開き、処理終了時に閉じます。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function useAsync(action)
    {
        const database = await open();
        try
        {
            await migrateAsync(database);
            return await action(database);
        }
        finally
        {
            database.close();
        }
    }

    /** 他の画面が使用中でない草稿の所有者を確保します。
     * @returns 呼出し元が使用する値です。
     */
    function claim(candidate)
    {
        return new Promise((resolve, reject) =>
        {
            navigator.locks.request('contents.editor.owner.' + candidate, { ifAvailable: true }, async lock =>
            {
                resolve(lock ? candidate : null);

                // The browser releases this lock on document destruction, including reload/crash.
                if (lock)
                {
                    await new Promise(() =>
                    {});
                }
            }).catch(reject);
        });
    }

    return {
        owns,

        /** 保存された値または現在の応答を読み取ります。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async read(key)
        {
            let database;
            try
            {
                database = await open();
            }
            catch (error)
            {
                // Retain a readable legacy draft for export even if the migration cannot commit.
                const legacy = localStorage.getItem(key);
                if (legacy !== null)
                {
                    return legacy;
                }
                throw error;
            }
            try
            {
                let failure;
                try
                {
                    await migrateAsync(database);
                }
                catch (error)
                {
                    failure = error;
                }
                const saved = await transaction(database, 'readonly', (store, recovery, done) =>
                {
                    const request = store.get(key);
                    request.onsuccess = () => done(request.result);
                });

                // A failed migration must never replace an already committed draft with an old UI's copy.
                if (saved !== undefined)
                {
                    return saved;
                }
                const legacy = localStorage.getItem(key);
                if (legacy !== null)
                {
                    return legacy;
                }
                if (failure)
                {
                    throw failure instanceof Error ? failure : new Error('Editor storage migration failed.', { cause: failure });
                }
                return null;
            }
            finally
            {
                database.close();
            }
        },

        /** 指定された値を所有する保存先へ書き込みます。
         * @returns 呼出し元が使用する値です。
         */
        write(key, text)
        {
            return useAsync(database => transaction(database, 'readwrite', store =>
            {
                const current = store.get(key);
                current.onsuccess = () =>
                {
                    if (current.result !== text)
                    {
                        store.put(text, key);
                    }
                };
            }));
        },

        /** 移行済みで、読取り後に変更されていない旧草稿だけを同じ取引で削除します。
         * @returns 元の本文が一致して削除できたかです。
         */
        removeIfUnchanged(key, expected)
        {
            return useAsync(database => transaction(database, 'readwrite', (store, recovery, done) =>
            {
                const current = store.get(key);
                current.onsuccess = () =>
                {
                    const matches = current.result === expected;
                    if (matches)
                    {
                        store.delete(key);
                    }
                    done(matches);
                };
            }));
        },

        /** 再読み込みと別タブを区別して草稿の所有者を決めます。
         * @returns 呼出し元が使用する値です。
         */
        owner(previous, proposed)
        {
            if (!claims.has(proposed))
            {
                claims.set(proposed, (async () =>
                {
                // Unsupported browsers retain the original copy behavior rather than sharing live drafts.
                    if (!navigator.locks)
                    {
                        return proposed;
                    }
                    if (previous)
                    {
                        const retained = await claim(previous);
                        if (retained)
                        {
                            return retained;
                        }
                    }
                    const fresh = await claim(proposed);
                    if (!fresh)
                    {
                        throw new Error('The editor draft owner is already active.');
                    }
                    return fresh;
                })());
            }
            return claims.get(proposed);
        }
    };
})();
