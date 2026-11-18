# desktop-lyric

Desktop lyrics overlay for Windows. Shows synced lyrics with Chinese translation for whatever's playing in Tidal (or any SMTC-compatible player).

## what it does

- Detects currently playing song via Windows SMTC
- Fetches lyrics from Netease, QQ Music, Kugou, LRCLIB
- Syncs lyrics to playback position
- Auto-translates to Traditional Chinese
- Floating overlay window you can drag around
- Karaoke word-by-word highlight (Netease YRC)
- Romaji for Japanese lyrics
- Export as .lrc file

## install

Download from [Releases](https://github.com/Epi-Lo/desktop-lyric/releases) — single .exe, no install needed.

Or build from source:

```
dotnet publish src/DesktopLyric/DesktopLyric.csproj -c Release
```

## usage

1. Play music in Tidal (or Spotify, or anything that shows in Windows media controls)
2. Run DesktopLyric.exe
3. Lyrics appear automatically
4. Click "overlay" for floating desktop lyrics

## known limits

- SMTC position can freeze for a few seconds on some players, lyrics might drift briefly
- Translation quality depends on Google Translate
- Karaoke timing only works when Netease has YRC data for the song
- Windows only for now

## settings

Saved to `%AppData%/DesktopLyric/settings.json`. You can edit it manually or through the app.

## lyrics sources

Searches these in parallel, picks the one whose timing best matches track duration:

1. Netease Music (网易云)
2. QQ Music
3. Kugou (酷狗)
4. LRCLIB

## about the commit history

I wrote this on my own pc from Jan to May 2026 and only pushed it to GitHub on 14 May, so every old commit shows the same "verified" time. The commit dates are the real local ones.

Some old commit messages have `鈥?` in them, that was an em dash broken by PowerShell. Can't fix without rewriting history, so I just leave it lor.

---

made by [Epi-Lo](https://github.com/Epi-Lo)
