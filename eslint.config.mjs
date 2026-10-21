import js from '@eslint/js';
import stylistic from '@stylistic/eslint-plugin';
import globals from 'globals';
import ts from 'typescript-eslint';
import quality from './eng/javascript/index.mjs';

/** 手書きの製品・試験・検証ツールへ同じ構文検査を適用する対象です。 */
const sources = ['src/**/*.{js,mjs}', 'tests/**/*.{js,mjs,cjs}', 'installer/**/*.mjs', 'eng/{javascript,css}/**/*.mjs', 'eslint.config.mjs', 'stylelint.config.mjs'];

/** 既存のclassic scriptはグローバル登録を維持し、moduleへ暗黙変更しません。 */
const scripts = ['browser', 'browser-startup', 'editor-store', 'operation-store', 'service-worker', 'service-worker.published'].map(name => `src/ReArtWord.Contents.BackgroundService.Client/wwwroot/${name}.js`);

/** 実行環境と意味のある検査を固定し、警告を残さないESLint設定です。 */
export default [
    { ignores: ['**/node_modules/**', '**/bin/**', '**/obj/**', '**/vendor/**', '.work/**', 'excludes/**', 'artifacts/**', '.git/**'] },
    { files: ['src/**/*.html', 'src/**/*.razor'], plugins: { quality }, processor: 'quality/markup' },
    {
        files: sources,
        plugins: { '@stylistic': stylistic, quality },
        languageOptions: { ecmaVersion: 'latest' },
        linterOptions: { noInlineConfig: true, reportUnusedDisableDirectives: 'error' },
        rules: {
            ...js.configs.recommended.rules,
            'no-var': 'error',
            'prefer-const': 'error',
            'eqeqeq': ['error', 'always', { null: 'ignore' }],
            'curly': ['error', 'all'],
            'no-constant-condition': ['error', { checkLoops: 'all' }],
            'no-eval': 'error',
            'no-implied-eval': 'error',
            'no-new-func': 'error',
            'no-throw-literal': 'error',
            'no-return-assign': ['error', 'always'],
            'no-sequences': 'error',
            'no-unused-expressions': 'error',
            'no-promise-executor-return': 'error',
            'no-duplicate-imports': 'error',
            'no-undef-init': 'error',
            'no-await-in-loop': 'off',
            'no-unused-vars': ['error', { args: 'after-used', caughtErrors: 'none', ignoreRestSiblings: true }],
            '@stylistic/brace-style': ['error', 'allman'],
            '@stylistic/indent': ['error', 4, { SwitchCase: 1 }],
            '@stylistic/semi': ['error', 'always'],
            '@stylistic/comma-spacing': 'error',
            '@stylistic/key-spacing': 'error',
            '@stylistic/keyword-spacing': 'error',
            '@stylistic/space-infix-ops': 'error',
            '@stylistic/arrow-spacing': 'error',
            '@stylistic/space-before-blocks': 'error',
            '@stylistic/object-curly-spacing': ['error', 'always'],
            '@stylistic/no-multi-spaces': 'error',
            '@stylistic/no-multiple-empty-lines': ['error', { max: 1, maxBOF: 0, maxEOF: 0 }],
            '@stylistic/no-tabs': 'error',
            '@stylistic/no-trailing-spaces': 'error',
            '@stylistic/eol-last': ['error', 'always'],
            '@stylistic/max-statements-per-line': ['error', { max: 1 }],
            'quality/documentation': 'error',
            'quality/control-flow': 'error',
            'quality/comment-governance': 'error',
            'quality/statement-layout': 'error',
            'quality/compact-comment': 'error',
            'quality/comment-spacing': 'error'
        }
    },
    { files: ['src/**/*.{js,mjs}'], languageOptions: { globals: globals.browser }, rules: { 'no-console': 'error', 'max-depth': ['error', 4], 'max-params': ['error', 6], 'complexity': ['error', { max: 20, variant: 'modified' }], 'quality/class-structure': 'error', 'quality/fetch-signal': 'error', 'quality/async-name': 'error', 'quality/conditional-strings': 'error', 'camelcase': ['error', { properties: 'never' }] } },
    { files: ['src/**/Scripting/JavaScript/*.mjs'], languageOptions: { globals: { ...Object.fromEntries(Object.keys(globals.browser).map(name => [name, 'off'])), ...globals.node } } },
    { files: scripts, languageOptions: { sourceType: 'script' } },
    { files: ['src/**/service-worker*.js', 'src/**/remote-desktop-worker.js'], languageOptions: { globals: { ...Object.fromEntries(Object.keys(globals.browser).map(name => [name, 'off'])), ...globals.worker } } },
    { files: ['src/**/remote-desktop-audio-worklet.js'], languageOptions: { globals: { ...Object.fromEntries(Object.keys(globals.browser).map(name => [name, 'off'])), ...globals.audioWorklet } } },
    { files: ['tests/**/*.{js,mjs,cjs}', 'installer/**/*.mjs', 'eng/{javascript,css}/**/*.mjs', 'eslint.config.mjs', 'stylelint.config.mjs'], languageOptions: { globals: globals.node } },
    { files: ['tests/ReArtWord.Contents.BackgroundService.Client.Tests/{Client,Styles}/**/*.{js,mjs,cjs}'], languageOptions: { globals: globals.browser } },
    {
        files: ['src/**/*.{js,mjs}'],
        languageOptions: { parser: ts.parser, parserOptions: { project: './tsconfig.eslint.json', tsconfigRootDir: import.meta.dirname } },
        plugins: { '@typescript-eslint': ts.plugin },
        rules: {
            '@typescript-eslint/no-floating-promises': ['error', { ignoreVoid: false, ignoreIIFE: false }],
            '@typescript-eslint/no-misused-promises': 'error',
            '@typescript-eslint/await-thenable': 'error',
            '@typescript-eslint/only-throw-error': 'error'
        }
    }
];
