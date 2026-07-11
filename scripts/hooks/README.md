# Git Hooks

このディレクトリには、Lefthook 用の Git フックスクリプトが含まれています。

## フックの種類

### pre-commit-checks.ps1

**実行タイミング**: コミット時

**チェック内容**:
- ステージングされたファイルの大文字小文字のファイル名競合の検出 (check-case-conflict)
- ステージングされたファイルのサイズ制限 (check-added-large-files: 最大 1000KB)
- 不要な末尾の空白 of チェック (trailing-whitespace: markdown ファイルを除く)
- ファイル末尾の改行のチェック (end-of-file-fixer: SVG などを除くテキストファイル)
- コンフリクトマーカー (`<<<<<<<`, `=======`, `>>>>>>>`) の検出 (check-merge-conflict)
- プライベートキーや秘密情報の漏洩検知 (detect-private-key)
- 改行コードのチェック (mixed-line-ending: CRLF 統一。`.sh` は shebang / 文字列比較の正しさのため LF 固定を要求)
- YAML ファイルのタブインデント検知 (check-yaml)

---

### pre-commit-format.ps1

**実行タイミング**: コミット時

**チェック内容**:
- `dotnet format` によるコードフォーマット検証
- フォーマット違反があればコミットを拒否

**修正方法**:
```powershell
dotnet format winui3/SquirrelNotifier.WinUI3.sln
git add .
git commit
```

---

### pre-commit-build.ps1

**実行タイミング**: コミット時

**チェック内容**:
- Release 構成でのビルド
- 警告をエラーとして扱う（TreatWarningsAsErrors=true）
- ビルドエラーがあればコミットを拒否

**修正方法**:
```powershell
# エラーを修正してから
dotnet build winui3/SquirrelNotifier.WinUI3.sln -c Release /p:Platform=x64
git add .
git commit
```

---

### pre-commit-test.ps1

**実行タイミング**: プッシュ時

**チェック内容**:
- すべてのユニットテストを実行
- コードカバレッジを測定（80% 必須）
- テスト失敗またはカバレッジ不足でプッシュを拒否

**修正方法**:
```powershell
# テストを修正・追加してから
dotnet test winui3/SquirrelNotifier.WinUI3.sln -c Release
git push
```

---

## フックのスキップ

緊急時や WIP (Work In Progress) のコミットでフックをスキップする場合:

```powershell
# コミット時のフックをスキップ
git commit --no-verify

# プッシュ時のフックをスキップ
git push --no-verify
```

**警告**: フックをスキップすると、CI/CD で失敗する可能性があります。

---

## トラブルシューティング

### PowerShell 実行ポリシーエラー

スクリプトの実行が許可されていないエラーが出た場合は、以下を実行してください:

```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

### Git フックが動作しない / 再設定したい

Lefthook の設定を再インストールするには以下を実行します:

```powershell
# フックの再インストール
lefthook install

# 手動でコミット前フックを実行してテスト
lefthook run pre-commit
```

### ビルドエラーが解決できない

```powershell
# クリーンビルド
dotnet clean winui3/SquirrelNotifier.WinUI3.sln
dotnet build winui3/SquirrelNotifier.WinUI3.sln -c Release /p:Platform=x64
```

---

## カスタマイズ

フックの動作をカスタマイズするには、`lefthook.yml` を edit してください。
詳細は [Lefthook ドキュメント](https://github.com/evilmartians/lefthook/blob/master/docs/configuration.md) を参照してください。
