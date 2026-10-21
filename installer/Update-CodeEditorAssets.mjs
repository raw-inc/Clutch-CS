import { build } from 'esbuild';
import { readFile, writeFile, mkdir, copyFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

/** 固定依存からブラウザー用の単一ES Moduleを生成します。 */
const destination = new URL('../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/vendor/code-editor/', import.meta.url);
await mkdir(destination, { recursive: true });
const result = await build({ entryPoints: [fileURLToPath(new URL('code-editor-entry.mjs', import.meta.url))],
    outfile: fileURLToPath(new URL('codemirror.js', destination)), bundle: true, format: 'esm', platform: 'browser', target: 'es2022',

    // スニペット内の字下げをエスケープし、生成ファイルの行末空白と区別します。
    supported: { 'template-literal': false }, minify: true, metafile: true, legalComments: 'eof' });
const names = new Set(Object.keys(result.metafile.inputs).filter(path => path.startsWith('node_modules/')).map(path =>
{
    const parts = path.slice('node_modules/'.length).split('/');
    return parts[0].startsWith('@') ? parts.slice(0, 2).join('/') : parts[0];
}));
const packages = [], notices = [];
for (const name of [...names].sort())
{
    const root = new URL(`../node_modules/${name}/`, import.meta.url);
    const info = JSON.parse(await readFile(new URL('package.json', root), 'utf8'));
    const license = await readFile(new URL('LICENSE', root), 'utf8');
    packages.push({ name, version: info.version, license: info.license, authors: typeof info.author === 'string' ? info.author : info.author?.name ?? '', project: info.homepage ?? '' });
    notices.push(`${name} ${info.version}\n${license}`);
}
await writeFile(new URL('packages.json', destination), JSON.stringify(packages, null, 2) + '\n');
await writeFile(new URL('LICENSE', destination), notices.join('\n\n'));
await copyFile(new URL('LICENSE', destination), new URL('../../licenses/code-editor-license.txt', destination));
const manifestPath = new URL('../../licenses/notices.json', destination);
const manifest = JSON.parse((await readFile(manifestPath, 'utf8')).replace(/^\uFEFF/u, '')).filter(item => !names.has(item.name));
manifest.push(...packages.map(item => ({ ...item, notices: [{ label: 'License', href: '/licenses/code-editor-license.txt' }] })));
manifest.sort((left, right) => left.name.localeCompare(right.name, 'en', { sensitivity: 'base' }) || left.version.localeCompare(right.version, 'en', { numeric: true }));
await writeFile(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
