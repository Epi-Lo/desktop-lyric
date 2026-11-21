# Desktop Lyric

Windows 桌面歌詞 overlay。跟住系統而家播緊嘅歌（網易雲、YouTube、Tidal、Spotify 等 SMTC 播放器），顯示同步歌詞同中文譯文。

Desktop lyrics overlay for Windows. Follows whatever is playing via SMTC, with synced lyrics and Chinese translation.

**v1.1.0** · [下載 Releases](https://github.com/Epi-Lo/desktop-lyric/releases)

---

## 繁體中文

### 新特點（1.1.0）

- **鎖歌詞（字幕窗「鎖」）**  
  凍結而家呢份歌詞。YouTube 縮圖預覽、頻道名、mix 換標題都唔會洗走 overlay。再撳解除，跟返播放器。
- **選歌可分開鎖歌名／歌手**  
  每格旁邊「鎖」：只改歌名、只改歌手，或者兩邊都鎖。**讀取播放中** 會填入未鎖嘅格再搜。
- **點擊穿透**  
  滑鼠唔喺字幕窗上面時，可以撳下面嘅瀏覽器、遊戲、其他程式。開住選歌時 overlay **繼續跟播**。
- **YouTube 歌名清理**  
  `大原ゆい子「ユビオリ」 Live Ver.` 會抽出 **ユビオリ**；`（歌名／歌手）` 動漫 OP 標題同樣抽出。歌手 `- Topic` 會剝走。片長 ≥12 分鐘（mix／長片）唔用來對歌。
- **網易雲搜尋**  
  改用 `cloudsearch`。舊搜尋接口已忽略歌名，日文／中文都會交錯歌。
- **時機編輯**  
  整首偏移／快慢、逐句提早延遲、停留（Live 拖音）、改詞、插入、匯入匯出 LRC（上原文下中文）。
- **記住揀過嘅版本**  
  同名歌用 **選歌** 揀一次，下次自動用。**記憶** 可以改揀或刪除。
- **Karaoke**  
  網易雲 YRC 逐字高亮；日文＋中文砌同一行會拆開，中文做譯文。
- **繁體／香港用字**  
  預設繁體（OpenCC 詞組＋香港異體）。字幕窗「繁」可關。
- **全屏**  
  專輯版面或全幕歌詞（F11）。
- **日文羅馬字**  
  設定入面開 Romaji。

### 安裝

1. 下載 [Releases](https://github.com/Epi-Lo/desktop-lyric/releases) 嘅 `DesktopLyric-1.1.0-win-x64.zip`
2. 解壓。`DesktopLyric.exe` 同 `Fonts\`（昭源圓體 Regular／Medium）要**同一層**
3. 雙擊 exe，唔使安裝。Windows 10／11

自己編譯：

```
dotnet publish src/DesktopLyric/DesktopLyric.csproj -c Release
```

本機 SDK 若係 .NET 10，要 `$env:DOTNET_ROLL_FORWARD = "LatestMajor"`。

### 用法

1. 播歌（網易雲、YouTube、Tidal、Spotify…）
2. 開 `DesktopLyric.exe`，字幕窗自動出現
3. 對錯歌 → overlay **選**；對好之後可撳 **鎖**
4. YouTube mix／頻道名搜唔到 → 選歌窗自己打歌名，鎖住歌手或歌名再搜
5. Live／現場版時機唔啱 → **時機編輯**（偏移、快慢 0.5%、逐句停留）
6. 主窗 ✕ 只係隱藏。真正退出：主窗隱藏時 overlay **✕**，或工作列圖示 **結束**

### 資料同設定

`%AppData%\DesktopLyric\`

| 檔 | 內容 |
|---|---|
| `settings.json` | 繁體、置頂、透明度、字型、開機啟動 |
| `choices.json` | 選歌記住嘅版本 |
| `offsets.json` | 每首歌偏移、快慢、停留、現場詞 |
| `run.log` | 崩潰調查 |

### 歌詞來源

平行搜，優先時機同歌曲長度接近嘅結果（長片除外）：

1. 網易雲
2. QQ 音樂
3. 酷狗
4. LRCLIB

### 限制

- 譯文靠 Google 翻譯，質素視歌曲而定
- 逐字 Karaoke 要網易雲有 YRC
- 只支援 Windows
- YouTube mix 嘅 SMTC 標題好多時係頻道名，要手動選歌或鎖歌詞

---

## English

### What’s new in 1.1.0

- **Lock lyrics** on the overlay so YouTube hover-previews and title flickers do not replace the current song
- **Lock title and artist separately** in the song picker; **Read now playing** fills unlocked fields and searches
- **Click-through overlay** when the cursor is not over the lyrics window; playback keeps updating while the picker is open
- **YouTube title cleanup**: extracts the song inside `「…」` or `（song／artist）`; strips `- Topic`; ignores duration on videos ≥ 12 minutes
- **NetEase search** uses `cloudsearch` (the old search API now ignores the query)
- **Timing editor**: whole-track offset/rate, per-line shift and hold, live text edits, LRC import/export
- **Remembered picks** for same-name tracks; a **記憶** list to retarget or delete
- **Karaoke** from NetEase YRC; packed Japanese + Chinese lines are split
- **Traditional Chinese** by default (OpenCC + Hong Kong variants)
- **Fullscreen** album layout or full-window lyrics (F11)
- **Romaji** for Japanese lyrics (settings)

### Install

Download [`DesktopLyric-1.1.0-win-x64.zip`](https://github.com/Epi-Lo/desktop-lyric/releases) from [Releases](https://github.com/Epi-Lo/desktop-lyric/releases). Unzip so `DesktopLyric.exe` sits next to the `Fonts\` folder. No installer.

Or build: `dotnet publish src/DesktopLyric/DesktopLyric.csproj -c Release`

### Usage

Play a song in any SMTC app, run the exe, and the overlay appears. Use **選** if the wrong track was matched; **鎖** to freeze lyrics. Close the main window to hide it; quit from the overlay ✕ (when the main window is hidden) or the tray **結束**.

Settings and per-track data live in `%AppData%\DesktopLyric\`.

### Lyrics sources

Netease, QQ Music, Kugou, and LRCLIB in parallel.

## about the commit history

I wrote this on my own pc from Jan to May 2026 and only pushed it to GitHub on 14 May, so every old commit shows the same "verified" time. The commit dates are the real local ones.

Some old commit messages have `鈥?` in them, that was an em dash broken by PowerShell. Can't fix without rewriting history, so I just leave it lor.

---

made by [Epi-Lo](https://github.com/Epi-Lo) · most of the 1.1.0 stuff (sync fix, OpenCC, timing editor) is from [pxxpkm](https://github.com/pxxpkm), thanks a lot
