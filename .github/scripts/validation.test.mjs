import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { validationReport } from './validation.mjs';

test('continued test failures retain their original outcome', () => {
    const report = validationReport({ unit_tests: { outcome: 'failure', conclusion: 'success' },
        client_tests: { outcome: 'success', conclusion: 'success' } });
    assert.equal(report.failed, true);
    assert.equal(report.results.find(result => result.id === 'unit_tests').outcome, 'failure');
});

test('selected passing tests and skipped stages do not report a failure', () => {
    assert.equal(validationReport({ unit_tests: { outcome: 'success' }, integration_tests: { outcome: 'skipped' } }).failed, false);
});

test('cancelled validation stages are not counted as passing', () => {
    assert.equal(validationReport({ install_tests: { outcome: 'cancelled' } }).failed, true);
});

test('build failures are separate from test results and malformed outcomes are rejected', () => {
    assert.equal(validationReport({ build: { outcome: 'failure' } }).failed, false);
    assert.throws(() => validationReport({ unit_tests: { outcome: 'unknown' } }), /Unexpected test outcome/u);
});

test('report command exposes failures without preventing artifact generation', async context => {
    const directory = await mkdtemp(join(tmpdir(), 'clutch-validation-report-'));
    context.after(() => rm(directory, { recursive: true, force: true }));
    const output = join(directory, 'outputs');
    const summary = join(directory, 'summary');
    execFileSync(process.execPath, [fileURLToPath(new URL('./validation.mjs', import.meta.url))], {
        stdio: 'pipe',
        env: { ...process.env, VALIDATION_STEPS: JSON.stringify({ unit_tests: { outcome: 'failure', conclusion: 'success' } }),
            GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: summary },
    });
    const outputs = Object.fromEntries((await readFile(output, 'utf8')).trim().split('\n').map(line => {
        const separator = line.indexOf('=');
        return [line.slice(0, separator), line.slice(separator + 1)];
    }));
    assert.equal(outputs.tests_failed, 'true');
    const outcomes = JSON.parse(outputs.test_results);
    assert.equal(outcomes.unit_tests, 'failure');
    assert.equal(outcomes.integration_tests, 'skipped');
    assert.ok((await readFile(summary, 'utf8')).length > 0);
});
