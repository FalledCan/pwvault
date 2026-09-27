# PwVault

ローカル完結のパスワード管理デスクトップアプリ（フェーズ1）。
保管庫は暗号化した 1 つのファイルとして PC 上に置き、アプリはネットワーク通信を一切行いません。

![エントリ一覧と詳細](docs/images/detail.png)

- 要件定義: [docs/requirements.md](docs/requirements.md)
- 設計メモ（未決事項の決定・要件との対応・既知の限界）: [docs/design.md](docs/design.md)

## 主な機能

| 機能 | 要件 |
| --- | --- |
| 保管庫の作成・アンロック・手動／自動ロック（無操作・OS ロック・スリープ） | FR-01〜03 |
| エントリの追加・編集・ゴミ箱・完全削除、タグ、お気に入り、並び替え | FR-04, 05, 16 |
| 入力と同時に絞り込む検索（タイトル・ユーザーID・URL・タグ） | FR-06 |
| パスワード生成（8〜128 文字、文字種、紛らわしい文字の除外） | FR-07 |
| クリップボードへのコピーと自動クリア（Windows のクリップボード履歴・クラウド同期から除外） | FR-08, SR-10 |
| パスワードの伏せ字表示と切り替え | FR-09 |
| マスターパスワード・KDF パラメータの変更（保管庫鍵の再ラップのみ） | FR-10, 13 |
| 暗号化バックアップ、平文 CSV（警告＋再認証） | FR-11, SR-12 |
| CSV インポート（Chrome / Edge / Firefox、Bitwarden、KeePassXC） | FR-12 |
| 弱いパスワード・使い回しの検出、パスワード変更履歴 | FR-14, 15 |

## キーボードショートカット

| キー | 操作 |
| --- | --- |
| Ctrl+F | 検索欄へ |
| Ctrl+N | 新規エントリ |
| Ctrl+E | 編集 |
| Ctrl+S / Esc | 編集の保存 / キャンセル |
| Ctrl+B | ユーザーIDをコピー |
| Ctrl+Shift+C | パスワードをコピー |
| Ctrl+G | パスワード生成 |
| Ctrl+L | ロック |

## 構成

```
src/PwVault.Core     暗号コア（UI 非依存）: 鍵階層・AEAD・ファイル形式・保存・生成・検索・CSV
src/PwVault.App      Avalonia UI（MVVM）: 画面・自動ロック・クリップボード・設定
tests/PwVault.Core.Tests  ユニットテスト・テストベクタ・改ざん検知
tests/PwVault.App.Tests   ヘッドレス UI テスト（画面遷移を描画して確認）
```

- 言語・UI: C# / .NET 10 / Avalonia 12
- 暗号: [NSec](https://nsec.rocks/)（libsodium）… Argon2id、HKDF-SHA256、XChaCha20-Poly1305

## ビルドと実行

.NET 10 SDK が必要です。

```bash
dotnet run --project src/PwVault.App
```

テスト:

```bash
dotnet test
```

実際のクリップボードを使うテスト（クリップボードの中身を書き換えるので通常は実行しない）:

```bash
dotnet test tests/PwVault.App.Tests -- --explicit only
```

## ダウンロード・リリース

[Releases](../../releases) から `PwVault-<版>-win-x64.exe` をダウンロードすれば、インストール不要でそのまま起動できます（.NET ランタイム同梱の単一ファイル）。`.sha256` はファイルが壊れていないかの確認用です。

新しい版を出すときは、`src/PwVault.App/PwVault.App.csproj` の `<Version>` を上げてタグを push します。GitHub Actions がテスト・ビルドしてリリースに exe を添付します。

```bash
git tag v0.1.0
git push origin v0.1.0
```

手元で同じ exe を作る場合:

```bash
dotnet publish src/PwVault.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

## ファイル

| 場所 | 内容 |
| --- | --- |
| `ドキュメント\PwVault\vault.pwv`（既定。作成時に変更可） | 保管庫 |
| 同じフォルダの `vault.pwv.bak1`〜 | 保存のたびに残す直前の版（世代数は設定で変更） |
| `%APPDATA%\PwVault\settings.json` | 機密を含まない設定（保管庫の場所、自動ロック時間など） |
