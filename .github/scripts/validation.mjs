import { appendFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const stages = {
    release_rules: 'Release and validation rules',
    media_tools: 'Media test tools',
    platform_tests: 'Versioning and platform scripts',
    unit_tests: 'Unit tests',
    client_tests: 'Client tests',
    installer_tests: 'Installer regression tests',
    integration_tests: 'Integration tests',
    performance_tests: 'Performance tests',
    native_capture_tests: 'Packaged native capture tests',
    install_tests: 'Installation lifecycle tests',
    avx2_tests: 'Installation without AVX2',
};

// continue-on-errorで成功へ変換される前の成否を集計し、前のジョブの失敗も引き継ぎます。
export function validationReport(steps, previous = {}) {
    const results = Object.entries(stages).map(([id, name]) => {
        const outcomes = [steps[id]?.outcome ?? 'skipped', previous[id] ?? 'skipped'];
        const precedence = ['failure', 'cancelled', 'success', 'skipped'];
        for (const outcome of outcomes) {
            if (!precedence.includes(outcome)) {
                throw new Error(`Unexpected test outcome for ${id}: ${outcome}`);
            }
        }
        const outcome = precedence.find(value => outcomes.includes(value));
        return { id, name, outcome };
    });
    return { failed: results.some(result => result.outcome === 'failure' || result.outcome === 'cancelled'), results };
}

async function main() {
    const report = validationReport(JSON.parse(process.env.VALIDATION_STEPS),
        JSON.parse(process.env.PREVIOUS_VALIDATION_RESULTS ?? '{}'));
    const outcomes = Object.fromEntries(report.results.map(result => [result.id, result.outcome]));
    await appendFile(process.env.GITHUB_OUTPUT, `tests_failed=${report.failed}\ntest_results=${JSON.stringify(outcomes)}\n`);
    const summary = ['### Test results', '', '| Stage | Result |', '| --- | --- |',
        ...report.results.map(result => `| ${result.name} | ${result.outcome} |`), ''];
    await appendFile(process.env.GITHUB_STEP_SUMMARY, summary.join('\n'));
    if (report.failed) console.error('::warning::Validation stages failed. Review the test results before using the build.');
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
    await main();
}
