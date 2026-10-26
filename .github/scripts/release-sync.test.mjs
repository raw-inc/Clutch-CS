import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { syncDevelop } from './release.mjs';

const repository = 'owner/project';
const sha = 'a'.repeat(40);
const nextSha = 'b'.repeat(40);
const mergeSha = 'c'.repeat(40);
const plan = { tag: 'v1.2.3-alpha', version: '1.2.3-alpha', sha, prerelease: true };
const branch = `feature/sync-${plan.tag}-to-develop`;
const releaseUrl = `https://github.com/${repository}/releases/tag/${plan.tag}`;
const pullUrl = `https://github.com/${repository}/pull/10`;
const conflict = () => Object.assign(new Error('HTTP 409'), { status: 409 });

// マージと競合解消用PRの作成だけを公開し、タグ・ブランチの強制更新やPRの承認は受け付けない。
function githubFixture(options = {}) {
    const calls = [];
    let reference = options.branch ?? null;
    let pulls = 'pulls' in options ? options.pulls : [];
    const api = async (method, path, body) => {
        calls.push({ method, path, body });
        if (method === 'GET' && path.endsWith(`/releases/tags/${plan.tag}`)) {
            return 'release' in options ? options.release : { tag_name: plan.tag, draft: false, html_url: releaseUrl };
        }
        if (method === 'GET' && path.endsWith(`/git/ref/tags/${plan.tag}`)) {
            return 'tag' in options ? options.tag : { object: { type: 'commit', sha } };
        }
        if (method === 'GET' && path.endsWith(`/git/tags/${nextSha}`)) return { object: { type: 'commit', sha } };
        if (method === 'GET' && path.endsWith(`/compare/${sha}...main`)) {
            return { status: options.mainStatus ?? 'ahead', commits: [{ sha: nextSha }] };
        }
        if (method === 'POST' && path === `/repos/${repository}/merges`) {
            if (options.failure) throw options.failure;
            return 'merge' in options ? options.merge : { sha: mergeSha };
        }
        if (method === 'GET' && path.startsWith(`/repos/${repository}/pulls?`)) {
            const query = new URL(path, 'https://api.github.com').searchParams;
            assert.equal(query.get('base'), 'develop');
            assert.equal(query.get('head'), `owner:${branch}`);
            assert.equal(query.get('state'), 'all');
            return pulls;
        }
        if (method === 'GET' && path === `/repos/${repository}/git/ref/heads/${branch}`) return reference;
        if (method === 'POST' && path === `/repos/${repository}/git/refs`) {
            if (options.branchFailure) throw options.branchFailure;
            reference = { ref: body.ref, object: { type: 'commit', sha: body.sha } };
            return reference;
        }
        if (method === 'POST' && path === `/repos/${repository}/pulls`) {
            if (options.pullFailure) throw options.pullFailure;
            const pull = 'pull' in options ? options.pull : { state: 'open', html_url: pullUrl };
            pulls = [pull];
            return pull;
        }
        throw new Error(`Unexpected request: ${method} ${path}`);
    };
    return { api, calls, writes: () => calls.filter(call => call.method !== 'GET') };
}

test('sync merges the published commit even when main has newer work', async () => {
    for (const mainStatus of ['ahead', 'identical']) {
        const fixture = githubFixture({ mainStatus });
        assert.deepEqual(await syncDevelop(fixture.api, repository, plan), { alreadyMerged: false, sha: mergeSha });
        assert.deepEqual(fixture.writes(), [{ method: 'POST', path: `/repos/${repository}/merges`, body: {
            base: 'develop', head: sha, commit_message: `chore: merge ${plan.tag} release into develop`,
        } }]);
    }
});

test('an already merged release succeeds without creating another commit', async () => {
    const fixture = githubFixture({ merge: null });
    assert.deepEqual(await syncDevelop(fixture.api, repository, plan), { alreadyMerged: true, sha: undefined });
    assert.equal(fixture.writes().length, 1);
});

test('missing, draft and mismatched releases cannot change develop', async () => {
    for (const release of [null, { tag_name: plan.tag, draft: true }, { tag_name: 'v9.0.0', draft: false }]) {
        const fixture = githubFixture({ release });
        await assert.rejects(syncDevelop(fixture.api, repository, plan), /must be published/);
        assert.deepEqual(fixture.writes(), []);
    }
});

test('a missing or moved tag cannot change develop', async () => {
    for (const tag of [null, { object: { type: 'commit', sha: nextSha } }]) {
        const fixture = githubFixture({ tag });
        await assert.rejects(syncDevelop(fixture.api, repository, plan), /does not match/);
        assert.deepEqual(fixture.writes(), []);
    }
});

test('annotated tags resolve to the same released commit', async () => {
    const fixture = githubFixture({ tag: { object: { type: 'tag', sha: nextSha } } });
    assert.equal((await syncDevelop(fixture.api, repository, plan)).alreadyMerged, false);
    assert.equal(fixture.writes()[0].body.head, sha);
});

test('a released commit outside main cannot change develop', async () => {
    for (const mainStatus of ['behind', 'diverged', 'unavailable']) {
        const fixture = githubFixture({ mainStatus });
        await assert.rejects(syncDevelop(fixture.api, repository, plan), /no longer contained in main/);
        assert.deepEqual(fixture.writes(), []);
    }
});

test('a conflict opens a PR from the published commit without forcing or approving a merge', async () => {
    const fixture = githubFixture({ failure: conflict() });
    assert.deepEqual(await syncDevelop(fixture.api, repository, plan), { alreadyCreated: false, url: pullUrl });
    assert.deepEqual(fixture.writes().map(call => call.path), [
        `/repos/${repository}/merges`, `/repos/${repository}/git/refs`, `/repos/${repository}/pulls`,
    ]);
    assert.deepEqual(fixture.writes()[1].body, { ref: `refs/heads/${branch}`, sha });
    assert.equal(fixture.writes()[2].body.base, 'develop');
    assert.equal(fixture.writes()[2].body.head, branch);
    assert.ok(fixture.writes()[2].body.body.includes(releaseUrl));
    assert.ok(fixture.writes()[2].body.body.includes(sha));
    assert.ok(!fixture.writes()[2].body.body.includes(nextSha));

    assert.deepEqual(await syncDevelop(fixture.api, repository, plan), { alreadyCreated: true, url: pullUrl });
    assert.equal(fixture.writes().length, 4);
    assert.equal(fixture.writes()[3].path, `/repos/${repository}/merges`);
});

test('rerunning a conflict keeps an existing PR and its manual resolution commits', async () => {
    const fixture = githubFixture({ failure: conflict(), branch: { object: { type: 'commit', sha: nextSha } },
        pulls: [{ state: 'open', html_url: pullUrl, head: { sha: nextSha } }] });
    assert.deepEqual(await syncDevelop(fixture.api, repository, plan), { alreadyCreated: true, url: pullUrl });
    assert.equal(fixture.writes().length, 1);
});

test('closed PRs are not silently replaced or reported as a successful merge', async () => {
    for (const merged_at of [null, '2026-01-01T00:00:00Z']) {
        const fixture = githubFixture({ failure: conflict(), pulls: [{ state: 'closed', merged_at, html_url: pullUrl }] });
        await assert.rejects(syncDevelop(fixture.api, repository, plan), merged_at ? /still conflicts/ : /closed without merging/);
        assert.equal(fixture.writes().length, 1);
    }
});

test('a branch left after a failed PR creation can be reused without rewriting it', async () => {
    const options = { failure: conflict(), pullFailure: new Error('PR creation denied') };
    const fixture = githubFixture(options);
    await assert.rejects(syncDevelop(fixture.api, repository, plan), /PR creation denied/);
    delete options.pullFailure;
    assert.deepEqual(await syncDevelop(fixture.api, repository, plan), { alreadyCreated: false, url: pullUrl });
    assert.equal(fixture.writes().filter(call => call.path.endsWith('/git/refs')).length, 1);
});

test('an unexpected existing branch cannot be overwritten or used for a new PR', async () => {
    const fixture = githubFixture({ failure: conflict(), branch: { object: { type: 'commit', sha: nextSha } } });
    await assert.rejects(syncDevelop(fixture.api, repository, plan), /was not overwritten/);
    assert.equal(fixture.writes().length, 1);
});

test('unavailable or malformed PR responses remain failures', async () => {
    for (const options of [{ pulls: null }, { pulls: [{ state: 'open' }] }, { pull: {} }]) {
        const fixture = githubFixture({ failure: conflict(), ...options });
        await assert.rejects(syncDevelop(fixture.api, repository, plan), /could not be (inspected|confirmed)/);
    }
});

test('a failed branch creation cannot be followed by PR creation', async () => {
    const fixture = githubFixture({ failure: conflict(), branchFailure: new Error('Branch creation denied') });
    await assert.rejects(syncDevelop(fixture.api, repository, plan), /Branch creation denied/);
    assert.equal(fixture.writes().some(call => call.path.endsWith('/pulls')), false);
});

test('permission, missing branch and server errors remain failures', async () => {
    for (const status of [403, 404, 422, 503]) {
        const failure = Object.assign(new Error(`HTTP ${status}`), { status });
        const fixture = githubFixture({ failure });
        await assert.rejects(syncDevelop(fixture.api, repository, plan), error => error === failure);
        assert.equal(fixture.writes().length, 1);
    }
});

test('an unconfirmed merge result is not reported as success or retried', async () => {
    const fixture = githubFixture({ merge: {} });
    await assert.rejects(syncDevelop(fixture.api, repository, plan), /could not be confirmed/);
    assert.equal(fixture.writes().length, 1);
});

// CLIとHTTPの境界も検証し、空の204応答とGitHubのエラーを区別する。
async function commandFixture(context, mergeStatus, pullStatus = 201) {
    const directory = await mkdtemp(join(tmpdir(), 'clutch-release-sync-'));
    context.after(() => rm(directory, { recursive: true, force: true }));
    const eventPath = join(directory, 'event.json');
    const summary = join(directory, 'summary.md');
    const event = {
        action: 'closed', repository: { full_name: repository },
        pull_request: {
            merged: true, merge_commit_sha: sha,
            base: { ref: 'main', repo: { full_name: repository } },
            head: { ref: `release/${plan.tag}`, repo: { full_name: repository } },
        },
    };
    await writeFile(eventPath, JSON.stringify(event));
    await writeFile(summary, '');
    const fixture = githubFixture();
    const server = createServer(async (request, response) => {
        try {
            let body = '';
            for await (const chunk of request) body += chunk;
            const result = await fixture.api(request.method, request.url, body ? JSON.parse(body) : undefined);
            const status = request.url.endsWith('/merges') ? mergeStatus
                : request.method === 'POST' ? (request.url.endsWith('/pulls') ? pullStatus : 201)
                    : result === null ? 404 : 200;
            response.writeHead(status, { 'Content-Type': 'application/json' });
            response.end(status === 204 ? undefined : JSON.stringify(result));
        } catch {
            response.writeHead(500);
            response.end();
        }
    });
    context.after(() => new Promise(resolve => server.close(resolve)));
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const run = () => promisify(execFile)(process.execPath, [fileURLToPath(new URL('./release.mjs', import.meta.url)), 'sync-develop'], {
        env: { ...process.env, GITHUB_EVENT_PATH: eventPath, GITHUB_STEP_SUMMARY: summary,
            GH_TOKEN: 'fixture', GITHUB_API_URL: `http://127.0.0.1:${server.address().port}`, GITHUB_SERVER_URL: 'https://github.com' },
        timeout: 10000,
    });
    return { run, summary, eventPath, event, ...fixture };
}

test('sync command handles real HTTP 201 and empty 204 responses', async context => {
    for (const status of [201, 204]) {
        const fixture = await commandFixture(context, status);
        await fixture.run();
        assert.match(await readFile(fixture.summary, 'utf8'), status === 201 ? /^Merged:/ : /^Already merged:/);
        assert.equal(fixture.writes()[0].body.head, sha);
    }
});

test('sync command turns real HTTP 409 into a conflict resolution PR', async context => {
    const fixture = await commandFixture(context, 409);
    await fixture.run();
    assert.match(await readFile(fixture.summary, 'utf8'), /^Created conflict resolution PR:/);
    assert.equal(fixture.writes()[1].body.sha, sha);
    assert.equal(fixture.writes()[2].body.head, branch);

    await fixture.run();
    assert.match(await readFile(fixture.summary, 'utf8'), /Existing conflict resolution PR:/);
    assert.equal(fixture.writes().filter(call => call.path.endsWith('/pulls')).length, 1);
});

test('sync command reports denied writes without opening a PR or a success summary', async context => {
    for (const status of [403, 404, 422, 503]) {
        const fixture = await commandFixture(context, status);
        await assert.rejects(fixture.run(), error => {
            assert.equal(error.code, 1, error.stderr);
            assert.match(error.stderr, new RegExp(`HTTP ${status}`));
            return true;
        });
        assert.equal(await readFile(fixture.summary, 'utf8'), '');
        assert.equal(fixture.writes().length, 1);
    }
});

test('sync command fails if GitHub disallows PR creation after a conflict', async context => {
    const fixture = await commandFixture(context, 409, 403);
    await assert.rejects(fixture.run(), error => {
        assert.equal(error.code, 1, error.stderr);
        assert.match(error.stderr, /\/pulls failed: HTTP 403/);
        return true;
    });
    assert.equal(await readFile(fixture.summary, 'utf8'), '');
});

test('sync command refuses ordinary PRs before contacting GitHub', async context => {
    const fixture = await commandFixture(context, 201);
    fixture.event.pull_request.head.ref = 'feature/example';
    await writeFile(fixture.eventPath, JSON.stringify(fixture.event));
    await assert.rejects(fixture.run(), /merged release/);
    assert.deepEqual(fixture.calls, []);
});
