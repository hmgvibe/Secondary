# Linux 第一階段：共用層與 Avalonia 外殼

狀態：已確認（2026-10-18），使用者指示進入 L2。實體機（Surface Pro 7，Ubuntu 26.04.1、GNOME 50.1 Wayland）檢查通過；實體鍵盤、外接螢幕與系統匣選單點擊留待日後。

## 交付內容

- 新增 Secondary.Shared（net10.0）。本階段只搬入外殼需要的部分：單一實例協調（SingleInstanceCoordinator）、操作紀錄（AppDiagnostics）及新增的 AppPaths。天氣、遊戲遙測、版面幾何等其他共用邏輯，在各自所屬階段（L2、L4）再搬，避免一次改動 Windows 太多程式碼。
- Windows 行為不變：設定與紀錄仍在 `%LOCALAPPDATA%\Secondary`，單一實例的身分識別與「顯示」指令維持原樣。新增的「切換遊戲模式」指令，WPF 版會拒絕且不做任何動作。
- 新增 Secondary.Linux（Avalonia 12.1.3，執行檔名 `secondary`）：
  - 底部導航「首頁｜快捷導航｜遊戲模式｜設定」，遊戲中為「監測｜退出｜設定」；退出遊戲回到之前的日常頁。
  - 設定為獨立頁面，Esc 或「返回」關閉。
  - 導航欄提供標準／小兩種大小、上下左右四個位置。小尺寸在縮放後仍保留超過 44 DIP 的操作範圍，並預留分數縮放造成的捨入誤差。
- 色彩使用與 WPF 相同的色票；明暗可選跟隨系統、淺色或深色，遊戲模式固定深色。系統高對比與減少動畫設定經 XDG 桌面入口（portal）讀取，變更時即時更新。
- 首頁顯示真實時間與日期，只在首頁可見時每秒更新；離頁、最小化或隱藏時停止。快捷導航與遊戲頁只有說明文字，不執行捷徑或腳本，也不取樣或顯示示意數值。
- 副屏與全螢幕：
  - 從 Avalonia 列出螢幕，以接口名稱作為識別；沒有名稱或名稱重複時改用螢幕位置。
  - 全螢幕前會把視窗移到選定的螢幕，之後回報視窗實際所在的螢幕。若桌面不允許移動，會提示使用者先把視窗拖到副屏。
- 系統匣：Ubuntu 預設的 AppIndicator 可顯示系統匣圖示，選單提供顯示、進出遊戲模式與退出。偵測不到系統匣時，關閉視窗會改為最小化，避免視窗再也找不回來。
- 遊戲模式快捷鍵：
  - 啟動時以應用程式 ID `io.github.hmgvibe.Secondary` 向入口服務登記，再經 GlobalShortcuts 介面要求綁定快捷鍵（預設 Ctrl+Alt+G）。是否允許由桌面詢問使用者，結果顯示在設定頁。
  - GNOME 只允許有已安裝 .desktop 檔的應用程式 ID 綁定全域快捷鍵。設定頁新增「桌面整合」：使用者按「加入應用程式選單」後，才會寫入 `~/.local/share/applications/io.github.hmgvibe.Secondary.desktop` 與圖示（啟動器包含「進入／退出遊戲模式」動作），接著重新連線入口服務並要求綁定。可再按「從選單移除」刪除；AppImage 換了位置時會提示更新。
  - 入口服務不支援或拒絕時，設定頁提供 `secondary --toggle-game` 指令，可複製後在「GNOME 設定 → 鍵盤 → 自訂快捷鍵」中綁定。在 AppImage 內會自動改用 AppImage 檔案本身的路徑。
- 設定存於 `$XDG_CONFIG_HOME/secondary`（預設 `~/.config/secondary`），紀錄存於 `$XDG_STATE_HOME/secondary/logs`。不讀取或遷移 Windows 設定檔。另有 `--profile=目錄` 可指定獨立設定，`--startup-check=檔案` 供打包驗證使用。
- 發布：
  - scripts/build-linux.sh：鎖定還原、依賴漏洞查詢、Release 建置、核心測試與 Linux 外殼測試。
  - scripts/package-linux.sh：產生自包含的 linux-x64 AppImage、相同內容的 tar.gz 與 SHA256SUMS，並在打包後實際啟動一次候選版本。
  - appimagetool 1.9.1 與 type2-runtime 20251108 以 SHA256 固定版本（scripts/fetch-appimage-tools.sh）。
  - CI 新增 ubuntu-latest 工作：先執行建置與測試，再在虛擬螢幕（xvfb）下打包並啟動候選版本。
- Windows 的 build.ps1 也會以無頭模式執行 Linux 外殼測試；建置與打包腳本都已關閉 Avalonia 的建置遙測（`AVALONIA_TELEMETRY_OPTOUT`）。

## 驗證方法

Linux 外殼測試（tests/Secondary.Linux.Tests）在 Windows 與 Linux 都可執行，共 79 項，涵蓋：

- XDG 路徑規則：預設值、絕對路徑覆寫、相對路徑會被忽略。
- 啟動參數解析與 AppImage 指令路徑：含空白與引號的引用、不信任相對路徑。
- 快捷鍵格式轉換、螢幕識別。
- 啟動器的 Exec 引用與跳脫、寫入與移除、指向其他位置的偵測，寫入時不留下暫存檔。
- 單一實例的雙指令往返，以及只支援顯示的接收端（Windows 版）會拒絕切換遊戲指令。
- 以 Avalonia Headless 搭配 Skia 實際渲染的視窗流程：
  - 導航、遊戲模式的進出與返回頁、設定頁與 Esc、主題與色票。
  - 高對比、減少動畫、快捷鍵驗證與保存、無系統匣時改為最小化、時鐘的啟停。
  - 快捷鍵被拒時引導到「桌面整合」，加入選單後重新連線，綁定成功時顯示桌面提供的按鍵說明。
  - 小導航在四個位置的 44 DIP 操作範圍與不遮擋內容。
  - 6 種視窗尺寸 × 2 種主題 × 4 個頁面共 48 種組合，皆無水平溢出。

Windows 端在 Release 下完整執行 build.ps1，依賴查詢完成：核心 233 項、WPF UI 6,732 項全數通過，與搬移前的基準數量相同（當時 Linux 外殼為 65 項；加入桌面整合後的 79 項已在 Windows 與 Linux 上另行通過）。

註：本機在 150% 縮放下，現有 WPF 檢查「小導航保留 44 DIP」會失敗（43.73 DIP），這是接手前就存在、使用者決定先擱置的問題。為了讓這項之後的 WPF 檢查都能跑到，基準與本輪驗證時暫時讓這一項不中斷測試，驗證後已還原，未提交這項修改。Linux 版已在導航尺寸計算中預留捨入誤差。

本機另以 `--startup-check` 啟動了真實的 Avalonia 視窗：視窗正常渲染後自行關閉，暫存設定已刪除。

## 實體機結果

2026-10-18 經 SSH 在使用者的 Surface Pro 7（Ubuntu 26.04.1 LTS、GNOME Shell 50.1 Wayland、xdg-desktop-portal 1.21.1、portal-gnome 50.0，內建螢幕 eDP-1 2736×1824、200% 縮放）上執行，GUI 程式在使用者已登入的 GNOME 工作階段中開啟：

- build-linux.sh：依賴查詢完成，核心 233 項、Linux 外殼 79 項通過；單一實例在 Linux 上走 Unix socket。
- package-linux.sh：AppImage 與 tar.gz 建置成功，打包後的啟動檢查通過。
- AppImage 啟動檢查的紀錄：
  - 螢幕辨識為 eDP-1，視窗位於該螢幕，跟隨系統淺色主題，高對比與動畫設定都讀得到。
  - 偵測到系統匣。渲染畫面中的 Noto CJK 字型、時鐘與導航都正常。
- 系統匣：Secondary 的 StatusNotifierItem 已登記到 GNOME 的 AppIndicator 監看服務。
- 單一實例：
  - 第二次以 `--toggle-game` 啟動時，190 ms 內轉給執行中的視窗，進入遊戲模式並保存。
  - 再切一次會回到日常模式；一般的第二次啟動只叫出原視窗，執行中始終只有一個 Secondary。
  - 以 SIGTERM 強制結束後可以重新啟動，鎖與 socket 都能回收。
- 全域快捷鍵：
  - 沒有啟動器時，向入口服務登記應用程式 ID 會失敗（`org.freedesktop.portal.Error.Failed`），綁定也被拒絕（`NotAllowed`），設定頁會引導使用者到「桌面整合」。
  - 暫時放入一個 .desktop 檔後，登記成功，綁定請求進入等待使用者在 GNOME 對話框中確認的狀態。
  - 使用者在這次測試跳出的對話框中按了允許。GNOME 把 Ctrl+Alt+G 記在 dconf 的 `/org/gnome/settings-daemon/global-shortcuts/io.github.hmgvibe.Secondary`，之後不會為同一個快捷鍵再次詢問。
  - 據此新增了「桌面整合」，並刪除了那個暫時的 .desktop 檔。
  - 使用者在 Secondary 中按了「加入應用程式選單」，寫入的啟動器指向 AppImage 檔案本身，圖示也一併寫入。
  - Surface 沒有接實體鍵盤，所以經 GNOME Mutter RemoteDesktop 介面送出 Ctrl+Alt+G：第一次進入遊戲模式，第二次回到日常模式，模式都有保存。
  - 結束並重新啟動 Secondary 後，不需任何操作再送一次，同樣能進出遊戲模式，沒有新的錯誤紀錄。GNOME 不會在每次啟動時重新詢問。

## 尚未驗證

- 用實體鍵盤按 Ctrl+Alt+G 的效果。上面的測試以 Mutter 送出按鍵事件，走的是同一條合成器快捷鍵路徑。
- 測試機只有內建螢幕，指定副屏全螢幕需要接上外接螢幕才能驗證。
- 系統匣選單項目的點擊與高對比畫面，尚未以人工操作確認。
- GitHub CLI 未登入，CI 的 Linux 工作仍沒有執行結果。
- AppImage 尚未附上 .NET、Avalonia、SkiaSharp 等套件的授權聲明，列入 L5。

## 實機驗收步驟

```bash
./scripts/build-linux.sh
./scripts/package-linux.sh
./artifacts/linux/release/Secondary-v1.0.2-linux-x86_64.AppImage
```

測試機上已建好 `~/Secondary/artifacts/linux/release/Secondary-v1.0.2-linux-x86_64.AppImage`。請在桌面上確認：

1. 導航與設定頁的外觀、字型。
2. 接上實體鍵盤時，Ctrl+Alt+G 能否進出遊戲模式（啟動器已加入，GNOME 已允許）。
3. 系統匣圖示與選單的三個項目。
4. 接上外接螢幕時，選擇該螢幕後「進入副屏全螢幕」是否落在正確的螢幕。
5. 活動總覽中的 Secondary 啟動器，以及右鍵選單的「進入／退出遊戲模式」動作。
