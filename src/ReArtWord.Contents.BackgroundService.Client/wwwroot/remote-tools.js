import { uiIcon } from './ui-icons.js';
import { desktopView } from './remote-desktop.js';
import { terminalView } from './remote-terminal.js';
import { filesView } from './remote-files.js';

const screens = new WeakMap();

/** 対象画面の処理と表示を解放します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function unmount(element)
{
    await screens.get(element)?.dispose();
    screens.delete(element);
    element.replaceChildren();
}

/** 画面の寿命に対応する接続・操作・表示を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function mount(element, mode, language, timeZone = 'UTC')
{
    const expectedFiles = 'files';
    const expectedBrowserFiles = 'browser-files';
    const expectedJa = 'ja';

    await unmount(element);
    const controller = new AbortController();
    const message = document.createElement('p');
    message.className = 'remote-message';
    message.setAttribute('role', 'status');
    const heading = document.createElement('h1');
    const titles = { desktop: 'RemoteDesktop', files: 'FileTransfer · Cluster', 'browser-files': 'FileTransfer · Browser ↔ Host', licenses: 'OSS License', 'signal-scope': 'SignalScope' };
    heading.textContent = titles[mode] ?? 'Terminal';
    const title = document.createElement('div');
    title.className = 'page-title';
    title.append(heading);
    const content = document.createElement('div');
    element.append(title, message, content);
    if (mode === expectedFiles || mode === expectedBrowserFiles)
    {
        const modes = document.createElement('nav');
        modes.className = 'transfer-modes';
        modes.setAttribute('aria-label', 'FileTransfer');
        for (const [value, path, label] of [['files', '/file-transfer', 'Cluster · Host ↔ Host (α)'], ['browser-files', '/browser-transfer', 'Browser ↔ Host (α)']])
        {
            const link = document.createElement('a');
            link.href = path;
            link.textContent = label;
            if (mode === value)
            {
                link.setAttribute('aria-current', 'page');
            }
            modes.append(link);
        }
        element.insertBefore(modes, message);
    }
    const context = {
        signal: controller.signal,
        timeZone,

        say: text =>
        {
            const assignedValue = message.textContent = text;
            return assignedValue;
        },

        t: (en, ja) => language === expectedJa ? ja : en,

        icon: uiIcon,

        /** 取消しと応答の確認を伴う管理要求を送信します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async api(path, peer, body, requestSignal)
        {
            const headers = {};
            const binary = body instanceof Uint8Array;
            if (peer)
            {
                headers['X-Contents-Cluster'] = peer.clusterId;
                headers['X-Contents-Peer'] = peer.peerId;
                if (peer.instanceId)
                {
                    headers['X-Contents-Instance'] = peer.instanceId;
                }
            }
            if (body !== undefined)
            {
                headers['Content-Type'] = binary ? 'application/octet-stream' : 'application/json';
                headers['X-Contents-Request'] = crypto.randomUUID();
            }
            const response = await fetch(path, { method: body === undefined ? 'GET' : 'POST', headers,
                body: body === undefined ? undefined : binary ? body : JSON.stringify(body), signal: requestSignal ?? controller.signal, cache: 'no-store' });
            if (!response.ok)
            {
                throw new Error(context.t(`Request unconfirmed (${response.status}). Check the peer and refresh before repeating an operation.`, `要求結果を確認できません (${response.status})。ピアを確認し、操作を繰り返す前に再取得してください。`));
            }
            return await response.json();
        },

        /** 操作の失敗を受け止め、現在の画面へ結果を通知します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async guard(action)
        {
            try
            {
                await action();
            }
            catch (error)
            {
                if (!controller.signal.aborted)
                {
                    context.say(error.message);
                }
            }
        },

        /** 操作の実行と処理中表示を持つボタンを構成します。
         * @returns 呼出し元が使用する値です。
         */
        button(text, action)
        {
            const button = document.createElement('button');
            button.type = 'button';
            button.textContent = text;
            button.onclick = () => context.guard(async () =>
            {
                button.disabled = true;
                try
                {
                    await action();
                }
                finally
                {
                    button.disabled = false;
                    screen.view?.updateControls?.();
                }
            });
            return button;
        },

        /** 現在到達可能なピアとクラスタを取得します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async peers()
        {
            const clusters = await context.api('/client/clusters');
            return clusters.flatMap(cluster => cluster.peers.map(peer => ({ ...peer, clusterId: cluster.clusterId })));
        }
    };
    const screen = {
        /** 所有する監視・接続・表示資源を解放します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async dispose()
        {
            controller.abort();
            await screen.view?.dispose();
        } };
    screens.set(element, screen);
    await context.guard(async () =>
    {
        const expectedDesktop2 = 'desktop';
        const expectedFiles2 = 'files';
        const expectedBrowserFiles2 = 'browser-files';
        const expectedLicenses2 = 'licenses';

        switch (mode)
        {
            case 'signal-scope':
                {
                    screen.view = await (await import('./signal-scope.js')).signalScopeViewAsync(content, context);
                }
                break;
            case expectedDesktop2:
                {
                    screen.view = await desktopView(content, context);
                }
                break;
            case expectedFiles2:
                {
                    screen.view = await filesView(content, context);
                }
                break;
            case expectedBrowserFiles2:
                {
                    screen.view = await (await import('./browser-files.js')).browserFilesView(content, context);
                }
                break;
            case expectedLicenses2:
                {
                    screen.view = await (await import('./oss-licenses.js')).licensesView(content, context);
                }
                break;
            default:
                {
                    screen.view = await terminalView(content, context);
                }
                break;
        }
        if (controller.signal.aborted)
        {
            await screen.view.dispose();
        }
    });
}
