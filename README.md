# AI Limit Monitor

監控 Claude Code 與 Codex CLI 剩餘使用量的小工具，提供 Console 與 Windows 系統匣兩種介面。

## 快速上手（免安裝）

不需要安裝 .NET，下載解壓即可使用（Windows x64）：

1. 到 [Releases](https://github.com/lawrence8358/AiLimitMonitor/releases) 下載其中一個（或兩個都要）：
   - **`console.zip`** — 終端機版，開著持續監控
   - **`desktop.zip`** — 系統匣版，常駐在工作列右下角
2. 解壓縮到任意資料夾
3. 執行：
   - Console 版：開啟終端機執行 `ailimit.exe`（`Ctrl+C` 離開；`ailimit.exe --once` 查一次就結束）
   - 系統匣版：直接雙擊 `ailimit-tray.exe`，圖示會顯示距離最近一次額度重置的時間，滑鼠移過去可看完整資訊，右鍵選單可離開
4. 第一次執行會自動偵測你電腦上已登入的 Claude Code（含多帳號）與 Codex CLI，設定檔產生在 `%USERPROFILE%\.ailimitmonitor\config.json`，可自行增減帳號

> 前提：電腦上要已經用 `claude` / `codex` 登入過（工具借用它們的憑證查詢，唯讀、不消耗額度）。
> 若 Windows SmartScreen 攔截，點「其他資訊 → 仍要執行」即可。

**Console 版**

![Console 版畫面](docs/screenshot-console.png)

**系統匣版（滑過圖示顯示）**

![系統匣版畫面](docs/screenshot-desktop.png)

---

以下為開發者說明。本專案以 .NET 10 開發，兩種介面共用同一套核心邏輯（`AiLimitMonitor.Core`）。

```
claude
  5h     [██████░░░░░░]  50.0% used    resets in 3h42m    (Sat 04:40 UTC+8)
  weekly [█████░░░░░░░]  42.0% used    resets in 27h02m   (Sun 04:00 UTC+8)

codex (team)
  weekly [███████████░]  91.0% used    resets in 65h06m   (Mon 18:04 UTC+8)
  reset credits: 1 available
```

## 專案結構

| 專案 | 說明 |
|---|---|
| `src/AiLimitMonitor.Core` | 共用邏輯：providers、設定檔、文字渲染 |
| `src/AiLimitMonitor.Cli` | Console 版（`ailimit`），持續刷新畫面 |
| `src/AiLimitMonitor.Tray` | 系統匣版（`ailimit-tray`），圖示顯示距離下次重置的時間，滑鼠移過去彈出與 Console 相同的資訊視窗 |
| `tests/AiLimitMonitor.Core.Tests` | 單元測試（xUnit） |

## 使用方式

```powershell
# Console 版：持續監控（Ctrl+C 離開）
dotnet run --project src/AiLimitMonitor.Cli

# 只查詢一次
dotnet run --project src/AiLimitMonitor.Cli -- --once

# 自訂刷新間隔（秒）／設定檔路徑／關閉邊框小寵物
dotnet run --project src/AiLimitMonitor.Cli -- --interval 30 --config C:\path\config.json --no-pet

# 系統匣版
dotnet run --project src/AiLimitMonitor.Tray
```

系統匣版操作：圖示上的文字為**距離所有帳號中最近一次重置的剩餘時間**（如 `3h` = 最快到期的視窗約 3 小時後重置，`58m`、`2d` 同理）；滑過或左鍵點擊顯示完整資訊；右鍵選單可 Refresh now / Show pet（切換邊框小寵物）/ Exit。

時間格式：超過一天顯示 `2d 3h`、一小時以上顯示 `4h 42m`、不足一小時顯示 `45m`（不足該單位就不顯示該單位）；重置時間點以台灣慣用格式顯示，如 `(2026/08/08 18:59 UTC+8)`。

邊框小寵物 🐹：預設沿邊框順時針跑；CLI 用 `--no-pet`、Tray 用選單「Show pet」關閉。關閉時**邊框仍保留**（只移除寵物），畫面不會跳動。

## 設定檔

第一次執行會自動掃描並產生 `%USERPROFILE%\.ailimitmonitor\config.json`：

- 家目錄下所有含 `.credentials.json` 的 `.claude*` 目錄 → 各自成為一個 claude provider（多帳號如 `~/.claude-5x` 會自動加入）
- `~/.codex/auth.json` → codex provider

```json
{
  "refreshSeconds": 60,
  "providers": [
    { "type": "claude", "name": "claude",    "configDir": "~/.claude" },
    { "type": "claude", "name": "claude-5x", "configDir": "~/.claude-5x" },
    { "type": "codex",  "name": "codex",     "authPath": "~/.codex/auth.json" },
    { "type": "command", "name": "my-custom", "command": "my-usage-command" }
  ]
}
```

### 自訂命令 provider（type: `command`)

`command` 會透過 PowerShell 執行，stdout 需輸出以下格式的 JSON，即可顯示在同一個畫面：

```json
{
  "windows": [
    { "label": "5h",     "usedPercent": 51.0, "resetsAt": "2026-08-08T00:10:00+08:00" },
    { "label": "weekly", "usedPercent": 54.0, "resetsInSeconds": 25440 }
  ],
  "notes": ["額外顯示的文字（可省略）"]
}
```

`resetsAt`（ISO 8601）與 `resetsInSeconds` 擇一即可，都省略則不顯示重置時間。

## 資料來源

兩者皆為唯讀 API，不消耗任何額度：

- Claude：`GET https://api.anthropic.com/api/oauth/usage`（token 讀自 config 目錄的 `.credentials.json`）
- Codex：`GET https://chatgpt.com/backend-api/wham/usage`（token 讀自 `~/.codex/auth.json`）

Claude 的 access token 過期時會自動用 refresh token 換新並寫回 `.credentials.json`（與 Claude Code 本身的行為一致），不需要手動介入；只有 refresh token 也失效時才需要開 `claude` 重新登入。Codex 的 token 由 Codex CLI 維護，若過期出現 401 請開一次 `codex`。

## 測試

```powershell
dotnet test
```

## 打包 Release

```powershell
.\build.ps1              # 跑測試 → 發佈 → 產出 dist\console.zip 與 dist\desktop.zip
.\build.ps1 -SkipTests   # 略過測試
```

兩個 zip 都是自包含（self-contained）單一執行檔，使用者不需安裝 .NET Runtime。Console 版有開啟 trimming（約 6–7 MB）；系統匣版因 WinForms 不支援 trimming，約 44 MB。打包前請先關閉執行中的 `ailimit-tray`，避免檔案被鎖定。
