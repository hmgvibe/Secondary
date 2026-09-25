# 第四階段：系統監控與遊戲優化

狀態：系統功能及生命週期測試已完成，效能驗收未全部通過。仍停留第四階段，未啟動第五階段。

## 提供的數據

| 項目 | 來源與定義 | 一般模式 | 遊戲模式 |
|---|---|---|---|
| CPU | GetSystemTimes 差分；kernel 含 idle，扣除 idle 後除總處理器時間。不是工作管理員的時脈加權效能指標 | 每 1 秒 | 每 3 秒 |
| 記憶體 | GlobalMemoryStatusEx：實體已用／總量及百分比 | 每 1 秒 | 每 3 秒 |
| GPU | PDH GPU Engine 計數器，同實體引擎的各程序值加總，顯示所有 GPU 中最忙引擎，不稱為全 GPU 平均值 | 每 2 秒 | 每 10 秒 |
| 網路 | 活動非 loopback／非 tunnel 介面位元組差分，除實際單調時鐘間隔；上下行分列 | 每 1 秒 | 每 3 秒 |
| 磁碟 | 固定磁碟可用／總空間；不是磁碟 I/O 使用率 | 每 30 秒 | 不採樣 |
| 電源 | GetSystemPowerStatus：外接／電池、剩餘電量 | 顯示 | 精簡畫面不顯示 |
| 溫度 | 未有可靠通用來源；不安裝核心驅動、不估算 | 顯示限制說明 | 不顯示 |

CPU／網路首筆需要基準，GPU 至少需要兩次查詢；沒有資料、讀取失敗、計數器重設時顯示「—」，不是 0%。GPU 驅動未提供計數器时顯示原因。網卡清單每 30 秒重建，VPN／虛擬介面可能重複計量；此值不是單一網際網路連線速度。超過 64 邏輯處理器的 Windows GetSystemTimes 有處理器群組限制，需在該類硬體上另外驗證。

API 依據：[GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)、[PDH 陣列讀取](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcounterarrayw)。

## 採樣與繪製設計

- 僅系統分區被啟用且視窗可見、非最小化時建立原生採樣器。
- 背景序列工作，原生呼叫不在 UI 執行緒執行；等待前一代停止後才開始下一代，不疊加查詢。
- 每次最多一個等待中的 Dispatcher 更新；離區／隱藏／最小化／關閉會取消，過期結果不會覆蓋新狀態。
- 停止後釋放 PDH 查詢；取消來源的清理不依赖 UI Dispatcher 存活。已進行中的同步原生呼叫只能等待返回後釋放，不宣稱能強制中止驅動。
- 數值文字未變更時不發送卡片 PropertyChanged；不使用持續動畫、60 FPS 計時器、歷史無界陣列或定時 GC。
- 遊戲模式保留 GPU 數據，降低採樣頻率，取消磁碟輪詢、玻璃、陰影及轉場，使用視窗層級 SoftwareOnly 繪製；離開遊戲恢復預設繪製。
- 卡片有穩定的讀數寬度以避免每次數值變更造成整排跳動；最大 320 DIP、窄視窗縮小，自動換行逐排居中，整區維持最大 1600 DIP，底部膠囊導航不變。

## 自動化與畫面驗證

Release 建置零警告零錯誤，49 項核心測試、2555 項 WPF UI 斷言通過。新增 CPU idle 算法／基準／重設、網路差分、遊戲四張監控卡片及窄視窗排版、背景採樣停用、恢復與原生資源釋放、取消舊結果、例外降級、相同讀數不重繪等測試。

`artifacts/stage-4/layouts/` 是原生 WPF 離屏版面渲染，沒有啟動真實採樣，因此顯示等待狀態。不可把它當作即時硬體或實體副屏驗收。computer-use 曾檢查原生遊戲測試視窗，看到 CPU、記憶體與網路實值，以及置中的卡片、退出按鈕與底部導航。

最終日常預覽亦用 computer-use 切至全能系統頁，確認 CPU、記憶體、GPU、上下行速率、C／D 固定磁碟空間與電源資訊已回傳。實際視窗截圖為 `artifacts/stage-4/system-live.png`；這是單視窗檢查，並非實體雙螢幕／全螢幕游戏驗證。UI 工具首次輸入遇到座標幾何不可用，重新觀察後重試成功。

## 效能初測與未完成項

本機 14 邏輯處理器、1200×820 DIP、Release、可見視窗、15 秒暖機後量測 60 秒。每 5 秒記錄一次 process CPU 時間、WorkingSet64、PrivateMemorySize64；CPU 除以邏輯處理器數，採整機正規化口徑。採樣間隔峰值不是瞬時峰值。程序包含系統匣，測試不註冊全域快捷鍵、不改使用者個人設定。尚非真實遊戲／全螢幕 4K 負載驗收。

| 試驗 | 平均 CPU | 最大觀測工作集 | 最大私有配置 | 結果 |
|---|---:|---:|---:|---|
| 初版：低頻 GPU + 預設繪製 | 0.102% | 163.53 MiB | 107.95 MiB | CPU 通過，工作集未通過 |
| 對照：遊戲不採 GPU | 0.108% | 158.59 MiB | 104.27 MiB | 改善不足，已恢復 GPU |
| 低頻 GPU + 軟體繪製 | 0.106% | 165.35 MiB | 88.82 MiB | 私有配置下降，但總工作集仍未通過 |
| 最終版：全能切入遊戲 + 軟體繪製 | 0.0223% | 165.05 MiB | 85.27 MiB | ValidRun=true，20 次更新；CPU 通過，工作集未通過 |

各輪背景負載、原生資源狀態與 UI 檢查有差異，這是探索性量測，不是嚴格隔離的因果實驗。低於 100 MiB 的私有配置不等於工作集達標。沒有透過 EmptyWorkingSet、定時 GC 或隱藏測試視窗壓低數字。

原始資料：`artifacts/stage-4/game-performance.json`、`game-low-overhead.json`、`game-software-render.json`。前述三輪屬初測；後續量測器新增 ValidRun、MonitoringUpdates，以及每筆採樣期間可見／模式檢查，避免採樣意外暫停造成假低負載。

最終切換情境報告：`artifacts/stage-4/game-after-full.json`，量測 60.09 秒、20 次監控更新、5 秒分段 CPU 最大 0.0893%。這份報告對應保留 GPU 讀數、軟體繪製的最終版，沒有對該次測試視窗進行 UI 自動化操作。

可重現命令（Report 請使用尚不存在的檔名）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure.ps1 -Mode Game -Report artifacts/stage-4/new-game-run.json
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure.ps1 -Mode GameAfterFull -Report artifacts/stage-4/new-transition-run.json
```

測試會開啟可見視窗，完成後自行退出並寫 JSON；GameAfterFull 先在全能系統頁运行 10 秒，再切入遊戲、暖機並量測。量測時不要操作／最小化該視窗。報告與獨立 `.settings.json` 保存在指定的專案 artifacts 中。

100 MiB 工作集門檻目前未達；需要進一步記憶體剖析或評估獨立輕量遊戲宿主，不能把本階段標為全面完成。溫度、GPU 多卡／驅動差異、實體副屏拔線、獨佔全螢幕遊戲、長時間／4K 功耗仍需額外驗證。

## 後續調整：垂直分欄

- 系統頁移除「儲存空間／系統詳情」分區小標題，整合成 CPU、記憶體、GPU、網路、儲存空間、系統與電源六張卡；磁碟進度條集中在儲存卡內。
- 採用獨立垂直流：CPU → GPU → 記憶體固定同欄連續排列，寬畫面保留三欄，其餘卡片按高度分配到另外兩欄中較短的一欄。各欄間與卡片間留 16 DIP；視窗變窄時依序縮為兩欄、單欄，硬體三卡仍相鄰。單卡上限 360 DIP，整組置中，保留外層 1600 DIP 上限及左右留白。優先保持硬體分組，因此不強制各欄等高；遊戲僅四張卡，不建立無內容的第三欄。
- 遊戲模式仍只有四張基本監測卡與高／低採樣選項，不顯示額外硬體詳情或磁碟卡。此次只調整排版，並未重測效能。
- 排版及進度條驗證圖位於 artifacts/stage-4/vertical-columns，使用 TEST FIXTURE 測試資料。

## 後續調整：儲存進度與採樣選項

- 每個固定磁碟以無動畫進度條顯示 `(總容量 - 總剩餘容量) / 總容量`，並列已用／總量、剩餘量、檔案系統。使用 TotalFreeSpace，避免把使用者磁碟配額限制誤當已用容量；單一磁碟失敗不影響其他磁碟。沒有有效容量時顯示不可用，不假造零使用率。
- 一般模式增加處理器型號與可用邏輯處理器、Windows 版本／架構與運行時間、可用實體記憶體、活動網路介面、供電狀態等五張卡片。型號、版本、網卡清單與磁碟只每 30 秒讀取；遊戲模式不讀這些額外資料。
- 遊戲頁提供低／高採樣率：低為 CPU／記憶體／網路 3 秒、GPU 10 秒；高為基本 1 秒、GPU 2 秒。預設低，選擇保存到設定 v2 的 GameHighSampling，舊設定缺少欄位仍為低。兩種設定都不啟動磁碟輪詢、陰影與動畫。
- 切換採樣率先取消並等待舊採樣器釋放，再建立新採樣器，重設差分基準，不疊加背景工作。高採樣率可能增加 CPU 開銷；前述效能報告屬舊版低採樣配置，不代表新版或高採樣已達標，100 MiB 問題仍未解決。
- 新增容量邊界、進度條實際填色比例、採樣率保存／重啟恢復、切換工作釋放與窄視窗排版測試。`artifacts/stage-4/details-and-sampling/` 為明確標示 TEST FIXTURE 的測試資料渲染，不是本機容量或效能證據。
