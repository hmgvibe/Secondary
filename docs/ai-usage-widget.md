# AI 用量組件

「AI 用量」組件顯示 Claude（Claude Code）與 Codex 的五小時和每週額度，包括已用百分比、進度條、重置倒數和資料更新時間。目前只有 Windows 版，Linux 選單不提供這個組件。讀取邏輯在 `Secondary.Shared/AiUsageService.cs`，不依賴 Windows，將來 Linux 可以直接沿用。

## 原則

- 只讀取 Codex 和 Claude Code 自己寫在本機的檔案，不連網路，不讀登入憑證。
- 組件顯示在畫面上時每分鐘讀一次，讀檔在背景執行緒；離開首頁就停止。
- 每個紀錄檔都記住上次讀到的位置，之後只解析新增的內容。第一次讀取時，Codex 只讀檔尾 2 MB，Claude transcript 最多讀檔尾 32 MB；單行超過 4 MB 會略過。
- 重置時間已過的視窗不沿用舊百分比，會顯示「已重置，等待下次使用後更新」。

## Codex

- 讀取位置：`%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl`；有設定絕對路徑的 `CODEX_HOME` 時改讀它。
- 只看修改時間最新的 6 個檔，取其中時間最新的 `token_count` 事件的 `rate_limits`。續接的舊 session 可能寫進較早日期的資料夾，所以依事件時間判斷，不依路徑日期。
- 用 `window_minutes` 分辨視窗：300 是五小時，10080 是每週。不依 `primary`／`secondary` 的位置判斷。`limit_id` 不是 `codex` 的事件（例如 `premium`，它的視窗是 null）會略過。
- `resets_at` 是 Unix 秒。
- 這份資料只有在使用 Codex 時才會更新；沒有紀錄時，組件會提示「使用 Codex 後會自動顯示」。

## Claude

Claude Code 唯一公開的用量欄位，是 statusLine 指令從 stdin 收到的 `rate_limits.five_hour`／`rate_limits.seven_day`（`used_percentage`、`resets_at`），只有 Pro／Max 帳號會提供。2026-10-20 實測確認，Claude 桌面版的 Code 分頁不會執行 statusLine，所以桌面版拿不到百分比。組件因此使用以下兩個來源：

1. **額度用完紀錄（桌面版和 CLI 都有）**：讀取最近 8 天內修改過、最新的 24 個 `%USERPROFILE%\.claude\projects\*\*.jsonl` transcript（有設定 `CLAUDE_CONFIG_DIR` 時改讀它），尋找 `apiErrorParams.rate_limit_info` 中 `status` 為 `rejected` 的紀錄。重置時間還沒到時，對應的視窗顯示「已用完」和重置倒數。
2. **statusLine 快取（只有 CLI）**：如果 `%LOCALAPPDATA%\Secondary\claude-statusline.json` 存在，就讀取其中的 `rate_limits` 百分比；transcript 中更新的額度用完紀錄會覆蓋它。

兩個來源都沒有資料時，組件會說明桌面版不提供百分比。

### 在 Claude Code CLI 中設定 statusLine（選用）

1. 把以下內容存成 `%USERPROFILE%\.claude\secondary-usage.ps1`。它把 statusLine 收到的 JSON 寫給 Secondary，並在狀態列顯示 `AI usage synced`。

   ```powershell
   # Claude Code statusLine bridge for the Secondary AI usage widget.
   [Console]::InputEncoding = [Text.Encoding]::UTF8
   $json = [Console]::In.ReadToEnd()
   $dir = Join-Path $env:LOCALAPPDATA 'Secondary'
   New-Item -ItemType Directory -Force -Path $dir | Out-Null
   $target = Join-Path $dir 'claude-statusline.json'
   $temp = "$target.tmp"
   [IO.File]::WriteAllText($temp, $json, (New-Object Text.UTF8Encoding $false))
   Move-Item -Force -LiteralPath $temp -Destination $target
   'AI usage synced'
   ```

2. 在 `%USERPROFILE%\.claude\settings.json` 加入以下設定，把 `<使用者>` 換成自己的帳戶資料夾名稱。如果已經有其他 statusLine，需要自己合併。

   ```json
   {
     "statusLine": {
       "type": "command",
       "command": "powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/<使用者>/.claude/secondary-usage.ps1"
     }
   }
   ```

注意事項：

- Windows 上，Claude Code 有 Git Bash 時透過 Git Bash 執行 statusLine，沒有時透過 PowerShell，所以路徑要用正斜線。
- 有些電腦的 PowerShell 執行原則會擋下腳本，因此指令要加上 `-ExecutionPolicy Bypass`。
- 這個腳本在 Git Bash 和 PowerShell 兩種呼叫方式下都實測過。
- 這個檔案包含 statusLine 的完整輸入，例如工作目錄和 session id，但只存在本機。
- Claude Code 在每次回應後才更新 statusLine，閒置時資料不會變。

## 風險

- 兩邊的 JSONL 格式都是工具內部格式，沒有公開文件，改版後可能改名或不再寫入。組件會略過讀不懂的行，最壞情況是顯示「沒有資料」，不會影響其他功能。
- Claude 桌面版平常沒有百分比，只有額度用完時才會顯示。
- 非官方的 `api.anthropic.com/api/oauth/usage` 需要 OAuth token。桌面版的登入資料不在 `~/.claude/.credentials.json`，而是由桌面 app 自行保管，所以這次沒有實作這個來源。
