# AI 用量組件

「AI 用量」組件顯示 Claude（Claude Code）與 Codex 的五小時和每週額度，包括已用百分比、進度條、重置倒數和資料更新時間。Windows（WPF）與 Linux（Avalonia）都提供這個組件，兩邊的行為和文字相同。讀取邏輯放在 `Secondary.Shared/AiUsageService.cs` 和 `ClaudeOnlineUsage.cs`，兩個平台共用。

本文的路徑以 Windows 寫法為主，Linux 的對應如下：

| Windows | Linux |
| --- | --- |
| `%USERPROFILE%\.codex` | `~/.codex` |
| `%USERPROFILE%\.claude` | `~/.claude` |
| `%LOCALAPPDATA%\Secondary\claude-statusline.json` | `${XDG_CONFIG_HOME:-~/.config}/secondary/claude-statusline.json` |

使用 `--profile=目錄` 啟動時，statusLine 快取不會跟著改到 profile 目錄。

## 原則

- 預設只讀取 Codex 和 Claude Code 自己寫在本機的檔案，不連網路，不讀登入憑證。唯一的例外是下面「Claude 線上用量」，它預設關閉，需要使用者手動開啟。
- 組件顯示在畫面上時每分鐘讀一次，讀檔在背景執行緒；離開首頁就停止。
- 組件底部的「每分鐘自動更新」預設勾選。取消勾選後，組件保留最後一次的讀數，計時器停止，不再讀檔，也不連線（包括線上用量）；這個選擇會存在組件設定裡，重新啟動後仍然維持。以暫停狀態啟動、還沒有讀數時，會顯示「已暫停自動更新；勾選下方開關後讀取」。重新勾選時會立刻讀一次，之後恢復每分鐘更新。
- 每個紀錄檔都記住上次讀到的位置，之後只解析新增的內容。第一次讀取時，Codex 只讀檔尾 2 MB，Claude transcript 最多讀檔尾 32 MB；單行超過 4 MB 會略過。
- 重置時間已過的視窗不沿用舊百分比，會顯示「已重置，等待下次使用後更新」。

## Codex

- 讀取位置：`%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl`；有設定絕對路徑的 `CODEX_HOME` 時改讀它。
- 只看修改時間最新的 6 個檔，取其中時間最新的 `token_count` 事件的 `rate_limits`。續接的舊 session 可能寫進較早日期的資料夾，所以依事件時間判斷，不依路徑日期。
- 用 `window_minutes` 分辨視窗：300 是五小時，10080 是每週。不依 `primary`／`secondary` 的位置判斷。`limit_id` 不是 `codex` 的事件（例如 `premium`，它的視窗是 null）會略過。
- `resets_at` 是 Unix 秒。
- 這份資料只有在使用 Codex 時才會更新；沒有紀錄時，組件會提示「使用 Codex 後會自動顯示」。

## Claude

Claude Code 唯一公開的用量欄位，是 statusLine 指令從 stdin 收到的 `rate_limits.five_hour`／`rate_limits.seven_day`（`used_percentage`、`resets_at`），只有 Pro／Max 帳號會提供。2026-10-20 實測確認，Claude 桌面版的 Code 分頁不會執行 statusLine，所以桌面版拿不到百分比。組件因此使用以下來源：

1. **額度用完紀錄（桌面版和 CLI 都有）**：讀取最近 8 天內修改過、最新的 24 個 `%USERPROFILE%\.claude\projects\*\*.jsonl` transcript（有設定 `CLAUDE_CONFIG_DIR` 時改讀它），尋找 `apiErrorParams.rate_limit_info` 中 `status` 為 `rejected` 的紀錄。重置時間還沒到時，對應的視窗顯示「已用完」和重置倒數。
2. **statusLine 快取（只有 CLI）**：如果 `%LOCALAPPDATA%\Secondary\claude-statusline.json` 存在，就讀取其中的 `rate_limits` 百分比；transcript 中更新的額度用完紀錄會覆蓋它。
3. **Claude 線上用量（非官方，預設關閉）**：見下一節。

本機來源都沒有資料時，組件會說明桌面版不提供百分比。

### Claude 線上用量（非官方，預設關閉）

- 開啟方式：在組件標題按右鍵，勾選「Claude 線上用量（非官方）」，看完確認對話框的說明後按「啟用」。設定只存在這個組件，取消勾選後就停用。
- 運作方式：讀取 `%USERPROFILE%\.claude\.credentials.json` 中 `claudeAiOauth.accessToken`（Claude Code CLI 登入時寫入），以 `Authorization: Bearer` 和 `anthropic-beta: oauth-2025-04-20` 呼叫 `GET https://api.anthropic.com/api/oauth/usage`，最多每 5 分鐘一次。讀取的欄位是 `five_hour`／`seven_day` 的 `utilization`（0–100）和 `resets_at`。
- token 只送往這個固定網址，不會寫入記錄或顯示在畫面上，也不會刷新；刷新會和 Claude Code 自己換 token 的流程互相干擾。
- 無法取得時，Claude 區塊會顯示「線上用量無法取得：原因」，本機來源的資料照常顯示。常見原因：
  - 找不到 Claude Code CLI 登入憑證。Claude 桌面版的登入不寫在這個檔案，只用桌面版時一定是這個結果。
  - CLI 登入憑證已過期：使用一次 Claude Code CLI，它會自己更新憑證。
  - 伺服器拒絕或回應格式無法辨識：端點可能已經變更。
- 實機驗證：2026-10-20 安裝 Claude Code CLI 2.1.304，以 Pro 帳號登入後實測。端點回應 HTTP 200，`five_hour`／`seven_day` 的 `utilization` 是 0–100 的數字，`resets_at` 是帶時區的 ISO 時間，和程式的解析方式一致。回應裡還有其他欄位（例如 `seven_day_opus`、`limits`、`extra_usage`），組件不使用。這是未公開的端點，格式以後仍可能改變，所以程式對欄位都採寬鬆解析。

### 在 Claude Code CLI 中設定 statusLine（選用）

#### Windows

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

#### Linux

1. 把以下內容存成 `~/.claude/secondary-usage.sh`，再執行 `chmod +x ~/.claude/secondary-usage.sh`。快取檔會寫到 `${XDG_CONFIG_HOME:-~/.config}/secondary/claude-statusline.json`。

   ```sh
   #!/bin/sh
   # Claude Code statusLine bridge for the Secondary AI usage widget.
   dir="${XDG_CONFIG_HOME:-$HOME/.config}/secondary"
   mkdir -p "$dir" && cat > "$dir/claude-statusline.json.tmp" && mv -f "$dir/claude-statusline.json.tmp" "$dir/claude-statusline.json" && echo "AI usage synced"
   ```

2. 在 `~/.claude/settings.json` 加入 `"statusLine": { "type": "command", "command": "~/.claude/secondary-usage.sh" }`。

注意事項：

- Windows 上，Claude Code 有 Git Bash 時透過 Git Bash 執行 statusLine，沒有時透過 PowerShell，所以路徑要用正斜線。
- 有些電腦的 PowerShell 執行原則會擋下腳本，因此指令要加上 `-ExecutionPolicy Bypass`。
- Windows 腳本在 Git Bash 和 PowerShell 兩種呼叫方式下都實測過；Linux 腳本在 Ubuntu 26.04 上實測過。
- 這個檔案包含 statusLine 的完整輸入，例如工作目錄和 session id，但只存在本機。
- Claude Code 在每次回應後才更新 statusLine，閒置時資料不會變。

## 風險

- 兩邊的 JSONL 格式都是工具內部格式，沒有公開文件，改版後可能改名或不再寫入。組件會略過讀不懂的行，最壞情況是顯示「沒有資料」，不會影響其他功能。
- Claude 桌面版平常沒有百分比，只有額度用完時才會顯示。
- Claude 線上用量使用沒有公開文件的端點，而且要經手 CLI 的登入 token，因此預設關閉，啟用前會再次確認。端點隨時可能失效或改變格式。

## 驗證紀錄（2026-10-20）

- Windows：實際啟動 app，讀到本機 Codex 紀錄，每週 6%、4 天 13 小時後重置，與原始 `resets_at` 核對一致。Claude 區塊顯示桌面版提示；開啟線上來源後顯示「無法取得」。在預設組件大小下不需要捲動。
- Linux：在 Ubuntu 26.04（Surface Pro 7、GNOME 50 Wayland、200% 縮放）上以 `--profile` 和 `--startup-check` 啟動 linux-x64 自包含版本，搭配 `CODEX_HOME` 指向的 Codex 範例紀錄，畫面與 Windows 一致。開啟線上來源時，因為沒有 CLI 憑證，顯示「無法取得」。
- Claude（CLI 登入後）：開啟線上來源時，組件顯示五小時 49%、每週 19%，重置倒數與端點回應一致。另外暫時設定 statusLine 橋接腳本，在 CLI 回覆一次後，快取檔寫入五小時 50%、每週 19%，重置時間與端點完全相同；關閉線上來源時，組件從快取顯示同樣的數字。測試結束後已移除暫時的 statusLine 設定、腳本和快取檔。
