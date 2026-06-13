# Git Hooks

このディレクトリには、pre-commitフレームワーク用のGitフックスクリプトが含まれています。

## フックの種類

### pre-commit-format.ps1

**実行タイミング**: コミット時

**チェック内容**:
- `dotnet format`によるコードフォーマット検証
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
- Release構成でのビルド
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
- コードカバレッジを測定（80%必須）
- テスト失敗またはカバレッジ不足でプッシュを拒否

**修正方法**:
```powershell
# テストを修正・追加してから
dotnet test winui3/SquirrelNotifier.WinUI3.sln -c Release
git push
```

---

## フックのスキップ

緊急時やWIP（Work In Progress）のコミットでフックをスキップする場合:

```powershell
# コミット時のフックをスキップ
git commit --no-verify

# プッシュ時のフックをスキップ
git push --no-verify
```

**警告**: フックをスキップすると、CI/CDで失敗する可能性があります。

---

## トラブルシューティング

### PowerShell実行ポリシーエラー

```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

### pre-commitフックが動作しない

```powershell
# フックの再インストール
pre-commit install
pre-commit install --hook-type pre-push

# 手動実行でテスト
pre-commit run --all-files
```

### ビルドエラーが解決できない

```powershell
# クリーンビルド
dotnet clean winui3/SquirrelNotifier.WinUI3.sln
dotnet build winui3/SquirrelNotifier.WinUI3.sln -c Release /p:Platform=x64
```

---

## カスタマイズ

フックの動作をカスタマイズするには、`.pre-commit-config.yaml`を編集してください。

例: テストフックを無効化
```yaml
repos:
  - repo: local
    hooks:
      - id: dotnet-test
        name: dotnet test
        # stages: [push]  # この行をコメントアウト
```
