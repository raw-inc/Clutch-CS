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
    assert.throws(() => validationReport({}, { unit_tests: 'unknown' }), /Unexpected test outcome/u);
});

test('packaging preserves earlier failures and adds installation outcomes', () => {
    const report = validationReport({ install_tests: { outcome: 'failure', conclusion: 'success' },
        avx2_tests: { outcome: 'success' } },
    { unit_tests: 'failure', integration_tests: 'success', performance_tests: 'cancelled', install_tests: 'skipped' });
    const outcomes = Object.fromEntries(report.results.map(result => [result.id, result.outcome]));
    assert.equal(report.failed, true);
    assert.equal(outcomes.unit_tests, 'failure');
    assert.equal(outcomes.integration_tests, 'success');
    assert.equal(outcomes.performance_tests, 'cancelled');
    assert.equal(outcomes.install_tests, 'failure');
    assert.equal(outcomes.avx2_tests, 'success');
});

test('passing or skipped installation tests cannot erase an earlier failure or cancellation', () => {
    for (const earlier of ['failure', 'cancelled']) {
        for (const later of ['success', 'skipped']) {
            const report = validationReport({ install_tests: { outcome: later } }, { install_tests: earlier });
            assert.equal(report.failed, true);
            assert.equal(report.results.find(result => result.id === 'install_tests').outcome, earlier);
        }
    }
    assert.equal(validationReport({ install_tests: { outcome: 'success' } }, { unit_tests: 'success' }).failed, false);
});

test('report commands pass regression outcomes to the package job without preventing artifact generation', async context => {
    const directory = await mkdtemp(join(tmpdir(), 'clutch-validation-report-'));
    context.after(() => rm(directory, { recursive: true, force: true }));
    const summary = join(directory, 'summary');
    let previous;
    for (const steps of [{ unit_tests: { outcome: 'failure', conclusion: 'success' } }, { install_tests: { outcome: 'success' } }]) {
        const output = join(directory, previous ? 'package-outputs' : 'test-outputs');
        const env = { ...process.env, VALIDATION_STEPS: JSON.stringify(steps), GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: summary };
        delete env.PREVIOUS_VALIDATION_RESULTS;
        if (previous) env.PREVIOUS_VALIDATION_RESULTS = previous;
        execFileSync(process.execPath, [fileURLToPath(new URL('./validation.mjs', import.meta.url))], { stdio: 'pipe', env });
        const outputs = Object.fromEntries((await readFile(output, 'utf8')).trim().split('\n').map(line => {
            const separator = line.indexOf('=');
            return [line.slice(0, separator), line.slice(separator + 1)];
        }));
        assert.equal(outputs.tests_failed, 'true');
        const outcomes = JSON.parse(outputs.test_results);
        assert.equal(outcomes.unit_tests, 'failure');
        assert.equal(outcomes.integration_tests, 'skipped');
        assert.equal(outcomes.install_tests, previous ? 'success' : 'skipped');
        previous = outputs.test_results;
    }
    assert.ok((await readFile(summary, 'utf8')).length > 0);
});
