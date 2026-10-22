import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { planRelease, publishRelease, releaseAssets } from './release.mjs';

const repository = 'owner/project';
const sha = 'a'.repeat(40);
const otherSha = 'b'.repeat(40);
const plan = { tag: 'v1.2.3', version: '1.2.3', sha, prerelease: false };
const runUrl = 'https://github.com/owner/project/actions/runs/12345';

function event(branch = `release/${plan.tag}`) {
    return {
        action: 'closed', repository: { full_name: repository },
        pull_request: {
            merged: true, merge_commit_sha: sha,
            base: { ref: 'main', repo: { full_name: repository } },
            head: { ref: branch, repo: { full_name: repository } },
        },
    };
}

test('merged release branches strip the prefix and distinguish prereleases', () => {
    assert.deepEqual(planRelease(event()), plan);
    for (const version of ['0.1.0-alpha', '0.2.0-rc.1', '1.2.3-alpha-beta', '1.2.3-0']) {
        assert.deepEqual(planRelease(event(`release/v${version}`)), { tag: `v${version}`, version, sha, prerelease: true });
    }
});

test('legacy names, ordinary branches and malformed release versions never release', () => {
    for (const name of [
        'v1.2.3', 'v0.1.0-alpha', 'develop', 'feature/example', 'hotfix/v1.2.3', 'feature/release/v1.2.3',
        'release/', 'release/1.2.3', 'release/v1.2', 'release/v01.2.3', 'release/v1.02.3', 'release/v1.2.03',
        'release/v1.2.3-', 'release/v1.2.3-01', 'release/v1.2.3-rc.01', 'release/v1.2.3-rc..1',
        'release/v1.2.3+build', 'release/v1.2.3/extra', 'release/v1.2.3;echo unsafe', 'release/v1.2.3\n',
    ]) {
        assert.equal(planRelease(event(name)), null, name);
    }
});

test('closed without merging, other bases and forks never release', () => {
    for (const modify of [
        value => { value.pull_request.merged = false; },
        value => { value.action = 'opened'; },
        value => { value.pull_request.base.ref = 'develop'; },
        value => { value.pull_request.base.repo.full_name = 'someone/fork'; },
        value => { value.pull_request.head.repo.full_name = 'someone/fork'; },
    ]) {
        const value = event();
        modify(value);
        assert.equal(planRelease(value), null);
    }
});

test('a release requires a concrete merged commit', () => {
    const value = event();
    value.pull_request.merge_commit_sha = null;
    assert.throws(() => planRelease(value), /commit SHA/);
});

test('plan command writes tag and version outputs for release branches only', async context => {
    const directory = await mkdtemp(join(tmpdir(), 'clutch-release-plan-'));
    context.after(() => rm(directory, { recursive: true, force: true }));
    const eventFile = join(directory, 'event.json');
    const outputFile = join(directory, 'output');
    const script = fileURLToPath(new URL('./release.mjs', import.meta.url));
    for (const [branch, expected] of [
        ['release/v1.2.3', { eligible: 'true', tag: 'v1.2.3', version: '1.2.3', sha, prerelease: 'false' }],
        ['release/v0.1.0-alpha', { eligible: 'true', tag: 'v0.1.0-alpha', version: '0.1.0-alpha', sha, prerelease: 'true' }],
        ['v0.1.0-alpha', { eligible: 'false' }],
        ['release/v1.2.3-01', { eligible: 'false' }],
    ]) {
        await writeFile(eventFile, JSON.stringify(event(branch)));
        await writeFile(outputFile, '');
        execFileSync(process.execPath, [script, 'plan'], {
            env: { ...process.env, GITHUB_EVENT_PATH: eventFile, GITHUB_OUTPUT: outputFile },
        });
        const output = await readFile(outputFile, 'utf8');
        assert.deepEqual(Object.fromEntries(output.trim().split('\n').map(line => line.split('='))), expected, branch);
    }
});

function githubFixture(existing = null, reference = null) {
    const calls = [];
    let release = existing;
    const api = async (method, path, body) => {
        calls.push({ method, path, body });
        if (method === 'GET' && path.includes('/git/ref/')) return reference;
        if (method === 'GET' && path.includes('/git/tags/')) return { object: { type: 'commit', sha } };
        if (method === 'GET' && path.includes('/releases?')) return release ? [release] : [];
        if (method === 'POST' && path.endsWith('/generate-notes')) return { body: 'Generated notes' };
        if (method === 'POST' && path.endsWith('/releases')) {
            release = { id: 7, ...body, html_url: 'https://github.com/owner/project/releases/tag/v1.2.3' };
            return release;
        }
        if (method === 'PATCH') {
            release = { ...release, ...body };
            return release;
        }
        throw new Error(`Unexpected request: ${method} ${path}`);
    };
    return { api, calls, current: () => release };
}

test('publishing creates a draft, uploads binary and source assets, then publishes', async () => {
    const fixture = githubFixture();
    const files = ['setup.zip', 'setup.zip.sha256', 'source.zip', 'source.zip.sha256'];
    const result = await publishRelease(fixture.api, async (tag, assets, repo) => {
        assert.equal(fixture.current().draft, true);
        assert.equal(fixture.current().target_commitish, sha);
        assert.equal(fixture.current().body, `Generated notes\n\n## Build Results\nCheck the CI results: ${runUrl}\n`);
        assert.equal(tag, plan.tag);
        assert.equal(repo, repository);
        assert.deepEqual(assets, files);
    }, repository, plan, files, runUrl);
    assert.equal(result.alreadyPublished, false);
    assert.equal(fixture.current().draft, false);
    assert.equal(fixture.current().make_latest, 'legacy');
    assert.equal(fixture.current().prerelease, false);
    assert.equal(fixture.current().body, `Generated notes\n\n## Build Results\nCheck the CI results: ${runUrl}\n`);
});

test('failed upload leaves a draft and rerunning resumes that same release', async () => {
    const fixture = githubFixture();
    const previous = 'https://github.com/owner/project/actions/runs/100';
    const current = 'https://github.com/owner/project/actions/runs/101';
    await assert.rejects(publishRelease(fixture.api, async () => { throw new Error('Upload failed'); }, repository, plan, [], previous), /Upload failed/);
    assert.equal(fixture.current().draft, true);
    assert.ok(fixture.current().body.includes(previous));
    assert.equal(fixture.calls.some(call => call.method === 'PATCH'), false);
    await publishRelease(fixture.api, async () => {}, repository, plan, [], current);
    assert.equal(fixture.current().draft, false);
    assert.ok(fixture.current().body.includes(current));
    assert.ok(!fixture.current().body.includes(previous));
    assert.equal(fixture.calls.filter(call => call.method === 'POST' && call.path.endsWith('/releases')).length, 1);
});

test('a published version is not changed on rerun', async () => {
    const fixture = githubFixture({ tag_name: plan.tag, draft: false, html_url: 'release' }, { object: { type: 'commit', sha } });
    const result = await publishRelease(fixture.api, async () => assert.fail('No upload expected'), repository, plan, []);
    assert.equal(result.alreadyPublished, true);
    assert.equal(fixture.calls.every(call => call.method === 'GET'), true);
});

test('a conflicting tag or draft cannot be repointed', async () => {
    for (const fixture of [
        githubFixture(null, { object: { type: 'commit', sha: otherSha } }),
        githubFixture({ tag_name: plan.tag, draft: true, target_commitish: otherSha }),
    ]) {
        await assert.rejects(publishRelease(fixture.api, async () => assert.fail('No upload expected'), repository, plan, []), /different commit/);
        assert.equal(fixture.calls.every(call => call.method === 'GET'), true);
    }
});

test('annotated tags are resolved and suffix versions remain prereleases', async () => {
    const fixture = githubFixture(null, { object: { type: 'tag', sha: otherSha } });
    await publishRelease(fixture.api, async () => {}, repository, { ...plan, prerelease: true }, []);
    assert.equal(fixture.current().prerelease, true);
    assert.equal(fixture.current().make_latest, 'false');
});

test('release lookup follows pagination', async () => {
    const fixture = githubFixture({ tag_name: plan.tag, draft: true, target_commitish: sha, id: 9 });
    const api = async (method, path, body) => {
        if (path.endsWith('page=1')) return Array.from({ length: 100 }, (_, index) => ({ tag_name: `old-${index}` }));
        return fixture.api(method, path, body);
    };
    await publishRelease(api, async () => {}, repository, plan, []);
    assert.equal(fixture.current().id, 9);
});

test('an unavailable release lookup cannot start publication', async () => {
    await assert.rejects(publishRelease(async () => null, async () => assert.fail('No upload expected'), repository, plan, []), /could not be inspected/);
});

test('assets require the matching checksum and archive name', async context => {
    const directory = await mkdtemp(join(tmpdir(), 'clutch-release-assets-'));
    context.after(() => rm(directory, { recursive: true, force: true }));
    const names = [`Clutch-CS-Setup-${plan.tag}-win-x64.zip`, `Clutch-CS-Source-${plan.tag}.zip`];
    const files = [];
    const bytes = Buffer.from('archive fixture');
    for (const name of names) {
        const file = join(directory, name);
        await writeFile(file, bytes);
        await writeFile(`${file}.sha256`, `${createHash('sha256').update(bytes).digest('hex')}  ${name}\n`);
        files.push(file, `${file}.sha256`);
    }
    assert.deepEqual(await releaseAssets(directory, plan.tag), files);
    for (const name of names) {
        const file = join(directory, name);
        await writeFile(file, 'corrupted');
        await assert.rejects(releaseAssets(directory, plan.tag), /checksum/);
        await writeFile(file, bytes);
    }
    await rm(join(directory, names[1]));
    await assert.rejects(releaseAssets(directory, plan.tag), /Source/);
});
