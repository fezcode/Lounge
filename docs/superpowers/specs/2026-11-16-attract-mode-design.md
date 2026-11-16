# Attract mode (Waiting screen)

## Intent

A waiting screen: when Lounge sits untouched on a menu, it turns into a crime-game
style loading screen. Illustrated movie and Lounge themed scenes with a background and
a foreground layer zoom at different speeds, over a brooding Eastern-European crime-drama
theme. The art and music are original to Lounge (genre homage, not a copy of any game's
art or music).

## Goals

- Setting **Waiting screen** (General, off by default).
- Starts after 30 s without input, Lounge in front, on a menu. Not during playback, on a
  web page, in Starfall, while a launched app runs, or while the dim is up.
- Any input ends it and is swallowed (like `IdleDim`). The 10-minute dim still takes over.
- Ten bg/fg pairs, shuffled, ~8 s each, cuts on the beat, quick dip to black between.
  Background pushes in 1.00 -> ~1.12 with a small drift; the foreground starts larger and
  offset and settles toward its side (parallax). Small Lounge mark in a corner.
- Music "Attract": synthesised in `Core/Audio`, ~90 bpm half-time drums, low strings,
  brass stabs, choir pad, accordion-like lead, harmonic minor. Original melody and
  harmony. Rendered once on first use (~90 s) and looped; uses the ambient volume, and
  the ambient bed fades out under it.

## Non-goals

- No user media, no downloads, no recreated game art, characters or music.
- No new settings beyond the toggle.

## Approaches

- Art: drawn in code (not detailed enough) vs. generated images shipped as files
  (chosen: the user generated them with Gemini).
- Music: synthesised at runtime like the theme ambients (chosen: no licensing, no large
  file) vs. a pre-rendered audio file.

## Assets

Source images in `tmp/` (not committed). `tools/attract/cut.py` keys out the magenta,
trims the pale sticker outline, and writes `Assets/Attract/NN-bg.webp` and
`NN-fg.webp` (alpha). Pairs: 01 harbour+couch, 02 financial district+heist man,
03 ocean drive+projector, 04 desert motel+director's chair, 05 rain alley+woman in red,
06 drive-in+convertible, 07 movie palace+armchair, 08 rooftops+getaway driver,
09 coast road+CRT TV, 10 hills+detective.

## Verification

Tests: the Attract synth is deterministic and not silent; the idle rule (when it may
start) is a pure function in Core and tested. Previews: one attract frame.
