# CSSの配置と静的解析

[ドキュメント一覧](../README.md) · [開発環境とビルド](build.md)

共通パレット・基本要素・複数画面で使うユーティリティーは `Client/wwwroot/app.css` に置きます。共通コントロール、状態表示、ノードの意味色は `wwwroot/styles` に分けます。個別画面の定義を共通CSSへ追記しません。

Razor固有のスタイルは、同じディレクトリの `Component.razor.css` に置きます。SDKのスコープを使用し、子コンポーネントやコードハイライトの生成要素へ届く必要がある場合だけ `::deep` を使用します。`NavLink`、共通アイコン、ダイアログの `ChildContent`、SVG内の入力欄にも所有境界があります。

JavaScriptが構築する画面にはRazorのスコープ属性が付かないため、`wwwroot/remote-desktop.css`、`remote-tools.css`、`host-manager/style.css` を使用します。読込み順は `index.html` と既存のHostManager読込み処理で管理します。追加のCSS結合器や実行時のスコープ属性付与は使用しません。

## 実行

Node.js 24 と、lockfile に固定した Stylelint を使用します。手書きの CSS と Razor CSS を検査し、第三者コード、生成物、作業用ディレクトリは除外します。

最初にリポジトリのルートで `npm ci --ignore-scripts` を実行してください。以後は目的に合うコマンドを選択してください。

| 目的 | コマンド |
| --- | --- |
| JavaScript と CSS を検査 | `npm run lint` |
| CSS だけを検査 | `npm run lint:css` |
| 自動修正できる箇所を修正 | `npm run lint:fix` |
| 検査・処理のテスト | `npm run test:css` |

Web クライアントを含む通常のビルド／発行でもCSSを検査し、警告があれば失敗します。依存関係が未導入の場合も導入手順を表示して失敗します。`lint` はJavaScriptとCSS、`lint:javascript` はJavaScript、`lint:css` はCSSと画面内の固定CSSの配置を検査します。

## 規則

| 分野 | 検査内容 |
| --- | --- |
| 構文と値 | 未知のプロパティ・値・単位、壊れた色、疑似要素、メディア条件、calc、不正な文字列、非推奨の指定 |
| 上書き | 宣言・カスタムプロパティ・セレクター・キーフレームの重複、長い指定を意図せず消す短縮プロパティ |
| 命名と範囲 | クラス・カスタムプロパティ・キーフレームのkebab-case、IDセレクター禁止、詳細度上限 `0,6,3`、ネスト3段以内 |
| コンポーネント | `::deep` はRazor CSSだけで使用可能。Razor CSSへ `:root`・`html`・`body` を直接定義しない |
| 可読性 | 4スペース、宣言ごとの改行、セレクター一覧とブロック間の改行、区切りの空白、末尾セミコロン・改行、連続空行・行末空白の制限 |
| コメント | 本文が1行なら閉じ記号も同じ行。保留には日本語の理由と解消条件。検査のインライン無効化は禁止 |
| 画面との境界 | HTML・Razorのstyle要素と固定style属性は禁止。固定レイアウトの分岐はクラスで表し、実行時の座標・寸法・遅延などは動的styleを維持 |

`!important` は宣言の直前に `例外理由:` と具体的な日本語の説明が必要です。非表示属性や利用者の動きを減らす設定のように、通常の詳細度より優先する必要がある場合に限定します。通常の調整は、所有する要素のクラスと詳細度で解決します。

詳細度が後で小さくなること自体は禁止しません。別のDOMに属する同名要素や状態別ルールまで誤検出するためです。詳細度の上限・重複検査とブラウザーでの表示確認を併用します。長いプロパティを短縮形へまとめることも強制せず、暗黙の初期化を増やしません。宣言順とメディア条件の前後関係は動作の一部として扱います。

適用設定は `stylelint.config.mjs`、汎用の追加規則は `eng/css`、HTML・Razorの配置検査は `eng/javascript/markup.mjs` が所有します。規則に製品の画面名やパスを埋め込みません。

マークアップ検査は固定のstyle属性を対象とし、Razor式やJavaScriptが実行時に組み立てるCSSの値までは判定しません。動的なクラス名があるため、セレクターの未使用判定も機械的な削除条件にはしません。

## 表示の検証

```powershell
# 最初の一回は試験用Chromiumを準備します。
npx playwright install chromium

# SDKが生成した最新のスコープ付きCSSを使用します。
dotnet build src/ReArtWord.Contents.BackgroundService.ClientHost/ReArtWord.Contents.BackgroundService.ClientHost.csproj
npm run verify:css
```

ブラウザー試験は `tests/ReArtWord.Contents.BackgroundService.Client.Tests/Styles` にあります。SDK出力から実際のスコープを読み、子コンポーネント・SVG・JS生成要素を区別した代表DOMで算出スタイルを確認します。1440・900・640pxの幅、ナビゲーション、ノード入力、ダイアログ、RemoteDesktop、HostManager、動きを減らす設定を検証します。

CSSソース文字列の一致を描画確認の代わりにしません。

通常はDebug出力を使用します。Releaseを検証する場合は `CONTENTS_TEST_CONFIGURATION=Release` を指定して `npm run test:styles` を実行します。試験前のビルドが必要です。代表DOMの試験であり、すべての画面状態や実機・ブラウザー差を網羅するものではありません。
