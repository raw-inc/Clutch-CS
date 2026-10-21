import { copyFile, mkdir, readFile } from 'node:fs/promises';

/** 固定したパッケージから、実際に使用するブラウザー用モジュールだけを配備します。 */
const source = new URL('../node_modules/@highlightjs/cdn-assets/', import.meta.url);
const destination = new URL('../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/vendor/highlight/', import.meta.url);
const packageInfo = JSON.parse(await readFile(new URL('package.json', source), 'utf8'));
if (packageInfo.version !== '11.12.0')
{
    throw new Error('Review syntax highlighting and license notices before changing the pinned version.');
}
await mkdir(new URL('languages/', destination), { recursive: true });
await copyFile(new URL('es/core.min.js', source), new URL('core.min.js', destination));
for (const language of ['csharp', 'python', 'javascript', 'bash', 'powershell', 'json', 'ini', 'yaml'])
{
    await copyFile(new URL(`es/languages/${language}.min.js`, source), new URL(`languages/${language}.min.js`, destination));
}
await copyFile(new URL('LICENSE', source), new URL('LICENSE', destination));
await copyFile(new URL('LICENSE', source), new URL('../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/licenses/highlight-license.txt', import.meta.url));
