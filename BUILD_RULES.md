# Kakikomi ビルド規則

共通運用は [cursor-playbook `docs/COMMON_APP_RULES.md`](https://github.com/honi0907/cursor-playbook/blob/master/docs/COMMON_APP_RULES.md) を参照。

## ビルド前

```powershell
Stop-Process -Name Kakikomi -Force -ErrorAction SilentlyContinue
```

## ポータブル（ダブルクリック起動）

```powershell
.\scripts\Build-Portable.ps1
```

| 成果物 | 場所 |
|--------|------|
| 起動 exe | `dist\Kakikomi\Kakikomi.exe` |
| ZIP | `dist\Kakikomi-{version}-x64-portable.zip` |

- アンパッケージ（`WindowsPackageType=None`）。`winapp` 不要。
- リリースのたびにポータブル ZIP も生成する。
- `dist` に古い `*-portable.zip` は残さない（スクリプトが削除）。

## リリース（Setup + ポータブル ZIP）

```powershell
.\scripts\Build-Release.ps1 -DryRun
```

GitHub Release まで出す場合（`origin` + `gh` 必須）:

```powershell
.\scripts\Build-Release.ps1
```

### 前提

- **Inno Setup 6**（`ISCC.exe`）。未インストール時は `installer/build-installer.ps1` が winget 導入を試みる
- 手動: `winget install --id JRSoftware.InnoSetup -e`

| 成果物 | 出力先 |
|--------|--------|
| Setup（GitHub / オンライン更新） | `dist\Kakikomi-{version}-x64-Setup.exe` |
| ポータブル ZIP | `dist\Kakikomi-{version}-x64-portable.zip` |
| publish フォルダ | `dist\Kakikomi\` |

### 保存フォルダ（PNG）

| 起動形態 | 保存先 |
|----------|--------|
| ポータブル（exe 隣に書ける） | `{exe}\save\` |
| インストール版（Program Files 等） | `%LocalAppData%\Kakikomi\save\` |

設定 → 保存 に実パスを表示し、「save フォルダを開く」でそこを開く。

- **毎回** Setup とポータブル ZIP の両方を生成する。
- `dist` には当該バージョンの成果物のみ残す（旧 `*-Setup.exe` / `*-portable.zip` は削除）。

### GitHub Release に載せるもの

| 層 | 載せるもの |
|----|-----------|
| **GitHub Release** | Setup.exe + portable.zip |
| **オンライン更新** | Setup.exe のみをダウンロード → インストーラー起動 |

- リポジトリ: `honi0907/kakikomi`（**公開**。オンライン更新はトークン無しで Release を読む）
- tag: `v{version}`（例: `v1.0.0`）
- 開発ビルド（`bin/`）では自己更新の適用をブロック
- プライベートにする場合は環境変数 `KAKIKOMI_GITHUB_TOKEN` が必要

## バージョン

- `Kakikomi.csproj` の `<Version>` / `<AssemblyVersion>` / `<FileVersion>` を揃える。
- パッチは **0〜9**（例: `1.0.0` … `1.0.9`）。**`1.0.9` の次は `1.1.0`**（`1.0.10` にはしない）。
- リリースのたびに必ず上げ、同じ tag / ファイル名の再利用はしない。
- 現状: **1.0.6**

## オンライン更新（アプリ内）

設定 → **バージョン / 更新** → **オンライン更新を確認**  
→ GitHub Release の最新 Setup を取得 → インストーラー起動 → アプリ終了。

トークン（プライベート repo 用）: 環境変数 `KAKIKOMI_GITHUB_TOKEN`
