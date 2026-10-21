/** 手書きCSSの構文・保守性・配置を、警告を残さず検査します。 */
export default {
    extends: ['stylelint-config-standard'],
    plugins: ['@stylistic/stylelint-plugin', './eng/css/index.mjs'],
    ignoreFiles: ['**/node_modules/**', '**/bin/**', '**/obj/**', '**/vendor/**', '.work/**', 'excludes/**', 'artifacts/**'],
    reportDescriptionlessDisables: true,
    reportInvalidScopeDisables: true,
    reportNeedlessDisables: true,
    ignoreDisables: true,
    rules: {
        'declaration-property-value-no-unknown': true,
        'declaration-block-no-duplicate-custom-properties': true,
        'selector-max-specificity': '0,6,3',
        'selector-max-id': 0,
        'max-nesting-depth': 3,
        'number-max-precision': 6,
        'no-descending-specificity': null,
        'declaration-block-no-redundant-longhand-properties': null,
        'media-feature-range-notation': 'prefix',
        'rule-empty-line-before': ['always', { except: ['first-nested'], ignore: ['after-comment'] }],
        'quality/important-reason': true,
        'quality/comments': true,
        '@stylistic/block-opening-brace-space-before': 'always',
        '@stylistic/block-opening-brace-newline-after': 'always',
        '@stylistic/block-closing-brace-newline-before': 'always',
        '@stylistic/block-closing-brace-newline-after': 'always',
        '@stylistic/declaration-block-semicolon-newline-after': 'always',
        '@stylistic/declaration-block-trailing-semicolon': 'always',
        '@stylistic/declaration-colon-space-after': 'always-single-line',
        '@stylistic/declaration-colon-space-before': 'never',
        '@stylistic/declaration-bang-space-before': 'always',
        '@stylistic/declaration-bang-space-after': 'never',
        '@stylistic/selector-list-comma-newline-after': 'always',
        '@stylistic/selector-combinator-space-before': 'always',
        '@stylistic/selector-combinator-space-after': 'always',
        '@stylistic/function-comma-space-after': 'always-single-line',
        '@stylistic/value-list-comma-space-after': 'always-single-line',
        '@stylistic/media-feature-colon-space-after': 'always',
        '@stylistic/media-query-list-comma-space-after': 'always',
        '@stylistic/at-rule-name-space-after': 'always-single-line',
        '@stylistic/number-leading-zero': 'always',
        '@stylistic/no-eol-whitespace': true,
        '@stylistic/no-missing-end-of-source-newline': true,
        '@stylistic/max-empty-lines': 1,
        '@stylistic/indentation': 4
    },
    overrides: [{
        files: ['**/*.razor.css'],
        rules: {
            'selector-pseudo-element-no-unknown': [true, { ignorePseudoElements: ['deep'] }],
            'selector-disallowed-list': [':root', 'html', 'body']
        }
    }]
};
