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
    install_tests: 'Installation lifecycle tests',
    avx2_tests: 'Installation without AVX2',
};

// continue-on-errorで成功へ変換される前の成否を集計します。
export function validationReport(steps) {
    const results = Object.entries(stages).map(([id, name]) => {
        const outcome = steps[id]?.outcome ?? 'skipped';
        if (!['success', 'failure', 'cancelled', 'skipped'].includes(outcome)) {
            throw new Error(`Unexpected test outcome for ${id}: ${outcome}`);
        }
        return { id, name, outcome };
    });
    return { failed: results.some(result => result.outcome === 'failure' || result.outcome === 'cancelled'), results };
}

async function main() {
    const report = validationReport(JSON.parse(process.env.VALIDATION_STEPS));
    await appendFile(process.env.GITHUB_OUTPUT, `tests_failed=${report.failed}\n`);
    const summary = ['### Test results', '', '| Stage | Result |', '| --- | --- |',
        ...report.results.map(result => `| ${result.name} | ${result.outcome} |`), ''];
    await appendFile(process.env.GITHUB_STEP_SUMMARY, summary.join('\n'));
    if (report.failed) console.error('::warning::Validation stages failed. Review the test results before using the build.');
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
    await main();
}
