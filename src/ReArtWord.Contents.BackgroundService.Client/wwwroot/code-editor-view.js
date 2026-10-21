import { EditorState, EditorView, EditorSelection, Compartment, keymap, lineNumbers, drawSelection, highlightSpecialChars,
    defaultKeymap, history, historyKeymap, indentWithTab, syntaxHighlighting, indentOnInput, bracketMatching, indentUnit,
    autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap, acceptCompletion, tooltips } from './vendor/code-editor/codemirror.js';
import { editorLanguage, editorIndent, editorIndentation, codeHighlight } from './code-editor-languages.js';
import { editorCompletions } from './code-editor-completions.js';
import { completeDeclaration } from './code-editor-insertion.js';

/** ネイティブ入力に適用されたfieldsetや操作保護も編集禁止として扱います。
 * @returns 利用者が編集できない状態かです。
 */
function isReadOnly(input)
{
    return input.matches(':disabled') || input.readOnly || !!input.closest('[inert]');
}

/** 補完ソースの参照を固定し、照会中の候補を再描画で破棄しません。
 * @returns 一つの言語に対応する拡張です。
 */
function languageSupport(language)
{
    return [editorLanguage(language), editorIndentation(language),
        autocompletion({ override: [editorCompletions(language)], interactionDelay: 0,

            tooltipClass: () => 'contents-code-completion' })];
}

/** ネイティブ入力をBlazorとの境界として保ち、編集履歴とIMEを専用エディターへ委ねます。
 * @returns 画面更新と破棄のための所有ハンドルです。
 */
export function attachEditor(input, preview, language)
{
    const host = document.createElement('div');
    host.className = 'script-editor-surface';
    const container = input.parentElement;
    container.append(host);
    const mode = new Compartment(), indentation = new Compartment(), permission = new Compartment();
    let disposed = false, readOnly = isReadOnly(input), unit = editorIndent(input.value, language);
    let analysisTimer = 0;

    /** 前の編集に対する解析予約を取り消します。 */
    function cancelAnalysis()
    {
        clearTimeout(analysisTimer);
        analysisTimer = 0;
    }

    /** 入力が止まってから解析を通知し、IME変換中の文字列を確定コードと扱いません。 */
    function scheduleAnalysis()
    {
        cancelAnalysis();
        analysisTimer = setTimeout(() =>
        {
            analysisTimer = 0;
            if (disposed || !input.isConnected || isReadOnly(input))
            {
                return;
            }
            if (view.composing)
            {
                scheduleAnalysis();
                return;
            }
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }, 600);
    }

    /** ローカルの編集と外部の草稿置換を混ぜず、新しい文書状態を作ります。
     * @returns 一つのコード欄の編集状態です。
     */
    const state = (text, selection = 0) => EditorState.create({ doc: text, selection: EditorSelection.cursor(selection), extensions: [
        lineNumbers(), drawSelection(), highlightSpecialChars(), history(), indentOnInput(), bracketMatching(), closeBrackets(),
        syntaxHighlighting(codeHighlight), EditorState.tabSize.of(4),
        indentation.of(indentUnit.of(unit)), mode.of(languageSupport(language)),
        permission.of([EditorState.readOnly.of(readOnly), EditorView.editable.of(!readOnly)]),
        EditorView.contentAttributes.of({ 'aria-label': input.getAttribute('aria-label') ?? 'Script code', 'aria-multiline': 'true' }),
        tooltips({ parent: document.body }),
        keymap.of([{ key: 'Tab', run: acceptCompletion }, { key: 'Tab',

            run: view => completeDeclaration(view, language) }, ...closeBracketsKeymap, ...defaultKeymap, ...historyKeymap, ...completionKeymap, indentWithTab,
        { key: 'Escape',

            /** Tabをフォーカス移動へ戻す短い猶予を設けます。
             * @returns 他のEscape処理を妨げないfalseです。
             */
            run(view)
            {
                view.setTabFocusMode(2000);
                return false;
            } }]),
        EditorState.transactionFilter.of(transaction => transaction.docChanged && isReadOnly(input) ? [] : transaction),
        EditorView.updateListener.of(update =>
        {
            if (update.docChanged && !disposed)
            {
                input.value = update.state.doc.toString();
                input.dispatchEvent(new Event('input', { bubbles: true }));
                scheduleAnalysis();
            }
        }),
        EditorView.domEventHandlers({
            /** フォーカス離脱を既存の静的解析へ引き渡します。 */
            blur()
            {
                cancelAnalysis();
                input.dispatchEvent(new FocusEvent('blur'));
            } })
    ] });

    const view = new EditorView({ state: state(input.value, input.selectionStart ?? 0), parent: host });
    input.hidden = true;
    preview.hidden = true;
    container.dataset.editorReady = 'true';

    /** エディター外のロック変更も、ネイティブ入力と同じ時点で反映します。 */
    const permissions = () =>
    {
        const next = isReadOnly(input);
        if (!disposed && next !== readOnly)
        {
            readOnly = next;
            cancelAnalysis();
            view.dispatch({ effects: permission.reconfigure([EditorState.readOnly.of(readOnly), EditorView.editable.of(!readOnly)]) });
        }
    };

    const observer = new MutationObserver(permissions);
    for (let ancestor = input; ancestor; ancestor = ancestor.parentElement)
    {
        observer.observe(ancestor, { attributes: true, attributeFilter: ['disabled', 'readonly', 'inert'] });
    }
    return {
        /** 言語・字下げ・外部Undoを反映し、通常入力ではカーソルと履歴を保持します。 */
        update(nextLanguage)
        {
            if (disposed)
            {
                return;
            }
            permissions();
            const nextUnit = editorIndent(input.value, nextLanguage);
            if (view.state.doc.toString() !== input.value)
            {
                cancelAnalysis();
                language = nextLanguage;
                unit = nextUnit;
                const position = Math.min(view.state.selection.main.head, input.value.length);
                view.setState(state(input.value, position));
            }
            else
            {
                if (language !== nextLanguage)
                {
                    cancelAnalysis();
                    language = nextLanguage;
                    view.dispatch({ effects: mode.reconfigure(languageSupport(language)) });
                }
                if (unit !== nextUnit)
                {
                    unit = nextUnit;
                    view.dispatch({ effects: indentation.reconfigure(indentUnit.of(unit)) });
                }
            }
        },

        /** 入力とレイアウトの監視を終了し、別ノードの編集状態を残しません。 */
        dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            cancelAnalysis();
            observer.disconnect();
            view.destroy();
            host.remove();
            input.hidden = false;
            preview.hidden = false;
            delete container.dataset.editorReady;
        }
    };
}
