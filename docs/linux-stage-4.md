# Linux 第四階段：遊戲模式與 Linux 監測來源

狀態：等待使用者確認。真正的 MangoHud 記錄格式要等 Surface 安裝 MangoHud 後再實測一次（見文末「尚未驗證」）。

## 交付內容

- 遊戲模式儀表（Avalonia）沿用 WPF 版的版面與文字：
  - FPS、1% Low、幀間隔，CPU、GPU、顯存與記憶體儀表，幀間隔與網路的 60 秒曲線，以及來源狀態與底部狀態列。
  - 低／高採樣率（保存於設定）。低：基本 3 秒、硬體 10 秒、幀記錄 1 秒；高：1 秒、2 秒、0.5 秒。介面每秒更新一次。
  - 有兩張以上 GPU 時，可選擇儀表顯示哪一張。
  - 視窗矮於 900 或內容窄於 700 時，與 Windows 相同改成緊湊摘要，把設定與曲線收進可展開區塊。
  - 進場動畫約 1.2 秒：掃描線往下，再淡入並上滑儀表；完成後停止。設定關閉動畫、系統關閉動畫或使用高對比時不播放；離開遊戲模式、開啟設定、隱藏或最小化時立即取消。
- 只在遊戲頁實際顯示時取樣。開啟設定、隱藏、最小化、螢幕鎖定、睡眠或離開遊戲模式都會停止取樣；回來時重新建立基準，不顯示舊數值。
- 每個來源各自判斷狀態（正常、等待、過期、失敗、停止），一個來源出錯不影響其他來源，下一次取樣會重試。過期的幀率顯示「—」，並說明遊戲可能已結束。
- 整機數據（/proc，一般權限）：
  - CPU：`/proc/stat` 的整機行。閒置＝idle＋iowait，與 Windows 的計算方式一致。
  - 記憶體：`MemTotal` 與 `MemAvailable`。
  - 網路：`/proc/net/dev` 中除了 lo 以外所有介面的總和。
- 硬體感測（sysfs，一般權限）：
  - Linux 讀 sysfs 不需要管理員權限，因此不像 Windows 需要手動「啟用硬體感測」，硬體讀數依採樣率自動更新。
  - CPU：溫度取 coretemp／k10temp／zenpower 的最高值（找不到時改用 x86_pkg_temp），頻率取最快的核心。
  - CPU 功耗讀 RAPL 的能量計數。Linux 預設只有 root 能讀，所以一般情況顯示「—」，並說明需要 root。
  - GPU 名稱來自 pci.ids（例如「Intel Iris Plus Graphics G4 (Ice Lake)」），AMD／NVIDIA 優先取括號內的產品名稱。
  - AMD（amdgpu）：使用率、顯存、溫度、核心頻率與功耗。
  - Intel（i915／xe）：
    - 使用率取 DRM 用戶端（`/proc/<pid>/fdinfo`）中最忙的引擎，這也是 gputop、nvtop 使用的來源，語意與 Windows 的「最忙引擎」一致。
    - 頻率取實際頻率；內建顯示卡沒有獨立的溫度、功耗與顯存，各自顯示原因。
    - 舊核心沒有 fdinfo 引擎時間時，改用非 RC6 省電時間估算，並標示「高於實際使用率」。
  - NVIDIA：
    - 專有驅動經 NVML（`libnvidia-ml.so.1`）讀取使用率、顯存、溫度、頻率與功耗；載入失敗時顯示原因。
    - nouveau 驅動退回 DRM 用戶端引擎時間。
  - 其他 GPU 若有 DRM 用戶端引擎時間，也會顯示使用率。
- FPS（MangoHud 記錄檔，經使用者同意的做法）：
  - Secondary 不注入遊戲，只讀取 MangoHud 寫出的 CSV 記錄。
  - 記錄資料夾為 `~/.local/share/secondary/mangohud`（依 `XDG_DATA_HOME`），進入遊戲頁時建立。
  - 遊戲頁的「C1 / MANGOHUD 幀率來源」提供 Steam 啟動選項，可一鍵複製；找不到 `mangohud` 指令時會提示安裝：
    `MANGOHUD_CONFIG="output_folder=…,log_interval=0,autostart_log=1,no_display" mangohud %command%`
  - 跟隨 30 秒內仍在寫入的最新記錄（不含 `_summary.csv`），從檔案開頭讀欄位名稱，再只讀最後 4 MiB。之後只讀新增的部分；寫到一半的行會等待補齊；檔案被截短時從頭重讀。
  - 欄位依名稱尋找：`frametime` 是幀間隔（ms），`elapsed` 是自記錄開始的奈秒數。
  - 每列交給 Windows 版共用的 `FrameWindow` 計算 FPS、1% Low 與幀間隔，與 PresentMon 的計算方式相同。
  - 若偵測到記錄不是逐幀（未設定 `log_interval=0`），仍顯示 FPS，但 1% Low 顯示「—」並說明原因。
  - 遊戲名稱取自記錄檔名（去掉 MangoHud 的時間戳記）。
- 啟動檢查的回執（`--startup-check`）新增遊戲模式的 CPU、GPU 使用率、幀率來源、FPS 與 1% Low，供 L5 量測使用。

## 與 Windows 版的差異

- 沒有「選擇遊戲程序」：MangoHud 記錄本身就對應一個遊戲，檔名就是遊戲名稱。
- 沒有「啟用硬體感測」開關：Linux 讀 sysfs 不需要提升權限，也不需要額外的輔助程式。
- CPU 功耗通常無法顯示（RAPL 只有 root 可讀）。Secondary 不要求 root，也不安裝 udev 規則。

## 驗證方法

Linux 外殼測試在 Windows 為 276 項，在 Linux 為 310 項（多出的是只在 Linux 執行的 RAPL、DRM fdinfo 符號連結與先前的執行器、App 目錄檢查）。新增的遊戲模式檢查：
- /proc 解析：CPU 時間對應、記憶體、排除 lo 的網路、缺少 MemAvailable 時拒絕；第一次取樣沒有基準；單一來源缺失不影響其他來源。
- sysfs 假資料夾：coretemp 優先於 ACPI、最快核心頻率、RAPL 功耗與計數器回繞、無權限時說明需要 root（Linux）。
  - card0-DP-1 這類接頭不算 GPU；pci.ids 名稱。
  - amdgpu 的使用率、溫度、頻率、功耗與顯存；i915 頻率、RC6 估算與內建顯示卡的說明；沒有感測器時的空讀數。
- DRM fdinfo（Linux）：依 PCI 裝置分組、同一用戶端只算一次、引擎數量的換算、取最忙引擎、xe 的 cycles 計數，以及 Intel 優先使用 fdinfo。
- MangoHud：
  - CSV：跳過系統資訊行、依欄名解析、判斷是否逐幀、略過無效列。
  - 跟隨記錄：資料夾不存在、忽略舊記錄與摘要檔、半行等待、追加讀取、FPS 與 1% Low、截短後重讀、長記錄只讀尾端。
- 儀表（無頭視窗）：
  - 關閉動畫時不播放；保存的高採樣率；建立 MangoHud 資料夾與啟動選項。
  - CPU、記憶體、FPS、1% Low、幀間隔、GPU 與顯存的實際數值；兩張 GPU 的選擇器；切換到內建顯示卡時的說明；LIVE 狀態與曲線。
  - 記錄停止後顯示「—」；設定、鎖定、睡眠時停止，回來時重新建立基準。
  - 緊湊與完整版面切換；動畫播放、取消與自行結束；關閉視窗時停止工作執行緒。

Surface（Ubuntu 26.04，i5-1035G4／Iris Plus G4，核心 6.19）實測：
- 真實 /proc 與 /sys：CPU、記憶體、網路、CPU 溫度 75 °C、頻率、RAPL 顯示需要 root、Intel GPU 名稱、頻率與 DRM 使用率。
- DRM 使用率與逐程序加總一致：gnome-shell 12.8%＋Xwayland 11.4%＋Secondary 3.9%，對上取樣器讀到的 27.2%。
- 打包後的 AppImage 以 `--toggle-game` 啟動，搭配以 MangoHud 格式即時寫入的模擬記錄（約 60 fps，每 2 秒卡頓一次）：
  - 顯示 FPS 57、1% Low 23、幀間隔 17.42 ms，判定為逐幀記錄。
  - 1301×843 視窗依規則使用緊湊摘要。
- 遊戲模式穩定狀態（低採樣率、記錄持續寫入）：Secondary 約佔單核 1.2%，RSS 241 MiB。

Windows 端完整執行 Release 版 build.ps1，依賴稽核完成：核心 243 項、WPF UI 6,721 項（與 L3 相同）、Linux 外殼 276 項全數通過。

## 尚未驗證

- 真正的 MangoHud 記錄：需要在 Surface 安裝 `mangohud` 與 `vulkan-tools`（vkcube）後實測欄位與逐幀設定。目前依 MangoHud 的格式（`frametime` 為 ms、`elapsed` 為奈秒）實作，並用相同格式的模擬記錄驗證。
- AMD 與 NVIDIA 實機：目前只有 sysfs 假資料夾的測試；NVML 路徑沒有實機驗證。
