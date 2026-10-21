const testGroups = [
    { name: '単体テスト（Core・Client・Installer）', stages: ['unit_tests', 'client_tests', 'installer_tests'] },
    { name: '結合テスト', stages: ['integration_tests'] },
    { name: 'パフォーマンステスト', stages: ['performance_tests'] },
    { name: 'インストール検証', stages: ['install_tests', 'avx2_tests'] },
    { name: 'スクリプト・検証環境の確認', stages: ['release_rules', 'platform_tests', 'media_tools'] },
];

const outcomeIcons = {
    success: '✅',
    failure: '❌',
    cancelled: '⏹️',
    skipped: '➖',
    partial: '⚠️',
    unknown: '❔',
};

// 一部の成功だけで区分全体を通過扱いにしません。
export function summarizeTests(outcomes = {}) {
    return testGroups.map(group => {
        const values = group.stages.map(stage => outcomes?.[stage]);
        let outcome = 'success';
        if (values.includes('failure')) outcome = 'failure';
        else if (values.includes('cancelled')) outcome = 'cancelled';
        else if (values.some(value => !['success', 'skipped'].includes(value))) outcome = 'unknown';
        else if (values.every(value => value === 'skipped')) outcome = 'skipped';
        else if (values.includes('skipped')) outcome = 'partial';
        return { name: group.name, outcome };
    });
}

// 配布物の案内と、同じCI実行から渡された検証結果を本文にまとめます。
export function releaseNotes(plan, generatedNotes, validation = {}) {
    const results = summarizeTests(validation.outcomes);
    const lines = [`Clutch-CS ${plan.tag} のWindows向け配布物です。`, ''];
    if (plan.prerelease) {
        lines.push('このバージョンは開発中のプレリリースです。導入前に、以下のテスト結果と変更内容を確認してください。', '');
    }
    lines.push('## テスト結果', '', '| 種類 | 結果 |', '| --- | --- |',
        ...results.map(result => `| ${result.name} | ${outcomeIcons[result.outcome]} |`), '');
    if (results.some(result => result.outcome !== 'success')) {
        lines.push('すべての検証の通過は確認できていません。失敗・中断・未実行などの詳細を確認してから、利用を判断してください。', '');
    }
    lines.push('表はテスト工程ごとの結果です。個別のテストのスキップや詳細はCIの実行結果で確認できます。');
    if (validation.runUrl) lines.push('', `[CIの実行結果を確認する](${validation.runUrl})`);
    lines.push('', '## ダウンロード・インストール', '',
        `下の「Assets」から \`Clutch-CS-Setup-${plan.tag}-win-x64.zip\` をダウンロードし、フォルダー全体を展開してください。展開したフォルダー内の \`Clutch-CS-Setup.exe\` を起動すると、インストールできます。`, '',
        `対応するソースとビルド手順は \`Clutch-CS-Source-${plan.tag}.zip\` に収録しています。再配布する場合は、インストーラーと一緒にこのソースZIPも提供してください。`, '',
        '各ZIPに添付した `.sha256` ファイルで、ダウンロードしたファイルの整合性を確認できます。', '',
        '## 変更内容', '', generatedNotes || '変更内容の自動生成結果はありません。', '');
    return lines.join('\n');
}
