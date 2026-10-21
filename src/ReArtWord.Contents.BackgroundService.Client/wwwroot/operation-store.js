// Keep large operation requests/results out of the small storage shared by preferences and drafts.
// Each write resolves only after its IndexedDB transaction commits, before the caller sends a request.
window.contentsOperationStore = (() =>
{
    const databaseName = 'contents.operation-journal', storeName = 'operations';

    /** 指定した対象を開き、表示状態を更新します。
     * @returns 呼出し元が使用する値です。
     */
    function open()
    {
        return new Promise((resolve, reject) =>
        {
            const request = indexedDB.open(databaseName, 1);
            let abandoned = false;
            request.onupgradeneeded = () => request.result.createObjectStore(storeName, { keyPath: 'operationId' });
            request.onsuccess = () => abandoned ? request.result.close() : resolve(request.result);
            request.onerror = () => reject(request.error);
            request.onblocked = () =>
            {
                abandoned = true;
                reject(new Error('Operation storage is blocked. Reload other open tabs.'));
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
            const tx = database.transaction(storeName, mode);
            let result, failure;
            tx.oncomplete = () => resolve(result);
            tx.onabort = () => reject(failure ?? tx.error ?? new Error('Operation storage transaction aborted.'));
            try
            {
                action(tx.objectStore(storeName), value =>
                {
                    result = value;
                });
            }
            catch (error)
            {
                failure = error;
                tx.abort();
            }
        });
    }

    /** 入力が処理可能な形式かを検証します。
     * @returns operationIdを含む結果です。
     */
    function validate(item)
    {
        if (!item || typeof item.operationId !== 'string' ||
            !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(item.operationId) ||
            typeof item.kind !== 'string' || typeof item.readPath !== 'string' ||
            typeof item.createdAt !== 'string' || item.request === undefined)
        {
            throw new Error('Invalid operation history. The original browser data has been preserved.');
        }
        return { ...item, operationId: item.operationId.toLowerCase() };
    }

    /** 旧保存値を確定してから元の値を移行します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function migrateAsync(database)
    {
        const sources = ['contents.blazor.operations', 'contents.operations']
            .map(key => ({ key, text: localStorage.getItem(key) })).filter(source => source.text !== null);
        if (!sources.length)
        {
            return;
        }
        const entries = new Map();
        for (const source of sources)
        {
            const values = JSON.parse(source.text);
            if (!Array.isArray(values))
            {
                throw new Error('Invalid operation history. The original browser data has been preserved.');
            }
            for (const value of values)
            {
                const expectedContentsOperations = 'contents.operations';

                // The older UI had no confirmed result or fixed peer in this format.
                const item = validate(source.key === expectedContentsOperations ? {
                    operationId: value.operationId, kind: value.kind, readPath: value.readPath,
                    recoverPath: value.recoverPath ?? null, request: value.request, createdAt: value.createdAt
                } : value);
                if (!entries.has(item.operationId))
                {
                    entries.set(item.operationId, item);
                }
            }
        }
        await transaction(database, 'readwrite', store =>
        {
            for (const item of entries.values())
            {
                const request = store.get(item.operationId);
                request.onsuccess = () =>
                {
                    if (!request.result)
                    {
                        store.add(item);
                    }
                };
            }
        });

        // A failed migration never removes the source. A tab still running the old UI may have changed it.
        for (const source of sources)
        {
            if (localStorage.getItem(source.key) === source.text)
            {
                localStorage.removeItem(source.key);
            }
        }
    }

    return {
        /** 保存された値または現在の応答を読み取ります。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async read()
        {
            const database = await open();
            try
            {
                await migrateAsync(database);
                const values = await transaction(database, 'readonly', (store, done) =>
                {
                    const request = store.getAll();
                    request.onsuccess = () => done(request.result);
                });
                return JSON.stringify(values);
            }
            finally
            {
                database.close();
            }
        },

        /** 指定された値を所有する保存先へ書き込みます。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async write(text, mode)
        {
            const item = validate(JSON.parse(text));
            if (!['create', 'track', 'update'].includes(mode))
            {
                throw new Error('Invalid operation storage mode.');
            }
            const database = await open();
            try
            {
                const saved = await transaction(database, 'readwrite', (store, done) =>
                {
                    const expectedCreate = 'create';

                    if (mode === expectedCreate)
                    {
                        store.add(item);
                        done(item);
                        return;
                    }
                    const request = store.get(item.operationId);
                    request.onsuccess = () =>
                    {
                        const expectedTrack = 'track';

                        const existing = request.result;
                        const value = existing ? (mode === expectedTrack ? existing : { ...existing, result: item.result }) : item;
                        store.put(value);
                        done(value);
                    };
                });
                return JSON.stringify(saved);
            }
            finally
            {
                database.close();
            }
        }
    };
})();
