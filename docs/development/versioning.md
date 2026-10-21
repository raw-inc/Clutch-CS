# バージョン管理

[ドキュメント一覧](../README.md) · [開発環境とビルド](build.md) · [配布物を作成する](distribution.md)

Clutch-CS の製品バージョンは、ルートの `Version.props` で管理します。リリースする内容に合わせて手動で更新してください。ビルドには、指定したバージョンがそのまま使われます。

## バージョンを設定する

`Version.props` の次の項目を編集してください。

| 項目 | 設定内容 | 例 |
| --- | --- | --- |
| `VersionPrefix` | `major.minor.patch` 形式のバージョン。各番号は `0`～`65534` の整数 | `1.2.3` |
| `VersionSuffix` | プレリリースの接尾辞。正式版では空にする | `rc.1` |

たとえば、`1.2.3-rc.1` を配布する場合は次のように設定してください。

```xml
<Project>
  <PropertyGroup>
    <VersionPrefix>1.2.3</VersionPrefix>
    <VersionSuffix>rc.1</VersionSuffix>
  </PropertyGroup>
</Project>
```

正式版の `1.2.3` として配布するときは、`VersionSuffix` を空にしてください。

現在の設定は、リポジトリのルートで次のコマンドを実行すると確認できます。

```powershell
./version.ps1 show
```

## コマンドでバージョンを上げる

`Version.props` を直接編集するほか、次のコマンドでも番号を更新できます。変更内容に合うものを一つ実行してください。

| コマンド | 用途の目安 | `1.2.3` からの変更例 |
| --- | --- | --- |
| `./version.ps1 patch` | 不具合修正 | `1.2.4` |
| `./version.ps1 minor` | 互換性を保った機能追加 | `1.3.0` |
| `./version.ps1 major` | 互換性に影響する変更 | `2.0.0` |

これらのコマンドは `VersionSuffix` も空にします。プレリリースとして配布する場合は、番号を更新したあとで接尾辞を設定してください。

## ビルドしてリリースする

1. リリースするバージョンを設定し、`./version.ps1 show` で確認してください。
2. [開発環境とビルド](build.md)に従ってビルド・試験を行ってください。
3. `Version.props` を対応するソースと一緒にコミットし、[配布物を作成する](distribution.md)の手順で配布物を生成してください。

`build.ps1`、`dotnet build`、`dotnet publish` は、いずれも `Version.props` の設定を使用します。繰り返し実行してもバージョンは変わりません。

インストーラーを生成するときの `-ReleaseId` は、製品バージョンに `v` を付けた値に揃えてください。次は、`Version.props` を正式版の `1.2.3` に設定した場合の例です。

```powershell
./installer/New-InstallPackage.ps1 -OutputRoot 'C:/build/Clutch-CS-1.2.3' -ReleaseId 'v1.2.3' -RuntimeIdentifier win-x64
```

GitHub Release では、設定したバージョンに合わせて `release/v1.2.3` や `release/v1.2.3-rc.1` のようにブランチを作成してください。ブランチの作成から公開までの手順は、[GitHub Release](distribution.md#github-release)を参照してください。

その実行だけ別のバージョンを使う場合は、配布スクリプトの `-Version 1.2.3`、または `dotnet build`・`dotnet publish` の `-p:Version=1.2.3` で指定できます。これらの指定は `Version.props` を書き換えません。

## 画面に表示されるバージョン

Git からビルドした場合、画面や診断情報には `1.2.3+<commit>` のようにコミットIDが付くことがあります。`+` 以降は、ビルド元のソースを特定するための情報です。

更新処理では、この情報を含むバージョンが配布物と稼働中のプロセスで一致するかを確認します。詳細は[更新処理](../reference/update-process.md)を参照してください。

## バージョン設定の処理を変更したとき

`Version.props` の読み取りや更新コマンドの実装を変更した場合は、次の試験を実行してください。

```powershell
./eng/versioning/Test-BuildVersion.ps1
```

隔離したプロジェクトを実際にビルドし、同じ設定での再ビルド、明示したバージョンの適用、プレリリースの表記、手動での番号更新を確認できます。
