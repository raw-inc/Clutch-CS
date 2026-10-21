import documentation from './rules/documentation.mjs';
import controlFlow from './rules/control-flow.mjs';
import commentGovernance from './rules/comment-governance.mjs';
import classStructure from './rules/class-structure.mjs';
import fetchSignal from './rules/fetch-signal.mjs';
import statementLayout from './rules/statement-layout.mjs';
import asyncName from './rules/async-name.mjs';
import conditionalStrings from './rules/conditional-strings.mjs';
import markup from './markup.mjs';
import compactComment from './rules/compact-comment.mjs';
import commentSpacing from './rules/comment-spacing.mjs';

/** JavaScriptの構文と処理の所有を検査する規則です。 */
export default {
    meta: { name: '@reartword/eslint-plugin-source-quality', version: '1.0.0' },
    processors: { markup },
    rules: { documentation, 'control-flow': controlFlow, 'comment-governance': commentGovernance, 'class-structure': classStructure, 'fetch-signal': fetchSignal,
        'statement-layout': statementLayout, 'async-name': asyncName, 'conditional-strings': conditionalStrings, 'compact-comment': compactComment, 'comment-spacing': commentSpacing }
};
