import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { createReadStream } from 'node:fs';
import { appendFile, readFile, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { releaseNotes } from './release-notes.mjs';

// PRの統合方法によらず、版名と統合後のコミットを固定する。
export function planRelease(event) {
    const pr = event.pull_request;
    const repository = event.repository?.full_name;
    if (event.action !== 'closed' || pr?.merged !== true || pr.base?.ref !== 'main'
        || !repository || pr.base.repo?.full_name !== repository || pr.head?.repo?.full_name !== repository
        || !pr.head.ref?.startsWith('release/')) {
        return null;
    }
    const tag = pr.head.ref.slice('release/'.length);
    const match = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/.exec(tag);
    if (!match || match[0] !== tag || match[4]?.split('.').some(part => /^\d+$/.test(part) && part.length > 1 && part.startsWith('0'))) {
        return null;
    }
    if (!/^[0-9a-f]{40}$/.test(pr.merge_commit_sha)) {
        throw new Error('The merged commit SHA is missing or invalid.');
    }
    return { tag, version: tag.slice(1), sha: pr.merge_commit_sha, prerelease: Boolean(match[4]) };
}

// Releaseを作る前に配布ファイルとチェックサムを照合する。
export async function releaseAssets(directory, tag) {
    const files = [];
    for (const name of [`Clutch-CS-Setup-${tag}-win-x64.zip`, `Clutch-CS-Source-${tag}.zip`]) {
        const archive = join(directory, name);
        const checksum = `${archive}.sha256`;
        for (const path of [archive, checksum]) {
            const entry = await stat(path);
            if (!entry.isFile() || entry.size === 0) throw new Error(`Missing release asset: ${path}`);
        }
        const hash = createHash('sha256');
        for await (const chunk of createReadStream(archive)) hash.update(chunk);
        const expected = (await readFile(checksum, 'utf8')).trim().split(/\s+/);
        if (expected.length !== 2 || expected[0].toLowerCase() !== hash.digest('hex') || expected[1] !== name) {
            throw new Error(`The release ZIP does not match its checksum: ${name}`);
        }
        files.push(archive, checksum);
    }
    return files;
}

// 完了済みReleaseは変更せず、同じコミットの未完了draftだけを再開する。
export async function publishRelease(api, upload, repository, plan, assets, runUrl) {
    const root = `/repos/${repository}`;
    const taggedCommit = await releaseCommit(api, root, plan.tag);
    if (taggedCommit && taggedCommit !== plan.sha) {
        throw new Error('This version tag already points to a different commit.');
    }
    // draftも取得できる一覧APIを使い、公開途中で止まった実行を検出する。
    const pageSize = 100;
    let release;
    for (let page = 1; !release; page++) {
        const releases = await api('GET', `${root}/releases?per_page=${pageSize}&page=${page}`);
        if (!Array.isArray(releases)) throw new Error('Existing releases could not be inspected.');
        release = releases.find(item => item.tag_name === plan.tag);
        if (releases.length < pageSize) break;
    }
    if (release && !release.draft) {
        if (!taggedCommit) throw new Error('The published release has no matching Git tag.');
        return { url: release.html_url, alreadyPublished: true };
    }
    if (release && release.target_commitish !== plan.sha) {
        throw new Error('The draft release belongs to a different commit.');
    }
    const notes = await api('POST', `${root}/releases/generate-notes`, {
        tag_name: plan.tag, target_commitish: plan.sha,
    });
    const body = releaseNotes(notes.body, runUrl);
    if (!release) {
        release = await api('POST', `${root}/releases`, {
            tag_name: plan.tag, target_commitish: plan.sha, name: `Clutch-CS ${plan.tag}`,
            body, draft: true, prerelease: plan.prerelease,
        });
    }
    await upload(plan.tag, assets, repository);
    const published = await api('PATCH', `${root}/releases/${release.id}`, {
        body, draft: false, prerelease: plan.prerelease, make_latest: plan.prerelease ? 'false' : 'legacy',
    });
    return { url: published.html_url, alreadyPublished: false };
}

// 公開した版だけを取り込み、CI実行中にmainへ加わった次の版は含めない。
export async function syncDevelop(api, repository, plan) {
    const root = `/repos/${repository}`;
    const release = await api('GET', `${root}/releases/tags/${encodeURIComponent(plan.tag)}`);
    if (!release || release.draft !== false || release.tag_name !== plan.tag) {
        throw new Error('The release must be published before merging into develop.');
    }
    if (await releaseCommit(api, root, plan.tag) !== plan.sha) {
        throw new Error('The published release tag does not match the merged commit.');
    }
    const main = await api('GET', `${root}/compare/${plan.sha}...main`);
    if (!main || !['identical', 'ahead'].includes(main.status)) {
        throw new Error('The released commit is no longer contained in main.');
    }
    try {
        const merged = await api('POST', `${root}/merges`, {
            base: 'develop', head: plan.sha, commit_message: `chore: merge ${plan.tag} release into develop`,
        });
        if (merged !== null && !/^[0-9a-f]{40}$/.test(merged?.sha)) {
            throw new Error('The merge result could not be confirmed.');
        }
        return { alreadyMerged: merged === null, sha: merged?.sha };
    } catch (error) {
        if (error.status !== 409) throw error;
    }
    return openSyncPullRequest(api, repository, plan, release.html_url);
}

// 競合時だけPRに切り替える。再実行でPRを重複作成せず、手動の競合解消を上書きしない。
async function openSyncPullRequest(api, repository, plan, releaseUrl) {
    const root = `/repos/${repository}`;
    const branch = `feature/sync-${plan.tag}-to-develop`;
    const query = new URLSearchParams({ state: 'all', base: 'develop', head: `${repository.split('/')[0]}:${branch}`, per_page: '1' });
    const pulls = await api('GET', `${root}/pulls?${query}`);
    if (!Array.isArray(pulls)) throw new Error('Existing develop sync PRs could not be inspected.');
    const existing = pulls[0];
    if (existing) {
        if (!existing.html_url) throw new Error('The existing develop sync PR could not be confirmed.');
        if (existing.merged_at) {
            throw new Error(`The previous sync PR was merged, but the released commit still conflicts with develop. Review the merge history: ${existing.html_url}`);
        }
        if (existing.state !== 'open') {
            throw new Error(`The develop sync PR was closed without merging. Review or reopen it: ${existing.html_url}`);
        }
        return { alreadyCreated: true, url: existing.html_url };
    }

    let reference = await api('GET', `${root}/git/ref/heads/${branch}`);
    if (!reference) {
        reference = await api('POST', `${root}/git/refs`, { ref: `refs/heads/${branch}`, sha: plan.sha });
    }
    if (reference?.object?.type !== 'commit' || reference.object.sha !== plan.sha) {
        throw new Error('The develop sync branch does not point to the published commit; it was not overwritten.');
    }
    const pull = await api('POST', `${root}/pulls`, {
        base: 'develop', head: branch, title: `chore: merge ${plan.tag} into develop`,
        body: `公開済みの [Clutch-CS ${plan.tag}](${releaseUrl}) を develop へ自動マージする際に競合が発生しました。\n\n`
            + `対象コミット: \`${plan.sha}\`\n\n`
            + '内容を確認し、競合がある場合は解消してから **Create a merge commit** でマージしてください。',
    });
    if (pull?.state !== 'open' || !pull.html_url) throw new Error('The develop sync PR could not be confirmed.');
    return { alreadyCreated: false, url: pull.html_url };
}

// 軽量タグと注釈付きタグのどちらも、指しているコミットで照合する。
async function releaseCommit(api, root, name) {
    const reference = await api('GET', `${root}/git/ref/tags/${encodeURIComponent(name)}`);
    if (!reference) return null;
    let object = reference.object;
    while (object.type === 'tag') {
        const tag = await api('GET', `${root}/git/tags/${object.sha}`);
        if (!tag) throw new Error('The annotated tag cannot be resolved.');
        object = tag.object;
    }
    if (object.type !== 'commit') throw new Error('The release tag does not point to a commit.');
    return object.sha;
}

// 認証や通信の失敗を「未作成」と区別する。
async function githubApi(method, path, body) {
    const response = await fetch(`${process.env.GITHUB_API_URL || 'https://api.github.com'}${path}`, {
        method,
        headers: {
            Authorization: `Bearer ${process.env.GH_TOKEN}`,
            Accept: 'application/vnd.github+json',
            'Content-Type': 'application/json',
            'X-GitHub-Api-Version': '2026-03-10',
        },
        body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (method === 'GET' && response.status === 404) return null;
    if (!response.ok) throw Object.assign(new Error(`GitHub ${method} ${path} failed: HTTP ${response.status}`), { status: response.status });
    if (response.status === 204) return null;
    return response.json();
}

async function main() {
    const event = JSON.parse(await readFile(process.env.GITHUB_EVENT_PATH, 'utf8'));
    const plan = planRelease(event);
    if (process.argv[2] === 'plan') {
        const output = { eligible: Boolean(plan), ...plan };
        await appendFile(process.env.GITHUB_OUTPUT, Object.entries(output).map(([key, value]) => `${key}=${value}\n`).join(''));
        return;
    }
    if (!['publish', 'sync-develop'].includes(process.argv[2]) || !plan) throw new Error('A merged release/vx.y.z[-suffix] branch is required.');
    if (!process.env.GH_TOKEN) throw new Error('GH_TOKEN is required for release operations.');
    if (process.argv[2] === 'sync-develop') {
        const result = await syncDevelop(githubApi, event.repository.full_name, plan);
        const summary = result.alreadyMerged || !result.url
            ? `${result.alreadyMerged ? 'Already merged' : 'Merged'}: ${plan.tag} into develop`
            : `${result.alreadyCreated ? 'Existing' : 'Created'} conflict resolution PR: [${plan.tag}](${result.url}) — resolve conflicts and merge manually.`;
        await appendFile(process.env.GITHUB_STEP_SUMMARY, `${summary}\n`);
        return;
    }
    const assets = await releaseAssets(process.argv[3], plan.tag);
    const runUrl = `${process.env.GITHUB_SERVER_URL}/${event.repository.full_name}/actions/runs/${process.env.GITHUB_RUN_ID}`;
    const result = await publishRelease(githubApi, async (tag, files, repository) => {
        execFileSync('gh', ['release', 'upload', tag, ...files, '--repo', repository, '--clobber'], { stdio: 'inherit' });
    }, event.repository.full_name, plan, assets, runUrl);
    await appendFile(process.env.GITHUB_STEP_SUMMARY, `${result.alreadyPublished ? 'Already published' : 'Published'}: [Clutch-CS ${plan.tag}](${result.url})\n`);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
    try {
        await main();
    } catch (error) {
        console.error(error);
        process.exitCode = 1;
    }
}
