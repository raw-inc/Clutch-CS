import { test } from 'node:test';
import assert from 'node:assert/strict';
import { releaseNotes } from './release-notes.mjs';

const runUrl = 'https://github.com/owner/project/actions/runs/12345';

test('generated changes, contributors and changelog are preserved before the CI results link', () => {
    const generated = "## What's Changed\n* Fix an update by @contributor in https://github.com/owner/project/pull/12\n\n"
        + '## New Contributors\n* @contributor made their first contribution in https://github.com/owner/project/pull/12\n\n'
        + '**Full Changelog**: https://github.com/owner/project/compare/v1.2.2...v1.2.3';
    assert.equal(releaseNotes(generated + '\n\n', runUrl), `${generated}\n\n## Build Results\nCheck the CI results: ${runUrl}\n`);
});

test('releases without new contributors retain only the sections returned by GitHub', () => {
    const generated = "## What's Changed\n* Another change\n\n**Full Changelog**: https://github.com/owner/project/compare/v1.2.3...v1.2.4";
    assert.equal(releaseNotes(generated, runUrl), `${generated}\n\n## Build Results\nCheck the CI results: ${runUrl}\n`);
});

test('absent notes or CI context do not introduce placeholder release content', () => {
    for (const generated of [undefined, '', '  \n']) {
        assert.equal(releaseNotes(generated, runUrl), `## Build Results\nCheck the CI results: ${runUrl}\n`);
    }
    assert.equal(releaseNotes('Generated changes\n'), 'Generated changes\n');
    assert.equal(releaseNotes(), '');
});
