# JavaScriptの静的解析

[ドキュメント一覧](../README.md) · [開発環境とビルド](build.md)

JavaScript の静的解析には Node.js 24 と、lockfile に固定した ESLint を使用します。製品・テスト・インストーラーの `.js`／`.mjs`、JavaScript スクリプトノードの解析・実行ホスト、検査用スクリプトを対象にします。第三者ライブラリ、ビルド生成物、作業用ディレクトリは除外します。

最初にリポジトリのルートで `npm ci --ignore-scripts` を実行してください。以後は目的に合うコマンドを選択してください。

| 目的 | コマンド |
| --- | --- |
| JavaScript と CSS を検査 | `npm run lint` |
| JavaScript だけを検査 | `npm run lint:javascript` |
| 自動修正できる箇所を修正 | `npm run lint:fix` |
| 検査・処理のテスト | `npm run test:javascript` |
| 規則のテスト | `npm run test:lint` |
| JavaScript の検査とテスト | `npm run verify:javascript` |

警告もエラーとして扱います。Web クライアントを含む通常の `dotnet build` と `dotnet publish` でも検査します。依存関係が未導入の場合は導入手順を表示して失敗し、検査を省略して成功とはしません。インラインのESLint無効化・ルール上書きは禁止です。適用範囲と実行環境はルートの `eslint.config.mjs` で管理します。

## 規則

`lint` と `lint:fix` は[CSSの検査](css.md)も実行します。JavaScriptだけを検査する場合は `lint:javascript` を使用します。

| 分野 | 検査内容 |
| --- | --- |
| 基本的な誤り | 未定義・未使用変数、重複宣言・import、到達不能コード、不正な比較、Promise executorのasync化や戻り値など |
| 処理の所有 | 型情報で分かる未処理Promise、void用途へ渡すPromise、await対象の不整合、fetchへの取消信号の指定 |
| 説明 | 名前を持つ関数・メソッド・クラスの日本語JSDoc。値またはPromiseを返す関数には日本語の `@returns`。無名コールバックは所属する処理と一緒に読む |
| 命名と規模 | 非同期関数の `Async` 接尾辞、クラスのPascalCase、1ファイル1トップレベルクラス・350行以内、製品コードの深さ4・引数6・複雑度20以内 |
| 制御構文 | 波括弧、無条件・恒偽ループ、3つ以上のelse-if連鎖、比較方向、三項演算子の条件数、判定用文字列の命名 |
| 例外・保留 | 非Errorのthrow、空catchの日本語の理由、TODO/FIXME/FYIの日本語の理由と解消条件、不正なfinally |
| 配置と空白 | Allman形式の波括弧、4スペース、文の改行、宣言の空行、演算子・カンマ・プロパティの空白、連続空行とTabの禁止。本文が1行のブロックコメントは閉じ記号まで同じ行にする |
| 画面との境界 | HTML・Razor内の実行可能なscriptとインラインのイベント属性を外部JavaScriptへ分離します。JSONデータのscriptは対象外 |

JavaScriptの `const` は再代入防止に使用します。`value == null` / `value != null` はnullとundefinedをまとめて確認する用途を維持し、それ以外の等価比較は厳密比較に限定します。`typeof` の固定語彙と空文字の判定には追加の定数を要求しません。終了条件を持つ逐次I/Oの `await` はループ内でも使用できます。

公開済みのinterop名やブラウザーのメソッド名など、変更できない非同期APIは、JSDocの `@contract` に日本語で具体的な理由を書きます。この指定で除外するのは命名検査だけです。Promiseの所有・例外・取消しは引き続き検査します。

コメントの前は原則として空行を1行置き、開き波括弧の直後は空行を置きません。連続する説明コメントは一つのまとまりにし、行末の補足コメントは元の行に残します。クラス・関数の宣言とメソッドの間も空行を1行置きます。`export` 付きの宣言、変数へ代入した関数、オブジェクト内のメソッドにも適用し、宣言に付いた説明コメントの前を区切ります。JSDocの字下げは説明対象の宣言に揃えます。これらの配置規則は `npm run lint:fix` で修正できます。

## 検査の境界

ブラウザー、Worker、AudioWorklet、Node.jsはそれぞれのグローバルを指定します。classic scriptの公開方法をmoduleへ変換するものではありません。ブラウザー製品コードとNode.jsのスクリプトホストでは、JavaScriptから推定した型情報を非同期処理の検査に使います。TypeScriptへの変換や、製品全体の型検査完了を意味しません。

利用者がワークフローへ記入したスクリプトには、構文とJSON入出力契約を確認する専用解析を使用します。

変数や引数で受け取るfetch options、動的に差し替える関数、外部ライブラリーの戻り値など、静的に確定できない契約は残ります。イベントの解除、WebSocket・デコーダー・タイマーの解放、副作用の再送防止は動作試験でも確認します。

検査規則は `eng/javascript/rules`、マークアップ検査は `eng/javascript/markup.mjs`、プロジェクト固有の適用設定は `eslint.config.mjs` に置きます。規則には製品のパスや画面名を埋め込みません。

## コードエディターの配布ファイル

CodeMirrorのブラウザー用モジュールは、lockfileに固定した依存関係から `npm run build:code-editor` で生成します。関連依存の更新時には再生成し、`wwwroot/vendor/code-editor` と `wwwroot/licenses` の変更もコミットします。この処理は、バンドルへ実際に含まれたパッケージのバージョンとライセンスも更新します。製品の起動時にCDNへアクセスする必要はありません。

`npm run test:code-editor` はPlaywrightのChromiumで、各言語の改行・補完・Undo・日本語入力・操作禁止・破棄を検証します。ブラウザー未導入の開発環境では、先に `npx playwright install chromium` を実行します。通常のJavaScript単体試験は `npm run test:javascript` で実行します。

`npm run test:pwa` は公開済み資材をloopbackで隔離配信し、マニフェスト・アイコン・インストール可否・オフライン再表示・画面更新を検証します。`dotnet publish` したClientHostの `wwwroot` を `CONTENTS_PWA_WEBROOT` に指定します。管理APIは試験内で応答し、本番のCoreServiceへ接続しません。開発用Service Workerはキャッシュしないため、通常のビルド出力は使用できません。

既定はPlaywrightのChromiumで、`CONTENTS_PWA_BROWSER=msedge` を指定するとインストール済みEdgeでも確認できます。
