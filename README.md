# desktop-lyric

Desktop lyrics overlay for Windows. Shows synced lyrics with Chinese translation for whatever's playing in Tidal, Spotify, YouTube in the browser, or any player that shows up in Windows media controls (SMTC).

Windows 桌面歌詞，跟住你而家播緊嘅歌出同步歌詞同中文譯文，預設繁體（香港用字）。

## what it does

- Detects the song playing via Windows SMTC
- Fetches lyrics from Netease, QQ Music, Kugou, LRCLIB and picks the one closest to the song length
- Syncs lyrics to playback position, still ok when the player's position freezes for a few seconds
- Traditional Chinese by default (OpenCC + Hong Kong variants), can turn off with 繁 on the overlay
- Floating overlay you can drag around, clicks go through it when the mouse is not on the lyrics
- 鎖 on the overlay locks the lyrics, so YouTube hover previews or mix titles don't swap the song
- 選 opens the song picker when it matched the wrong song. You can lock the title or the artist and search again, and it remembers your pick next time
- Cleans up YouTube titles, e.g. takes the song name out of 「」 and drops "- Topic"
- Timing editor for live versions: offset, speed, per line shift, LRC import / export
- Karaoke word-by-word highlight (Netease YRC)
- Romaji for Japanese lyrics, fullscreen mode (F11)
- Two looks for the main window, pick in 設定 > 外觀: 經典 (the dark one) or 萃香

## about the 萃香 look

It's a Touhou Project fan theme (伊吹萃香), not official. The picture in `Assets/suika.png` is AI generated (gpt-image), then cleaned with [gpt-anime-denoise](https://github.com/CXP-2024/gpt-anime-denoise) to take away the GPT texture. Not someone else's fan art, so ok to ship with the app.

## install

Download `DesktopLyric-1.1.0-win-x64.zip` from [Releases](https://github.com/Epi-Lo/desktop-lyric/releases), unzip, and keep `DesktopLyric.exe` next to the `Fonts` folder. No install needed, Windows 10 / 11.

Or build from source:

```
dotnet publish src/DesktopLyric/DesktopLyric.csproj -c Release
```

The font files are not in the repo (too big), copy the two `ChironGoRoundTC-*.ttf` from the release zip into `src/DesktopLyric/Fonts/` first. Without them it still runs, just with a normal system font. If your SDK is .NET 10, set `DOTNET_ROLL_FORWARD=LatestMajor`.

## usage

1. Play music in Tidal, Spotify, YouTube, or anything that shows in Windows media controls
2. Run DesktopLyric.exe, the overlay shows up by itself
3. Wrong song? Click 選 on the overlay and pick the right one, then 鎖 if you want it to stay
4. Live version timing off? Open the timing editor and shift it
5. Closing the main window only hides it. To quit, use ✕ on the overlay (when the main window is hidden) or 結束 in the tray icon

## known limits

- Translation quality depends on Google Translate
- Karaoke timing only works when Netease has YRC data for the song
- YouTube mixes often show the channel name as the title, need to pick the song by hand or lock it
- Windows only

## settings

Saved in `%AppData%\DesktopLyric\`:

- `settings.json` - settings, you can edit it by hand or in the app
- `choices.json` - songs you picked with 選
- `offsets.json` - per song timing from the timing editor
- `run.log` - for checking crashes

## lyrics sources

Searches these in parallel, picks the one whose timing best matches track duration:

1. Netease Music (网易云)
2. QQ Music
3. Kugou (酷狗)
4. LRCLIB

## about the commit history

I wrote this on my own pc from Jan to May 2026 and only pushed it to GitHub on 14 May, so every old commit shows the same "verified" time. The commit dates are the real local ones.

Some old commit messages have `鈥?` in them, that was an em dash broken by PowerShell. Can't fix without rewriting history, so I just leave it.

---

made by [Epi-Lo](https://github.com/Epi-Lo) · most of the 1.1.0 stuff (sync fix, OpenCC, timing editor) is from [pxxpkm](https://github.com/pxxpkm), thanks a lot
