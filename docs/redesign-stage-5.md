# 第五階段：真實遊戲儀表

保留已確認的科幻介面、CPU／GPU／顯存／記憶體四個儀錶與 B1／B2 折線圖；移除所有示意值及假曲線。未前往第六階段。

## 使用與來源

- 進入可見遊戲頁才啟動整機監測。低採樣基本 3 秒、GPU PDH 10 秒；高採樣基本 1 秒、GPU PDH 2 秒；獨立 UI 每秒刷新一次。
- 「選擇遊戲程序」使用自繪選單，可搜尋名稱／PID。僅列出有權讀取開始時間的程序；顯示最多 80 個搜尋結果。保存 PID 與開始時間以拒絕 PID 重用；不自動找遊戲或切換目標，程序退出後要求手動重選。
- CPU／記憶體／網路為整機數據。未啟用硬體助手時，GPU 為所有顯示卡的最忙引擎，不冒充選定遊戲使用率。
- 「啟用硬體感測」預設關閉且不持久保存。啟用後單一助手保持連線，低／高模式每 10／2 秒讀取一次（查詢本身耗時另計），不反覆啟動助手。多 GPU 時可選裝置；同一 GPU 的名稱、使用率、溫度、時脈、功耗及顯存保持一致。缺少廠商對應欄位則顯示不可用。
- CPU 底層來源受 PawnIO／權限限制；未安裝時略過並顯示原因。可用的 CPU 感測以同類最高值顯示，名稱有標註，不將核心功耗相加當成套件功耗。
- 曲線保留最近 60 秒（上限 121 點），以實際時間定位，無資料斷線。網路收送共用 Y 軸刻度、實線／虛線區分；幀曲線顯示 ms 刻度。

## 幀統計

- 固定官方 PresentMon 2.6.0 x64 console，驗證雜湊；明確使用 `--v2_metrics --qpc_time_ms`，stdout 有界解析。
- 按官方固定 tag 的 [CsvOutput.cpp](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/PresentMon/CsvOutput.cpp) 修正第四階段解析器：實際 v2 欄位是 `FrameTime / CPUBusy / CPUWait`；`CPUStartQPCTime` 是毫秒，預設 `CPUStartTime` 是秒。不能沿用舊文件中的欄名或把秒當毫秒。
- FPS 為最近一秒有效應用程式幀間隔平均值的倒數，不是螢幕刷新率或 UI 更新率；沒有聲稱量測所有插幀後的顯示 FPS。
- 同一 PID 最近 60 秒事件最多的 swapchain 單獨計算，不混合多個交換鏈。1% Low 取最慢 `ceil(N×1%)` 幀間隔的平均值再取倒數，少於 100 幀顯示等待。
- 最多 60,000 幀，達到容量上限時停用 1% Low，避免把截短資料冒充完整窗口。無新幀 3 秒即停用讀数；換程序／切採樣／暫停重新開始清除窗口。
- 權限拒絕、元件遺失、雜湊不符、來源停止、無新幀各有訊息，FPS 不以 0 或固定數字代替。

## 生命週期與權限

- 離開遊戲、設定、隱藏、最小化、鎖定、睡眠取消；返回後重新建立基準。切換採樣先等待舊一代工作停止，不疊加查詢。
- PresentMon 每次唯一 session，只清理自己建立的 session；不提權、不改群組、不停止其他捕獲。應用退出等待清理最多 5 秒。
- 硬體助手只接受固定單次／高低串流模式，以繼承匿名管道回傳；父端每 5 秒續租，20 秒沒有續租或 stdin 關閉則退出。單次查詢仍有 20 秒 watchdog。沒有任意命令或硬體控制功能。
- 不安裝任何驅動，不注入遊戲、不使用 overlay。

## 驗證與限制

2026-09-23 收尾驗證：Release 0 warnings / 0 errors；170 個核心測試、6,057 個一般 UI 斷言，以及 6,069 個含實機整合的斷言通過。已檢查發布輸出的 PresentMon 雜湊、助手與授權檔。未進入第六階段。

- 一般 Release 核心與 80 組視窗／缩放／主題回歸；選用 `--game-telemetry` 測試真實 CPU、記憶體、持續硬體助手、取消退出、PID 身分檢查及 UI 切頁／最小化。
- 本機 GPU 溫度、時脈、功耗、顯存已讀取；一般權限 PresentMon 仍回報 ETW access denied。成功幀捕獲及允許權限下 ETW session 清理尚未實機通過，不宣稱已驗證真實 FPS。
- 主程序被強制終止／崩潰時 PresentMon 的殘留 session 回收、長時間總資源趨勢、實體副屏與最終打包仍屬後續驗證；硬體助手已有父程序失聯退出機制。

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.tools/dotnet'
& ./tests/Secondary.UiTests/bin/Release/net10.0-windows/Secondary.UiTests.exe artifacts/redesign/stage-5 --game-telemetry
```

實測 HUD 截圖為 `artifacts/redesign/stage-5/game-LIVE.png`，只證明該次測試的應用畫面，不代表實體副屏已驗收。
