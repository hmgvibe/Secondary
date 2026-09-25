# 新版第四階段：資料來源驗證

狀態：進行中，免驅動來源與手動硬體感測助手已驗證；PresentMon 權限仍受限。尚未進入第五階段，遊戲 HUD 繼續顯示預覽資料。

## 手動硬體感測助手（2026-09-22 後續）

使用者僅同意加入手動助手，並未授權管理員驗證或驅動安裝。設定頁新增「硬體感測助手 → 啟用並讀取一次」與停止按鈕，沿用自繪控制樣式與單欄版面。預設不啟動、不保存自動啟動設定；資料為帶時間的快照，讀取完成自動退出。離開設定、隱藏、最小化、鎖定、睡眠及關閉視窗會取消，不自動恢復。

- `Secondary.SensorHelper` 獨立 x64 子程序，鎖定 NuGet 0.9.6 及 packages.lock.json；主 UI 不載入感測庫。
- 只接受固定 `--sample`，透過繼承的匿名 stdout/stderr 管道傳回 JSON；無開放埠、共享命名管道、任意命令執行介面。
- 最多 512 項、512 KiB、20 秒助手 watchdog／22 秒父端期限；取消後終止自身子程序並等待退出，崩潰或格式不符在 UI 顯示錯誤。
- 只啟用 CPU（已存在 PawnIO 時）、GPU、記憶體。沒有主機板、風扇控制、磁碟、電源供應器掃描或硬體設定寫入功能。
- 無 PawnIO 時略過 CPU 底層及此庫依賴 CPU 探測的 Intel GPU，明確顯示原因；不啟動安裝器、不提權、不變更群組。
- 顯示完整裝置／感測器名稱與識別，不把 Virtual Memory 當成 GPU 顯存，不將不同 GPU 的值合併。
- 本機 RTX 3060 Laptop GPU 已讀到溫度、時脈、功耗及顯存；CPU 底層來源因缺少 PawnIO 正確降級。感測準確度尚未與外部工具對照。
- Release：160 個核心測試、6,057 個一般 UI 斷言；另加真實助手讀取／取消／無殘留／主 UI 不載入硬體庫，共 6,061 個斷言通過。發布輸出已包含助手、依賴及授權檔。

可選實機測試（一般 build 不會啟動感測助手）：

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.tools/dotnet'
& ./tests/Secondary.UiTests/bin/Release/net10.0-windows/Secondary.UiTests.exe artifacts/redesign/sensor-helper --sensors
```

## 已完成

- 獨立 `Secondary.Diagnostics` 工具，不啟動主程式、不讀寫使用者設定、不安裝服務或驅動。
- 統一讀數包含時間、來源、範圍識別、單位、可用狀態與原因。無資料保持 null，不當成 0。
- Windows 基礎讀數先建立基準，2.2 秒後取第二次樣本；結束即釋放 PDH。
- PresentMon 固定為官方 2.6.0 x64 console，執行前驗證 SHA256，檔案鎖防止驗證後被替換。
- 指定 PID、獨立隨機 session、5 秒採集、15 秒逾時；只要求清理自身 session，沒有停止其他會話或自動提權。
- v2 CSV 按欄名解析，支援引號與逗號、欄位重新排列；排除其他 PID、NA、NaN、Infinity、負數、零長度幀及缺欄。只保留最後 60 筆診斷樣本。
- 尚未計算 FPS、1% low、主 swapchain 或整合 HUD，不把 CPU frame interval 當成顯示 FPS。

## 執行

使用專案 `.tools/dotnet/dotnet.exe` 建置 solution 後：

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.tools/dotnet'
& ./src/Secondary.Diagnostics/bin/Debug/net10.0-windows/Secondary.Diagnostics.exe
# 可選：指定已在運行的程序 PID，取代 1234
& ./src/Secondary.Diagnostics/bin/Debug/net10.0-windows/Secondary.Diagnostics.exe --presentmon .tools/monitoring/PresentMon-2.6.0-x64.exe 1234
```

輸出 JSON 僅供診斷。0 代表診斷工具完成，不代表所有來源可用；必須檢查每項 State。2 表示參數錯誤，130 表示取消。

## 2026-09-22 本機驗證

一般權限下，CPU、記憶體、網路收送、GPU PDH 均回傳有效數值。GPU 定義為「所有顯示卡中最忙引擎」，不是指定遊戲或指定 GPU 使用率。

PresentMon 程序退出碼 6，官方錯誤為 `failed to start trace session: access denied`，要求管理員或 Performance Log Users 權限。本次沒有變更群組、提權、安裝驅動，也沒有取得真實遊戲幀；成功採集、取消清理、遊戲退出及長時間行為仍待後續實機驗證。

Debug build：0 warnings / 0 errors；149 個核心測試、5,505 個 UI 斷言通過。既有程式 UI 未改動，無需重啟使用中的 Secondary。

## 外部元件

- [PresentMon 2.6.0 官方發布](https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0)，使用獨立 console，不使用 MSI、overlay 或注入。
  - SHA256：`b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af`，已與 GitHub release asset digest 及下載檔核對。
  - [MIT 授權](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/LICENSE.txt)，副本見 `third-party/PresentMon-LICENSE.txt`。第四階段只在 `.tools` 使用；第五階段起隨 App 的 Monitoring 目錄一併發布。
- [LibreHardwareMonitor 0.9.6](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.6)：以 NuGet 庫接入獨立助手，MPL 2.0 與第三方授權、原始碼網址隨助手提供。未安裝驅動或官方 GUI。

## 待完成（不能標記整階段完成）

1. 手動助手已加入；CPU 底層感測需另行決定是否配置所需驅動與權限，尚未授權或執行。
2. 使用者決定是否以有 ETW 權限的診斷程序進行限時驗證。主 UI 不提升權限，不自動變更 Performance Log Users 群組。
3. 在實際渲染程序上驗證成功 CSV、PID 結束、取消及 timeout 清理；目前僅驗證無權限降級。
4. 真實感測值及裝置識別的對照確認、缺資料原因與安全退出驗收。
