---
name: github-release
description: 自動分析專案變更、執行測試驗證，並依標準格式產出正體中文 Git Commit Message 與 GitHub Release 說明。
---

# GitHub Release & Commit Message Generator

當使用者需要產出版本 Commit Message 或發佈 GitHub Release 說明時，請遵循以下標準化流程與格式。

## 執行流程

1. **取得版本與變更內容**：
   - 檢查專案版本定義檔案（例如 `Directory.Build.props`、`package.json`、`Cargo.toml`、`pyproject.toml` 等）確認新版本號。
   - 執行 `git diff` 或 `git status` 檢查本次修改的實際程式碼與項目。
2. **執行自動化測試與驗證**：
   - 執行專案的測試指令（例如 `dotnet test`、`npm test`、`cargo test`、`pytest`）。
   - 記錄通過的測試項目總數與建置狀態（例如 0 warnings / 0 errors）。
3. **產生標準輸出**：
   - 提供格式標準的正體中文 Git Commit Message。
   - 產出結構化、正體中文的 GitHub Release Markdown 說明。

---

## 輸出格式規範

### 1. Git Commit Message 格式（正體中文）

採用 Conventional Commits 風格，主旨與內文均使用正體中文：

```gitcommit
<type>(<scope>): <簡短主旨說明>

- <詳細說明項目 1>
- <詳細說明項目 2>
- 版本號升級至 <version>
```

常見 type：`feat`（新功能）、`fix`（修復/改善）、`refactor`（重構）、`docs`（文件）、`chore`（雜項）。

### 2. GitHub Release 格式範本

必須嚴格遵循以下結構與分類標題：

```markdown
## <專案名稱> v<版本號>

### 新增

- <新功能說明 1>
- <新功能說明 2>

### 改善

- <優化、重構、相容性提升或修復項目>
- 專案與 README 版本同步更新至 <版本號>。

### 驗證

- <測試數量> 項單元測試全數通過。
- 專案建置成功，0 warnings / 0 errors。
```

> **分類規則**：
> - `### 新增`：使用者可直接感知的新功能、新參數或新介面。
> - `### 改善`：效能優化、Bug 修復、相容性加強、介面微調、依賴與版本更新。若無新增功能可僅保留此區塊。
> - `### 驗證`：實際測試通過數量與建置編譯結果。