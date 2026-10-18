# Linux 版規格與階段計劃

狀態：計劃已確認（含 MangoHud FPS 做法），L1 已實作，等待實機驗收；見 [Linux 第一階段](linux-stage-1.md)。每次只實作一個階段，完成後等待使用者確認才繼續。

## 已確認的方向

- 介面改用 Avalonia 新增 Linux 版，與 Windows 共用 Secondary.Core 及抽出的共用邏輯；Windows 版暫時維持現有已驗證的 WPF，不在本計劃內改寫。Linux 達到功能對等後，再另行決定是否統一為一套介面。
- 功能範圍與 Windows v1.0.2 對齊：首頁、快捷導航、遊戲模式、設定。
- 主要目標：Ubuntu／GNOME（Wayland），x64。其他桌面只做降級處理，不另行驗收。
- 發布採自包含 AppImage 與 .deb（linux-x64／amd64，內容相同；.deb 另附系統選單項目），CI 加入 ubuntu-latest 建置與測試；實機驗收在使用者的實體 Linux 機器上進行。

## 現況盤點

- Secondary.Core（約 40 KB）與核心測試已是 net10.0，可直接在 Linux 建置與執行。
- Secondary.App（約 400 KB）為 WPF／WinForms，Linux 無法執行。其中以下邏輯不依賴 WPF，可抽出共用：WeatherDataService、GameTelemetrySession 與 ISystemSampler、SystemDashboardModel、SingleInstanceCoordinator、AppDiagnostics、ShortcutExecutor 的確認與雜湊流程。HomeWidgetGeometry／HomeWidgetLayout 只用到 System.Windows 的 Rect／Size，換成自有結構即可共用。
- 僅限 Windows、Linux 需另行實作：DisplayService、GlobalHotkey、TrayService、ThemeService（DWM）、WindowsSampler／GpuCounter（PDH）、PresentMon、SensorHelper（LibreHardwareMonitor）、MediaWidgetSource（Windows 媒體控制）、背景影片（MediaElement）、.lnk 與 .ps1／.bat／.cmd 快捷、視窗按鍵傳送。

## 共用層

新增 Secondary.Shared（net10.0）放入上述可共用邏輯，WPF 與 Avalonia 兩邊引用；L1 只搬外殼所需部分，其餘隨所屬階段搬移。抽出時 Windows 行為不得改變：每次搬移後完整執行現有 233 項核心測試與 6,790 項 UI 檢查，只搬移、不順手重構。平台差異以介面注入（取樣、媒體、顯示、快捷執行、設定路徑），不使用條件編譯散落各處。

設定格式沿用同一 schema，Linux 存於 `$XDG_CONFIG_HOME/secondary`（預設 `~/.config/secondary`）的 settings.json 與 shortcuts.json，紀錄存於 `$XDG_STATE_HOME/secondary/logs`。顯示器名稱、快捷鍵等平台相關欄位依平台解讀；不讀取或遷移 Windows 設定檔。

## 各功能在 Ubuntu／GNOME 的做法

| 功能 | Linux 做法 | 限制與降級 |
|---|---|---|
| 介面框架 | Avalonia（實作時鎖定當時最新穩定版），預設經 XWayland 執行 | 原生 Wayland 後端成熟後再評估 |
| 副屏與全螢幕 | Avalonia Screens 列舉顯示器，移到指定螢幕後全螢幕 | Wayland 不允許應用程式自行指定位置；若 XWayland 下仍無法定位，改為提示使用者把視窗拖到副屏再全螢幕 |
| 全域快捷鍵 Ctrl+Alt+G | 優先用 XDG 桌面入口（portal）GlobalShortcuts；另提供 `secondary --toggle-game` 指令，經單一實例通道傳給執行中的程式 | portal 不可用時，設定頁說明如何在 GNOME「鍵盤 → 自訂快捷鍵」綁定該指令 |
| 系統匣 | Avalonia TrayIcon（StatusNotifierItem），Ubuntu 預設的 AppIndicator 擴充可顯示 | 偵測不到系統匣時，關閉視窗改為最小化而非隱藏，避免程式無從找回 |
| 主題、減少動畫、高對比 | portal 的 color-scheme，GNOME enable-animations 與 high-contrast 設定 | 讀不到時沿用程式內選項 |
| 鎖定、睡眠 | systemd-logind D-Bus 訊號，與 Windows 相同停止取樣與背景資源 | — |
| 單一實例 | 沿用 SingleInstanceCoordinator（.NET 在 Linux 以 Unix socket 實作具名管道） | 需在 Linux 補測試確認 |
| 媒體組件 | MPRIS（D-Bus）取得標題、演出者、封面與播放狀態，提供上一首／播放暫停／下一首 | 播放器不支援的操作停用，與 Windows 一致；封面只讀本機檔案，不另外連網抓取 |
| 天氣 | 直接共用 Open-Meteo 流程 | — |
| 圖片背景 | Avalonia 原生實作，圖片背景解碼與尺寸上限沿用 | 漸層與流光動效已依使用者要求自兩個版本移除 |
| 影片背景 | 使用系統已安裝的 libvlc（LibVLCSharp）解碼成畫面，再交給 Avalonia 顯示，讓組件能疊在影片上 | 未安裝或解碼失敗時回退靜態背景並顯示原因；不安裝編解碼器 |
| 快捷導航 | 程式、檔案、資料夾、網站、.desktop 啟動器（取代 .lnk）；檔案與網址經 xdg-open；圖示從 .desktop 與系統圖示主題取得 | — |
| 巨集步驟 | 開啟、等待、外部 .sh 腳本（一般權限，不使用 sudo／pkexec，取消只終止自己啟動的程序） | Wayland 禁止向其他應用程式送按鍵，「指定視窗快捷鍵」步驟在 Linux 停用並說明原因；匯入含此步驟的巨集保留但不可執行 |

## 遊戲模式監測

Linux 不需要獨立硬體助手，以下來源一般權限即可讀取，皆在主程序內依現有高／低採樣頻率取樣：

- CPU、記憶體、網路：/proc/stat、/proc/meminfo、/proc/net/dev；程序搜尋用 /proc/[pid]。
- CPU 溫度、時脈：hwmon（k10temp、coretemp）與 cpufreq。CPU 功耗的 RAPL 介面一般使用者無法讀取，顯示「—」並說明原因。
- GPU：AMD 用 amdgpu sysfs 與 hwmon（使用率、顯存、溫度、時脈、功耗）；NVIDIA 用驅動附帶的 NVML 函式庫；Intel 只提供 hwmon 可讀的項目，使用率讀不到時顯示「—」。多 GPU 可選感測裝置，與 Windows 相同。
- 未連接來源不使用示意數值，各來源分別標示有效、等待、過期或失敗。

FPS／1% Low／幀間隔：Linux 沒有 PresentMon 的對應物。建議讀取 MangoHud 的記錄檔：使用者自行在遊戲啟動選項啟用 MangoHud 記錄（例如 Steam 啟動選項），Secondary 只讀取記錄檔、不載入或注入任何東西到遊戲。未啟用時顯示「—」與設定說明。使用者已確認此做法；仍需在實機上確認 MangoHud 記錄是否包含逐幀時間，足以沿用現有 1% Low 定義。

## 階段

| 階段 | 內容 | 驗收重點 |
|---|---|---|
| L1 | 抽出 Secondary.Shared；Avalonia 外殼：底部四入口、設定頁、副屏選擇與全螢幕、主題、系統匣、單一實例、`--toggle-game` 與 portal 快捷鍵、XDG 路徑；CI 加入 ubuntu-latest；AppImage 骨架 | Windows 測試全數維持通過；實機上 GNOME Wayland 的副屏、系統匣與快捷鍵行為 |
| L2 | 首頁：組件畫布拖曳／八方向縮放／吸附、三款時鐘、全部組件、MPRIS 媒體、天氣、圖片／影片背景、離頁釋放資源 | 與 Windows 版行為對照；Avalonia Headless UI 測試 |
| L3 | 快捷導航：分組、搜尋、拖入與排序、.desktop 與圖示、巨集與 .sh 腳本確認流程 | 確認與重新確認流程、取消語義、Wayland 下按鍵步驟的停用說明 |
| L4 | 遊戲模式：Linux 取樣器、GPU 來源、MangoHud FPS（若確認）、儀表介面、緊湊摘要、進場動畫、高／低採樣 | 實機上 AMD 或 NVIDIA 讀數；無來源時的「—」與說明 |
| L5 | AppImage 與 .deb 發布（含第三方授權聲明）、Linux 版量測腳本、長時間資源、Orca 讀屏與鍵盤操作、實機驗收；併入下一個 Release | 實體副屏與長時間驗收 |

## 不在本計劃內

Flatpak／Snap、ARM、非 GNOME 桌面的專門整合、遊戲內 overlay、自動提權、安裝驅動或核心模組、Windows 版改用 Avalonia。
