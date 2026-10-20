# Secondary

Secondary v1.2.0 是副屏應用程式：Windows 11 x64 版使用 WPF／.NET 10，Ubuntu／GNOME 版使用 Avalonia，提供可自訂首頁、快捷導航與真實遊戲監測；沒有副屏時，也能用另一臺設備透過「連線模式」顯示並操作主電腦。部分監測來源受系統權限限制。

## 下載

從 [GitHub Releases](https://github.com/hmgvibe/Secondary/releases/latest) 下載。各發行包都已包含所需的 .NET 執行環境：

- Windows 版：`Secondary-v1.2.0-windows-x64.zip`，解壓縮後執行 `Secondary.App.exe`。更新前先從系統匣完全退出舊程式，再啟動新目錄中的程式；既有設定與布局會沿用。
- Linux 版（Ubuntu／GNOME）：`secondary_1.2.0_amd64.deb`（`sudo apt install ./secondary_1.2.0_amd64.deb`），或免安裝的 `Secondary-v1.2.0-linux-x86_64.AppImage`。
- 連接助手（主電腦，Windows）：`Secondary-Connect-v1.2.0-windows-x64.zip`，解壓縮後執行 `Secondary.Connect.exe`，常駐系統匣。只在使用連線模式時需要。

[本版更新內容](docs/releases/v1.2.0.md)

## 連線模式

沒有副屏時，可以用另一臺 Windows 或 Linux 設備當副屏：主電腦執行「連接助手」，設備在設定頁的「連線模式」搜尋並配對。只在同一個區域網路內使用：在連接助手選單按「開始配對」取得 6 位數配對碼，於設備輸入後完成配對，之後連線以 TLS 1.3 加密並只接受已配對的設備。連線後可查看主電腦的遊戲監測、用快捷鍵同步進出遊戲模式、控制主電腦的媒體播放，並在快捷導航新增、執行存放在主電腦的捷徑，編輯時也可在本機與主電腦之間移動（每個捷徑第一次執行要在主電腦上確認）。詳見 [連線模式計劃](docs/connection-plan.md)。

底部導航：**首頁｜快捷導航｜遊戲模式｜設定**。不再區分全能／輕量模式。

「設定 → 導航欄」可選標準或小（整體 80%）尺寸，以及上／下／左／右位置；小尺寸保留至少 44 DIP 的操作範圍。左右使用直向排列，立即套用並保存，日常與遊戲模式共用。預設仍為標準大小、底部置中。

- 首頁：自由排列的時鐘、日曆、便箋、倒數計時與碼錶。由「＋ 組件」新增，拖動標題移位、拖四角或四邊縮放，位置與大小自動保存；右鍵標題可移除。時鐘保留數字／翻頁／指針三種樣式。支援本機圖片與循環靜音 MP4 H.264 背景。
- 快捷導航：新增／編輯程式、檔案、資料夾、網站與 .lnk，依分組縱向或橫向排列並保存選擇；分組維持原本位置，組內快捷方式依可用寬度換行或依可用高度換列。支援分組篩選、拖入及拖曳排序，可匯出／匯入整個快捷導航設定（捷徑、分組與排列方式；另一個系統的捷徑會列為「待處理」，可移到主電腦執行、編輯或刪除；匯入前自動備份，匯入的捷徑首次執行需重新確認），拖到另一組可改變分組。可編排開啟、等待、指定視窗快捷鍵、外部腳本等步驟；首次執行確認後記住，步驟或腳本內容變更會重新確認，可取消、失敗即停。腳本不提權、不繞過執行原則。
- 遊戲模式：科幻儀錶、整機 CPU／記憶體／GPU 引擎使用率、網路與 60 秒曲線。手動搜尋選擇程序取得 PresentMon 幀率；無權限或無資料時顯示「—」。高／低採樣切換立即生效。可手動啟用獨立硬體助手，取得支援的溫度、時脈、功耗與顯存；多 GPU 可選感測裝置，不與整機 GPU 引擎值混用。
- 設定：獨立單直欄頁面，管理顯示器、主題、快捷鍵、動畫及手動單次硬體診斷。首頁外觀由「自訂首頁」彈窗管理。

低採樣：基本資料 3 秒、GPU／硬體 10 秒；高採樣：基本資料 1 秒、GPU／硬體 2 秒。介面每秒更新一次，幀事件獨立採集。切換程序或採樣設定會重新建立基準；進入設定、退出遊戲、隱藏、最小化、鎖定或睡眠會停止採樣與助手。

PresentMon 的 FPS／1% Low／幀間隔採集需要足夠的 ETW 權限；成功實機採集尚待有權限環境驗證。CPU 底層感測需要已安裝的 PawnIO 與裝置存取權限，只有安裝驅動仍可能無法取得溫度、功率或頻率。程式不自動提權、不安裝驅動、不變更帳號群組；未連接來源不使用示意數值。

遊戲模式可用底部入口、Ctrl+Alt+G 或系統匣手動進出；退出返回之前的日常頁。動畫會在完成、中途退出、開啟設定、隱藏或最小化時清理，並遵循系統減少動畫及高對比設定。

首頁右下「自訂首頁」開啟獨立彈窗，調整後按「套用並查看首頁」儲存並關閉；關閉或 Esc 放棄尚未套用的修改。背景只參照本機檔案，不複製或上傳；圖片最大 80 MB，解碼尺寸上限 2560×1440。影片是否可播放取決於 Windows 已有的解碼支援，失敗或檔案遺失會顯示原因並回退靜態背景，不安裝編解碼器。

可額外加入最多 12 個組件，同類型可重複。便箋按「儲存便箋」保存（最多 4000 字）；日曆支援切換月份與回到今天。倒數支援 1–1440 分鐘，與碼錶均可開始／暫停／重設；切頁仍繼續計時，但停止畫面更新。關閉程式後計時進度重設，倒數長度保留；到時僅在組件顯示提示，沒有背景通知或鈴聲。

新增「待辦清單」「計數器」「時間進度」，從首頁「＋ 組件」加入：待辦支援新增、完成／取消完成及刪除，最多 20 項、每項 120 字；計數器支援加減及歸零，範圍 ±999999。兩者操作即儲存到本機，重啟保留。時間進度顯示今日、本週（週一開始）與今年的進度，每分鐘更新；離開首頁停止刷新。所有組件沿用拖動、縮放、右鍵移除；不會自動新增到現有首頁。

「媒體」以封面、進度條和圖示按鈕顯示 Windows 目前播放的標題、演出者與狀態，提供上一首、播放／暫停、下一首；播放器必須支援 Windows 媒體控制。「天氣」由使用者輸入城市，使用 Open-Meteo 查詢目前溫度、體感、濕度、風速和天氣狀況，約每 15 分鐘更新；設定後只顯示天氣摘要，右上角齒輪可重新設定。城市名稱會存於本機，查詢會將城市名稱傳送給 Open-Meteo，沒有網路時會顯示錯誤。「倒數日」儲存事件名稱和 YYYY-MM-DD 日期，之後只顯示天數與日期，內容區右上角的小齒輪可再次編輯。三者都由「＋ 組件」手動加入，離開首頁時停止定期更新。

組件四角與四邊均可拖動：左右邊調寬、上下邊調高，四角同時調整寬高，對側保持固定。移動或縮放時，接近畫布或其他組件的邊線、中心線 8 DIP 內會對齊吸附並顯示虛線；按住 Alt 可暫時略過吸附。聚焦拖動柄後，Ctrl＋左右鍵調寬、Ctrl＋上下鍵調高（Shift 加速），鍵盤調整保持精確步距。尺寸與位置在放開時保存，Esc 取消當次拖曳並回復原布局。日曆日期、便箋輸入區、待辦列表、讀數、封面及字體隨內容區的寬高調整，縮放保留草稿及選取範圍；空間不足時可捲動。時鐘外框依格式與字型量測，數字及錶盤保持比例；翻頁只翻動變化的數字，約 420 ms 完成。「自訂首頁」中改變時鐘整體大小會重設時鐘框的獨立寬高。

新增組件會先尋找空白位置；空間已滿會提示，並將新組件放到上層。從「＋ 組件 → 選取現有組件」可找回被遮住的組件；鍵盤選取時會持續顯示編輯框。便箋也可按 Ctrl＋S 保存，待辦輸入後可按 Enter 新增。

天氣查詢會提供城市／行政區／國家候選，選定後保存座標；相同地點共用快取，顯示最後成功更新及失敗後的過期狀態。媒體組件共用 Windows 播放資訊，播放器不支援的操作會停用。圖片背景在背景工作中解碼，切換或離開首頁會取消舊請求。

「AI 用量」（Windows 與 Linux）顯示 Claude 和 Codex 的五小時與每週額度：已用百分比、進度條、重置倒數和資料更新時間，每分鐘讀一次；取消勾選組件底部的「每分鐘自動更新」可暫停讀取。組件只讀本機紀錄，不連網路，也不讀登入憑證。Codex 用量來自 `~/.codex/sessions` 的紀錄，使用 Codex 後才會出現。Claude 桌面版不提供用量百分比，額度用完時才會顯示重置時間；使用 Claude Code CLI 時可另外設定 statusLine 取得百分比，或在組件標題右鍵啟用預設關閉的「Claude 線上用量（非官方）」。資料來源、設定方式與風險見 [docs/ai-usage-widget.md](docs/ai-usage-widget.md)。

較小或較矮的遊戲視窗會顯示緊湊摘要，首屏保留 FPS、1% Low、幀間隔、CPU、GPU、記憶體、網路及來源狀態；採樣選項與曲線可展開查看。各來源分別標示有效、等待、過期或失敗，無可用值仍顯示「—」。

離開首頁、隱藏、最小化、鎖定或進入遊戲時，停止首頁計時、關閉影片、釋放圖片與背景動畫。減少動畫時保留靜態時鐘／圖片，停用翻頁效果與影片，內建動效改為靜態。

## 啟動

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start.ps1
```

設定頁面可選擇副屏並進入全螢幕；Esc 優先關閉設定／取消轉場，否則返回一般視窗。關閉視窗會隱藏到系統匣，系統匣「退出 Secondary」才會結束程式。

設定位於 `%LOCALAPPDATA%\Secondary\settings.json`；顯示器、主題、快捷鍵與採樣偏好都會保存在本機。

重複啟動會顯示原視窗，同一設定檔只保留一個寫入者。設定保存失敗時會顯示待保存狀態，可在設定頁按「重新儲存」；快捷資料超過大小上限時保留原檔。有限的操作錯誤紀錄位於 `%LOCALAPPDATA%\Secondary\Logs`，只記錄時間、操作、例外類別及錯誤碼，不記錄便箋、目標路徑或腳本內容。

## 建置與測試

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Configuration Debug
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Configuration Release -Offline -Live -Sensors -GameTelemetry
```

專案內 .tools/dotnet 提供 SDK，無需修改全域 PATH。腳本會執行核心測試與 WPF UI 可執行測試，並非 dotnet test 探索專案。`-Live` 啟用實際 WPF 視窗流程，`-Sensors`／`-GameTelemetry` 啟用本機硬體整合；結果及畫面預設寫入 artifacts/verification。`-Offline` 僅使用已快取套件並明確標記依賴漏洞掃描未完成；連網驗證則必須完成漏洞查詢。硬體庫版本由 packages.lock.json 鎖定；PresentMon console 固定為 2.6.0 x64 並驗證 SHA256，可用 scripts/fetch-presentmon.ps1 下載已固定的官方元件。

```powershell
# 隔離測試資料的效能量測，不改動日常設定；包含主程序與已觀測到的子程序。
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure.ps1 -Mode HomeWidgets -Seconds 60 -Warmup 10
# 預設含 runtime；Installed 輕量包需要 .NET 10 Desktop Runtime x64。
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1 -RuntimeMode Bundled
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1 -RuntimeMode Installed
```

量測情境支援 Home、HomeWidgets、Shortcuts、GameLow、GameHigh、GameSensors、Hidden、Minimized、Image、Video；圖片與影片須指定 `-Media`。CPU 百分比以邏輯處理器總容量為分母，報告包含資源覆蓋狀態；排程延遲包含取樣開銷，不能視為輸入延遲。短時間量測不代表長時間或實體副屏已驗收。

GitHub Release 提供自包含的 Windows x64 發行包。設計預覽與測試資料不代表真實遊戲監測效能。

## Linux 版

Ubuntu／GNOME（Wayland）版本以 Avalonia 開發，與 Windows 共用核心邏輯，提供外殼、首頁、快捷導航與遊戲模式（L1–L4），自 v1.1.0 起隨 Release 發布；長時間、無障礙與實體副屏驗收（L5）仍在進行。遊戲模式的 FPS 讀取 MangoHud 記錄檔，需自行安裝 MangoHud 並在遊戲啟動選項中加入 Secondary 提供的設定。建置與打包：

```bash
./scripts/build-linux.sh
./scripts/package-linux.sh
```

打包會產生三種格式，內容相同：AppImage（免安裝）、Ubuntu／Debian 用的 `secondary_<版本>_amd64.deb`，以及 tar.gz。.deb 會把程式裝在 `/opt/secondary`，並加入 `secondary` 指令和應用程式選單項目；裝好後 GNOME 就允許綁定全域快捷鍵，不必再到設定頁按「加入應用程式選單」。

```bash
sudo apt install ./secondary_1.2.0_amd64.deb
```

詳見 [Linux 版計劃](docs/linux-plan.md) 與各階段說明：[L1](docs/linux-stage-1.md)、[L2](docs/linux-stage-2.md)、[L3](docs/linux-stage-3.md)、[L4](docs/linux-stage-4.md)。

[功能規格](docs/redesign-plan.md) · [開發進度](docs/roadmap.md)

快捷方式資料另存於 `%LOCALAPPDATA%\Secondary\shortcuts.json`，最多 200 筆、每筆最多 20 步。腳本不支援額外參數；視窗按鍵需完整且唯一的程序名稱／視窗標題。取消不能撤銷已完成的操作，也不會關閉已開啟的程式。詳見 [快捷導航說明](docs/redesign-stage-3.md)。
