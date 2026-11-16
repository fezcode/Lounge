# Attract Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A "Waiting screen" setting that, after 30 s idle on a menu, shows a two-layer zooming loading-screen slideshow over an original synthesised crime-drama theme.

**Architecture:** Pure rules, choreography and the music renderer live in `Core` (no Avalonia, testable). `SoundEngine` mixes the rendered loop in place of ambient. A full-window `AttractMode` overlay in `Shell` shows the scenes; `MainWindow` starts and stops it from its idle timer, like `IdleDim`.

**Tech Stack:** .NET 10, Avalonia 11.3, NAudio (existing), xUnit, Python + Pillow for the one-off asset cut.

**Spec:** `docs/superpowers/specs/2026-11-16-attract-mode-design.md`

## Global Constraints

- `Core/` and `Media/` never reference Avalonia.
- Setting key `general.waitingScreen`, label "Waiting screen", default off, category General.
- Idle threshold 30 s; scene length ~8 s, cut on a bar line; bg zoom 1.00 -> 1.12.
- Music: original melody and harmony only; 48 kHz stereo float; deterministic for a seed.
- Assets in `Assets/Attract/NN-bg.webp`, `NN-fg.webp`, NN = 01..10.
- No commit attribution.

## Review Focus

- Input during the dip-to-black or first frame: must dismiss and be swallowed, never reach the menu.
- Dim timer reached while attract runs: attract stops, dim shows; a press clears both.
- Setting turned off while attract is showing: it stops at once.
- Lounge loses the foreground / launches an app while attract runs: it stops, music fades.
- Window aspect not 16:9 (windowed, ultrawide): layers cover the screen with no bars.

---

### Task 1: Cut the assets
**Files:** Create `tools/attract/cut.py`, `Assets/Attract/*.webp`.
- [ ] Key magenta by `min(r,b)-g` against the border median, unmix edges against that colour, erode alpha 2 px to drop the pale sticker outline, crop to the alpha bounding box but keep full-frame placement by saving the fg at full 16:9 size (simplest for layout).
- [ ] Map source files to pairs 01..10 (table in the script), save bg as lossy WebP q85, fg lossy WebP with alpha q85; total under 10 MB.
- [ ] Verify: composite each pair to `tmp/_check-NN.jpg` and look.

### Task 2: Setting, rules and choreography (Core)
**Files:** Modify `Core/Settings/SettingsSchema.cs`; create `Core/Attract/Attract.cs`; test `tests/AttractTests.cs`.
**Produces:**
- `K.WaitingScreen = "general.waitingScreen"`, toggle in General after IdleDimMinutes.
- `static class AttractRules { const long IdleMs = 30_000; static bool CanStart(bool enabled, long idleMs, bool active, bool menu, bool dimmed) }`
- `static class AttractChoreo { const double SceneSeconds; static (int Scene, double T) At(double seconds); static Layer Background(double t); static Layer Foreground(double t, bool left); static double Black(double t) }` with `record struct Layer(double Scale, double X, double Y)` (X/Y as fractions of width/height). `Black` is the dip-to-black opacity, 1 at cuts.
- [ ] Tests: CanStart false under 30 s, false when disabled/dimmed/not active/not a menu, true otherwise; bg scale 1.0 at t=0 and ~1.12 at end, monotonic; fg settles (scale at end < start); Black(0)≈1, Black(mid)=0.

### Task 3: Attract theme (Core/Audio)
**Files:** Create `Core/Audio/AttractTheme.cs`; test in `tests/AttractTests.cs`.
**Produces:** `static class AttractTheme { const double Bpm = 90; static double BarSeconds; static float[] Render(int seed = 1) }` interleaved stereo, length a whole number of bars (~90 s), loops seamlessly.
- [ ] Tests: same seed gives identical output; RMS of every 2 s window > a floor (never silent); peak <= 1.
- [ ] Voices: kick/snare/hat (half-time), timpani on section hits, low string ensemble (detuned saws, slow attack, low-pass), brass stabs, choir pad (formant filtered), accordion lead (two detuned pulse reeds with tremolo), simple stereo reverb. Harmonic minor in D; original phrases.

### Task 4: Mix it (Media)
**Files:** Modify `Media/SoundEngine.cs`.
**Produces:** `void SetAttract(bool on)`: on renders (once, on a worker) and fades the loop in over 1 s while ambient fades to 0; off fades out over 400 ms and restores ambient as `AmbientWanted` says. Level = `AmbientVolume` (min 0.25 so it is audible when ambient volume is tiny).

### Task 5: Overlay and wiring (Shell)
**Files:** Create `Shell/AttractMode.cs`; modify `Shell/MainWindow.cs`.
**Produces:** `sealed class AttractMode : Grid { bool Active; void Set(bool on) }`, ~60 fps timer only while active, bitmaps loaded lazily per scene (current + next) from `avares://Lounge/Assets/Attract/`, shuffled order, Uniform-to-fill cover, small Lounge mark bottom-right.
- [ ] MainWindow: add above `_dim`; in `Watch()` start when `AttractRules.CanStart(...)` (menu = `Top is not PlayerScreen/WebScreen/StarfallScreen` and no player playing); stop when the dim starts, when Dormant/foreground lost, or setting off. In `HandleAction` a press while active stops it, calls `UpdateAmbient()`, returns.

### Task 6: Preview and verification
**Files:** Modify `tools/Preview/Program.cs`.
- [ ] Render one attract frame (scene 1, mid-scene) to `dist/preview/attract.png`.
- [ ] Run `dotnet test`, `tools/Checks`, `tools/Preview`; launch the app for the user.
