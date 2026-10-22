// GitHubの変更一覧を保ち、同じCI実行の結果へのリンクを末尾へ添えます。
export function releaseNotes(generatedNotes, runUrl) {
    const sections = [];
    if (generatedNotes?.trim()) sections.push(generatedNotes.trimEnd());
    if (runUrl) sections.push(`## Build Results\nCheck the CI results: ${runUrl}`);
    return sections.length ? `${sections.join('\n\n')}\n` : '';
}
