# Lounge — Design Spec

**Date:** 2026-10-14
**Status:** Draft for review
**Location:** `D:\Workhammer\Lounge`

## 1. Purpose

Lounge turns a Windows PC into a living-room media center driven **entirely by an Xbox controller**. It launches straight into a full-screen "big picture" shell, plays local video and music through its own player (a C# port of tivi), runs streaming sites in embedded WebView2, launches apps and games (including Cogas big picture), and manages files — all without a keyboard or mouse.

### Success criteria (from the user)

1. **Premium UI** — a 2026 Apple-grade feel: depth, blur, spring motion, couch-legible type.
2. **Everything configurable** — every behaviour, key binding, theme parameter and path is adjustable from inside the app.
3. **No keyboard or mouse** — every task, including renaming files, adding library folders and entering URLs, is completable with the controller alone.

### Non-goals (v1)

- Online metadata scraping (TMDB etc.). Art comes from embedded tags and sidecar files.
- macOS/Linux. Windows-first (XInput, WebView2, Game Bar interplay).
- Reading or writing tivi's live settings/history. Lounge is self-contained (see §6).
- DVR / live TV / network shares discovery (SMB folders work if mapped/browsable).

## 2. Stack

- .NET 10, Avalonia 11 (latest stable), single solution.
- FFmpeg via **FFmpeg.AutoGen**, shipping the same FFmpeg DLL set as tivi (`avformat avcodec avutil swscale swresample avfilter`) plus **libass** via P/Invoke.
- WebView2 for web tiles.
- SQLite (Microsoft.Data.Sqlite) for the library cache.
- XInput via hand-written `[DllImport("xinput1_4.dll")]`, derived from Cogas `Services/XInputPad.cs`.
- Release flow cloned from Cogas: `version.ps1`, `build.ps1`, `installer.ps1`, `forge.toml` (Forge installer).

## 3. Processes

| Executable | Role |
|---|---|
| `Lounge.exe` | The full-screen shell. Single instance; a second launch focuses the first. |
| `LoungeAgent.exe` | Tiny per-user background helper started at login (Run key, toggle in settings). No Avalonia; Win32 only, near-zero CPU. |

**Agent behaviour:** polls XInput slots at 1 Hz (raised to 10 Hz for 2 s after any change). On a **controller connected** transition it shows a borderless, topmost toast: *"Would you like to go to Lounge?  Ⓐ Yes · Ⓑ No"* (auto-dismiss after 10 s). Ⓐ launches or focuses Lounge. It stays silent when Lounge is already running, when a full-screen exclusive app has focus (configurable: *always / not over full-screen apps / never*), or during a configurable quiet period after a "No". It is not a Windows Service, because Session-0 services cannot draw UI on the user's desktop.

The agent reads its options from Lounge's `settings.json` (`agent.*` keys) and reloads on file change.

## 4. Module map (inside Lounge.exe)

Each module has one purpose and talks to others through the listed interfaces.

| Module | Responsibility | Key interfaces |
|---|---|---|
| `Input` | Poll XInput (digital buttons, analog triggers and sticks, connect/disconnect). Map raw input → **actions** via the keymap. Owns input *contexts* (Shell, Player, Keyboard, Web, Cursor, Dormant). | `IPadSource`, `IActionRouter`, `Keymap` |
| `Shell` | Window, navigation stack, spatial focus engine, toasts/dialogs, on-screen keyboard, home screen host. | `INavigator`, `IFocusEngine`, `IDialogService`, `IOnScreenKeyboard` |
| `Themes` | Theme definitions: colour/material tokens + home **layout variant** + ambient track set + sounds. Live switch. | `IThemeService`, `ThemeDefinition` |
| `Player` | Video/audio decode and render (port of tivi). | `IPlayer`, `PlaybackSession` |
| `Library` | Folder roots, scanning, natural sort, art extraction, SQLite cache. | `ILibrary` |
| `Audio` | Ambient music engine, UI sounds, system master volume (Core Audio). | `IAmbient`, `ISystemVolume` |
| `Web` | WebView2 tiles, gamepad passthrough, cursor mode, DRM fallback. | `IWebHost` |
| `Launcher` | App/game/URL tiles, process launch, focus tracking, dormant mode, return chord. | `ILauncher` |
| `Files` | Controller file explorer and file operations. | `IFileOps` |
| `Settings` | Single typed settings model, schema metadata, persistence, migration, change notifications; settings UI generated from schema. | `ISettings`, `SettingDescriptor` |
| `Agent` | Separate project `LoungeAgent`. | — |

**Rule:** no module reads gamepad state directly. All behaviour is triggered by actions from `IActionRouter`, which is what makes every binding remappable and lets tests drive the app with a virtual pad.

## 5. Input

### 5.1 Actions and contexts

An action is a named intent (`Nav.Up`, `Nav.Accept`, `Nav.Back`, `Player.SeekForwardVariable`, `Player.SeekBackwardVariable`, `Volume.Up`, `Volume.Down`, `App.Settings`, `App.ContextMenu`, `App.Home`, `Cursor.Toggle`, `Keyboard.Backspace`, …). The keymap binds **inputs** (button, button-hold ≥N ms, chord, trigger pressure band, stick direction) to actions **per context**. The active context stack decides which bindings apply; unhandled actions fall through to the parent context.

### 5.2 Default bindings

**Global (Shell):** D-pad / left stick = navigate · A = accept · B = back · Menu = Settings · View = context menu · **hold View 600 ms = Home** · RB/LB = system volume ± · right stick = fast scroll.

**Player:** A = play/pause · **RT/LT = variable-speed seek forward/back** (trigger pressure bands → 2×, 4×, 8×, 16×, 32×; release resumes normal playback at the new position) · D-pad ←/→ = −/+10 s · D-pad ↑/↓ = −/+60 s · RB/LB = volume · Y = tracks panel · X = info overlay · B = stop/back · View = context menu.

**Guide button:** not used by default because Windows Game Bar intercepts it. Settings → Controller offers "Use Guide as Home" with an explanation that Game Bar must be disabled.

### 5.3 Remote screen (remapping UI)

Settings → Controller → **Remote** shows a large Xbox controller illustration. Moving focus across the buttons on the drawing highlights each one and lists its bindings per context. Ⓐ on a binding → "Press the new input" capture (supports holds and chords; B+B cancels). Conflicts are shown inline with a swap/replace choice. Includes analog options (stick dead zone, trigger thresholds, seek speed bands, vibration on/off and strength) and **Reset context / Reset all**.

### 5.4 Dormant mode and the return chord

When Lounge launches an external app (game, Cogas, Steam, exe) and that process's window has foreground, Lounge enters **Dormant**: every input is ignored except the **return chord** (default: hold View + Menu 1 s; configurable). XInput is non-exclusive, so the launched app receives the controller unaffected. The return chord brings Lounge to the foreground; leaving the external app running or offering "Close app" is a per-tile setting. When the launched process exits, Lounge un-dormants and refocuses automatically.

### 5.5 Cursor mode

Toggle with hold View + B (configurable). Left stick moves a virtual pointer with acceleration, right stick scrolls, A = left click, X = right click, Y = on-screen keyboard (types via `SendInput` into the focused control). Available in web tiles always and in launched apps only when the tile has "Allow controller mouse" enabled (default off; never on for games).

## 6. Player (port of tivi)

Lounge clones tivi's playback behaviour in C#. Reference sources: `D:\Workhammer\tivi\src\player.c`, `audio_out.c`, `yuvtex.c`, `subs.c`, `playlist.c`, `watch.c`, `viconfig.c`.

### 6.1 Pipeline

- Demux/decode: FFmpeg; hardware decode with D3D11VA when available, software fallback (setting: Auto/Off).
- Video: frames are uploaded to the GPU and converted from YUV to RGB in a shader (NV12, P010, yuv420p), rendered inside an Avalonia GPU-backed control so overlays (OSD, subtitles, panels) draw natively above the video. CPU `sws_scale` fallback.
- Audio: master clock. Output through WASAPI shared mode. Speed 0.25–2× via `atempo` filter graph (pitch correction on) or resampling (off) — same as tivi.
- Seek: `av_seek_frame` backward to keyframe then decode forward to the exact target. **Variable-speed scrub (RT/LT)** is seek-stepping: while held, the player issues keyframe seeks at a rate matched to the speed band and shows a thumbnail strip plus timestamp; audio is muted during scrub; on release it does an exact seek and resumes.
- Subtitles: libass for text formats (ASS/SSA directly; SRT/VTT/SUB/SBV via FFmpeg conversion), bitmap subs (PGS/VOBSUB/DVB) via FFmpeg. Sidecar auto-detection identical to tivi (`movie.srt`, `movie.<lang>.srt`, preferred language setting).
- Codec coverage equals tivi's FFmpeg build (H.264, HEVC, AV1, VP9, ProRes…; AAC, AC-3, DTS, Opus, FLAC, TrueHD…).

### 6.2 Player UI

Auto-hiding OSD: title, progress with chapters, time, and a "Next up" card 20 s before the end (natural-order folder queue, as tivi). **Y tracks panel** (tivi-style side panel): audio track, subtitle track, subtitle delay, speed, picture adjust (brightness/contrast/saturation/hue/gamma), aspect/zoom. Resume prompt on open ("Resume from 42:10 / Start over").

### 6.3 Music

Same decoder in audio-only mode. **Now Playing** screen: album-art backdrop (blurred), track info, queue, shuffle/repeat. Music continues in the background while browsing the shell; a mini-player chip appears on the home screen. Ambient music stays muted while music is playing.

### 6.4 tivi compatibility

"Fully compatible" means **same formats and behaviour, separate data**:
- Lounge keeps its **own** `config.ini` (tivi's `key=value` format and key names for shared concepts — sub language, pitch_correct, hw_decode, picture adjust, resume_playback…) and its **own** `history.ini` (`position|duration|sub_delay_ms|last_seen_unix|path`, 400-entry cap) in `%APPDATA%\fezcode\Lounge\`.
- Lounge never reads or writes `%APPDATA%\fezcode\tivi\*`.
- Same FFmpeg DLL set and versions as tivi's bundle.
- Optional tile/context action "Open in tivi" when tivi is installed (useful fallback; not required).

## 7. Shell and UI

### 7.1 Design language (criterion 1)

- 1920×1080 design grid, uniformly scaled to the display (4K-crisp vector and image assets).
- Minimum text size 22 px at 1080p; titles 40–64 px; Inter-style variable font (bundled).
- Focus is shown with scale (1.0 → 1.08), lift shadow, specular sheen and soft glow, all **spring-animated** (stiffness/damping tokens per theme). There are no hard focus rectangles.
- Frosted-glass (acrylic-style blur) surfaces over dynamic backdrops; parallax on the focused hero image.
- UI sounds (move, select, back, error), per theme, toggleable.
- Performance switch: "Reduce effects" (no blur, simpler motion) for weak GPUs. The target is 60 fps on the focus engine at 4K.

### 7.2 Themes (≥4, ship 5)

A theme = tokens (colours, materials, radii, motion springs, fonts) + **home layout variant** + ambient track set + sound set.

| Theme | Inspiration | Home layout |
|---|---|---|
| **Aurora** (default) | Apple TV | Top tab bar; full-bleed hero of focused item; translucent content rows. |
| **Living Room** | Samsung/LG smart TV | Content backdrop with bottom launcher ribbon; apps first. |
| **Kodi Classic** | XBMC/Kodi | Left vertical main menu (Videos/Music/Apps/Web/Files/Settings); fanart backdrop; list views with metadata. |
| **Console One** | PS5 | Top row of large icons; dynamic backdrop of selected item; deep blue. |
| **Series** | Xbox Series | Dense tile grid, grouped "Jump back in", green accent. |

Appearance settings: theme, accent override, wallpaper per theme (picked via file explorer), blur on/off, animation speed, UI scale, sounds on/off and volume.

### 7.3 Home content

Sections (order and visibility configurable): **Jump back in** (from history), **Videos**, **Music**, **Apps & Games** (incl. Cogas), **Web** (streaming tiles), **Files**, **Settings**. Tiles can be pinned, reordered (hold A → move mode) and hidden from the controller.

### 7.4 Settings UI (tivi style)

A right-side slide-in panel, as in tivi: letter-spaced uppercase section headers; rows are toggles, steppers (D-pad ←/→), choice lists, sliders, path pickers (open the file explorer), key captures. Changes autosave and apply live. Every row has "Reset to default" (View menu); a global "Reset all" sits under About.

Sections: General · Appearance · Controller (incl. Remote) · Playback · Subtitles · Audio (incl. Ambient) · Library · Web · Launcher · Agent · About.

The UI is **generated from `SettingDescriptor` metadata** (key, type, range, default, section, label, help text). A setting that exists in code but not in the UI is impossible by construction (criterion 2). A unit test asserts every descriptor renders a row.

### 7.5 On-screen keyboard (criterion 3)

Full-screen overlay with a large QWERTY grid, number row, and a symbols layer.
- **Bindings:** X = backspace, Y = space, LB/RB = cursor left/right, LT = shift/symbols, RT = suggestion accept, Menu = done, B = cancel.
- **Suggestion strip:** the original name, recent entries, common fragments.
- **Rename mode:** the extension is locked (toggle to unlock); the name stem is pre-selected so one "clear" removes it.
- Used everywhere text is needed: rename, new folder, search, URL entry, tile names.

### 7.6 Dialogs and toasts

All are controller-dismissable, with the primary action on Ⓐ and cancel on Ⓑ. They never require pointer focus.

## 8. Files (controller file explorer)

- Two views: list (details) and grid (thumbnails). Start points: drives, Home folders, library roots, recent.
- Operations: open (routes media to the Player, executables to Launcher confirm), rename (on-screen keyboard), new folder, copy/cut/paste (clipboard held in-app), delete (to Recycle Bin; permanent delete behind a confirm and a setting), properties.
- Multi-select: X toggles selection; View → bulk actions.
- **Library folders:** on any folder, View → "Add to Videos library" / "Add to Music library". Settings → Library lists the roots with remove/rescan. No paths are typed.
- Long operations show progress toasts and can be cancelled. Errors (in use, access denied) offer Retry/Skip/Cancel.

## 9. Library

- Roots: Videos and Music lists in settings (added via Files).
- Scan on startup (incremental by mtime) and on demand. A FileSystemWatcher picks up changes while running.
- Video: grouping by folder; poster from `poster.jpg/folder.jpg/<name>.jpg` or an embedded cover, else a generated frame thumbnail (cached).
- Music: artist/album/track from tags (FFmpeg metadata); cover from embedded art or `cover.jpg/folder.jpg`.
- Natural sort (port tivi's `playlist.c` ordering).
- Cache: `library.db` (SQLite) + `thumbs\` folder. Safe to delete; it is rebuilt.

## 10. Audio

- **System volume:** RB/LB adjust the Windows master volume via Core Audio, with a large on-screen volume HUD. Step size is configurable.
- **Ambient music (PS4-style):** plays while in the shell and nothing else is producing media. It fades in after 2 s and fades out within 400 ms when the Player, music, a web tile or a launched app starts, then resumes on return. Settings: on/off, volume, source = *theme's bundled tracks* or *a folder picked in the file explorer* (shuffled), crossfade length.
- Bundled ambient tracks must be original or royalty-free (licensed for redistribution). Sources are recorded in `assets/ambient/CREDITS.md`.

## 11. Web tiles

- Embedded WebView2 with a persistent profile in `%APPDATA%\fezcode\Lounge\webview\` (logins persist).
- Default tiles (editable): YouTube (`youtube.com/tv`), Netflix, Disney+, Prime Video, Twitch. Tile editor: name, URL (on-screen keyboard), icon, user agent override, "Allow controller mouse", "Open externally".
- **Input:** the page receives the controller via the browser Gamepad API by default. Lounge reserves only the return chord (back to home) and the cursor-mode toggle.
- **DRM fallback:** WebView2 Widevine support is limited. If playback is blocked or the user selects it, "Open in full browser" launches Edge/Chrome in kiosk/full-screen and Lounge goes Dormant (return chord brings it back).
- Missing WebView2 runtime: a one-button installer prompt.

## 12. Launcher

- Tile types: **Executable** (path picked via Files, args, working dir), **URL/protocol** (`steam://open/bigpicture`), **Store app** (AUMID), **Cogas Big Picture**.
- **Cogas change (separate repo work):** add a `--big-picture` CLI flag to Cogas that opens `BigPictureWindow` on startup. Lounge's Cogas tile runs `Cogas.exe --big-picture`. The Cogas path is auto-detected from its install location, with an override in settings.
- Lounge tracks the launched process and its top-level window to drive Dormant mode (§5.4). Per tile: "On return chord: switch back / close app / ask".

## 13. Persistence

All Lounge data lives in `%APPDATA%\fezcode\Lounge\`:

| File | Content |
|---|---|
| `settings.json` | Typed settings model with a `schemaVersion` and forward migrations. |
| `keymap.json` | Bindings per context. |
| `tiles.json` | Home tiles (apps, web, Cogas, pins, order). |
| `config.ini` | tivi-format player settings (§6.4). |
| `history.ini` | tivi-format resume history (§6.4). |
| `library.db`, `thumbs\` | Rebuildable cache. |
| `webview\` | WebView2 profile. |
| `logs\` | Rolling logs (7 days). |

Writes are atomic: write a temp file, then replace. A file that fails to parse is renamed to `*.corrupt-<timestamp>`, defaults are loaded, and a toast explains what happened.

## 14. Error handling

| Situation | Behaviour |
|---|---|
| Controller disconnects | Full-screen "Reconnect your controller" overlay; playback pauses; dismisses itself on reconnect. |
| No controller at launch | Same overlay, plus a hint about pairing. Keyboard/mouse still work silently as a fallback, but they are never required. |
| File won't decode | "Can't play this file" card: Skip / Open in tivi (if installed) / Details. |
| Library root missing | Root shown greyed out with "Remove / Retry". |
| Launch fails | Toast with the reason and an "Edit tile" action. |
| WebView2 missing / DRM blocked | §11 fallbacks. |
| Agent toast can't take focus | Retry once, then fall back to a Windows notification. |
| Unhandled exception | Logged; a controller dialog offers "Restart Lounge". |

## 15. Testing

- **Unit:** keymap resolution (holds, chords, contexts, fall-through), action router, settings schema (every descriptor renders; defaults; migration), INI read/write round-trips for `config.ini` and `history.ini` (cap, atomic write), natural sort, library scanner, file ops (rename collisions, Recycle Bin path).
- **Virtual pad:** `IPadSource` fake that drives scripted input sequences; UI tests complete key flows controller-only (rename a file, add a library folder, change theme, remap a button, play and seek).
- **Headless Avalonia snapshot tests:** each theme's home screen, settings panel, on-screen keyboard, and player OSD, checked against stored baselines.
- **Player:** sample clips (H.264, HEVC 10-bit, AV1, with SRT/ASS/PGS subs) checked for seek accuracy within one frame, A/V drift under 40 ms over 60 s, speed changes, and hardware/software decode parity.
- Manual checklist per milestone on real hardware (TV at 4K, Xbox controller).

## 16. Milestones

Each milestone leaves Lounge usable.

| # | Scope |
|---|---|
| **M1** | Solution scaffold and release scripts; Input (XInput, keymap, contexts, virtual pad); Shell (navigation, focus engine, dialogs, toasts); Aurora theme; settings model plus generated tivi-style settings UI; on-screen keyboard; file explorer with library-folder adding; Remote remapping screen. |
| **M2** | Player (FFmpeg port, HW decode, GPU YUV, libass, variable-speed RT/LT scrub, tracks panel, resume via `history.ini`, `config.ini`); Library scanning and home rows; Music and Now Playing; system volume HUD. |
| **M3** | Themes Living Room, Kodi Classic, Console One, Series (layouts plus tokens); ambient music engine and bundled tracks; UI sounds. |
| **M4** | Web tiles (WebView2, gamepad passthrough, cursor mode, DRM fallback); Launcher tiles, Dormant mode, return chord; Cogas `--big-picture` flag. |
| **M5** | LoungeAgent (connect detection, toast, quiet rules, startup registration); Forge installer; v1.0.0 release. |
