# AWS EC2 Desktop E2E runner 運用メモ

Issue #224 の実デスクトップ E2E 用 runner は、開発 PC ではなく AWS EC2 の専用 Windows VM
で運用する。AWS account、AMI、instance type、EBS size はこのリポジトリへ固定値や credential
として保存しない。

## 作成前の確認

1. 以前の Kiro 用 AWS account を利用できること、対象リージョン、service quota、予算上限を確認する。
2. Windows Server / Windows 11、WinUI 3 の実行要件、Docker Desktop / WSL2 の構成を決める。
3. Mcp-Docker の固定 commit と image digest、sandbox 用の GitHub App / repository、token cache
   初期状態を決める。本番 repository、PR、queue、App、token は使用しない。
4. clean snapshot を作成する前に、runner image へ .NET SDK、WiX CLI、Docker、browser、Windows
   App SDK runtime、GitHub Actions runner、`DESKTOP_E2E_FULL_DRIVER` を導入する。

## ストレージ測定

容量は先に決め打ちしない。clean image、依存関係導入後、最小 DesktopSmoke 実行後の 3 時点で
次を実行し、`budget.recommendedMinimumGiB` を記録する。

```powershell
pwsh -File .\tests\e2e\scripts\Measure-DesktopE2EStorage.ps1 `
  -OutputPath .\artifacts\e2e-local\desktop\storage.json
```

候補は 32 / 40 / 48 / 64 / 80 GiB で、測定済み使用量 + 4 GiB を満たす最小候補を採用する。
Docker image、NuGet cache、Windows update の増加分が候補を超える場合は image を整理してから
再測定する。測定結果なしに EBS を拡張しない。

## runner 運用

- GitHub Actions runner label は `self-hosted`, `windows`, `squirrel-notifier-desktop` とする。
- `desktop-e2e` GitHub environment に sandbox 専用値を登録する。値は secret context から process
  environment へ渡し、workflow の echo、command line、screenshot、artifact に出力しない。
- workflow 実行前に clean snapshot へ戻し、実行後は成功・失敗を問わず停止または terminate する。
- 定期実行は設定しない。検証は `workflow_dispatch`、公開前は release workflow の gate のみとする。
- runner が clean でない、Docker daemon が使えない、Full driver がない、外部 stack が到達不能な
  場合は retry や `continue-on-error` で隠さず失敗させる。

## AWS 操作境界

このリポジトリの GitHub Actions workflow は EC2 の作成、起動、停止、terminate、snapshot 復元を
実行しない。AWS 操作は account owner の runbook または IaC から行い、workflow には AWS secret や
長期 access key を渡さない。最初の作成後は、実行前後の instance state、snapshot ID、AMI ID、EBS
サイズを手動で記録し、`versions.json` と突合する。
