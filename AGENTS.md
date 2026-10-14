# Lounge working and release flows

## Scope and defaults

This repository is Lounge, a controller-first media center for Windows built with
Avalonia/.NET 10, with its own FFmpeg 8 player (a C# port of the sibling `../tivi`)
and a WinForms helper, LoungeAgent. Windows is the only platform. Preserve existing
changes when working in a dirty checkout. Build scripts live at the repository root;
Forge is the sibling `../Forge` project; Cogas's `--big-picture` flag is what the
Cogas tile launches.

`Core/` and `Media/` must never reference Avalonia. A test asserts this; it is what
keeps the router, settings, focus tree, library and the FFmpeg engine testable
without a window. Controller input reaches the app only through `ActionRouter`
actions, never raw buttons, which is what makes every button remappable.

Lounge never reads or writes tivi's live data (`%APPDATA%\fezcode\tivi`). It keeps
its own `config.ini` and `history.ini` in tivi's formats in `%APPDATA%\fezcode\Lounge`.

## Commit messages

Use a normal commit title and body only. Do not add `Co-Authored-By` trailers or
AI/assistant attribution (including Claude or Codex) to commits or release notes.
For multiline messages or messages containing quotes, write a temporary UTF-8
message file (no BOM) and use `git commit -F <file>`. Check the exit code and verify
the resulting commit before tagging; PowerShell 5.1 silently splits inline quoted
messages and will produce a commit that is not the one you wrote.

## Verification

Four layers, none of which may drive the live desktop. Never use SendKeys,
synthetic clicks, or anything that steals focus; launching the app so the user can
use it is fine, driving their desktop is not.

1. `dotnet test tests/Lounge.Tests.csproj` - Core, the focus tree, and the FFmpeg
   engine against clips generated with MSYS2's `ffmpeg.exe` (cached in
   `%TEMP%\lounge-clips-v2`). Media tests are serialised in one collection: they
   share the audio device and run real decoders.
2. `dotnet run --project tools/Checks` - the real screens, headless, driven by a
   virtual pad through the real router.
3. `dotnet run --project tools/Preview -- dist/preview` - every screen in every
   theme to PNG, plus the icon sizes and the README banner.
4. The video path itself: OpenGL cannot render headlessly. Run the published app
   with `LOUNGE_DATA=<temp>` and `LOUNGE_SELFCHECK=<folder>` plus
   `--windowed --no-activate --new-instance --play <clip>`; it plays, reads the
   GL framebuffer back to `gl-frame.png`, writes `player.json` and exits without
   taking focus. Look at the PNG: the test pattern's colour bars must be right.

Headless traps that have already produced wrong results here:

- `new DispatcherTimer(interval, priority, callback)` starts the timer. A timer
  that must stay off until something else starts it is created with
  `new DispatcherTimer(priority) { Interval = ... }`. Getting this wrong made the
  input pump poll the real (empty) XInput during checks and marked the window as
  backgrounded.
- Previews and checks set `Motion.Enabled = false`; springs then snap.
- Image decoding goes through `ImageCache` (two workers, newest first, shared
  in-flight loads). A burst of parallel `Task.Run` decodes starved the pool and
  left posters unloaded. Previews wait for `ImageCache.Busy` before each shot.
- Two `MediaPlayer.Open` calls must never overlap; `Open`/`Close` are serialised.

## RELEASE workflow

Only an explicit request to **RELEASE** triggers the complete publishing flow.
Ordinary fixes, builds, and installer requests do not imply a version bump, commit,
push, tag, or GitHub release. When RELEASE is requested, perform these steps in
order and stop and report any failure before proceeding:

1. Run `./version.ps1 -Bump patch` by default, or `-Set x.y.z` for a requested
   version. Clarify conflicting or ambiguous version instructions. The script keeps
   `Lounge.csproj`, `Agent/LoungeAgent.csproj`, `Core/Product.cs`, the Forge app
   version, the wizard strings and the registry Version synchronized. Run
   `./version.ps1` with no arguments to verify.
2. Run `./build.ps1` to stage FFmpeg, build, run tests, checks and previews,
   publish Lounge and LoungeAgent side by side into `dist/Lounge` (extracted, not
   single-file), and prove the published binaries run via `--selftest` and
   `--version`. Do not skip tests for a release. Replacing this output closes a
   running copy of that build; report that and respect authorization already given.
3. Run `./installer.ps1 -SkipBuild` immediately afterward. Forge requires the
   sibling `../Forge/build/forge.exe` and `uninstall.exe`, produced by
   `gobake build` in Forge. Verify `dist/installer/Lounge-Setup-<version>.exe` and
   surface that exact path for testing. During RELEASE, launch the new Setup
   executable for the user to install and test; an installed copy stays old until
   Setup is run. Keep its option to launch the new app after installation enabled.
   Honor any requested test gate.
4. Review and commit the intended changes without attribution, using a message
   file. Verify the commit landed and record its hash before the next steps.
5. Push the commit to the configured remote and release branch (normally
   `origin main`). Inspect `git remote -v` and the branch first. Never infer a
   missing remote or force-push.
6. Create the matching `vX.Y.Z` tag on the verified commit, push it, and create a
   GitHub release with `gh release create vX.Y.Z`, attaching only the matching
   `dist/installer/Lounge-Setup-X.Y.Z.exe`. Use title `Lounge vX.Y.Z`; notes start
   with `## Lounge vX.Y.Z` followed by `### <feature>` sections and bullets, with
   no emoji. Supply notes through `--notes-file` to avoid PowerShell multiline
   argument splitting. Verify the published asset.

## Build and installer maintenance

- Keep `forge.toml` on the `huh` wizard theme, using the app's icon and identity.
  Step titles double as the rail's labels, so keep them to one word where possible.
- Forge embeds its themes with `go:embed`, so editing a theme without running
  `gobake build` in Forge silently packages the previous one. `installer.ps1`
  fails when `forge.exe` is older than anything under Forge's `themes/`, `cmd/` or
  `internal/`.
- The app writes two Run values itself (`LoungeAgent` when the agent is on,
  `Lounge` when "Open Lounge when Windows starts" is on); `[uninstall]
  registry_values` removes both. Forge stops anything running from the install
  folder (the agent included) before an upgrade copies files.
- Preserve user data under `%APPDATA%/fezcode/Lounge` unless the user chooses the
  installer's option to remove settings and data. It holds settings, the button
  map, tiles, history and the rebuildable library cache.
- `native/` is not committed. `tools/fetch-native.ps1` stages the FFmpeg 8 +
  libass closure from MSYS2 mingw64 (avcodec-62, avformat-62, avutil-60,
  swscale-9, swresample-6, avfilter-11, libass-9 and their dependencies), and
  `build.ps1` runs it. FFmpeg.AutoGen 8.0.0.1 must match that major version.
- Fail on inconsistent versions, failed tests, checks or previews, a missing
  payload, or a non-GUI Setup executable. Never report an old installer as a new
  success. Check GUI Forge exit codes with `Start-Process -Wait -PassThru`.
- Keep `dist` tidy: `Lounge/`, `preview/` and `installer/`; `installer/` keeps only
  the current Setup plus its `logs/`.

These flows adapt Sir Word-a-lot's and clockt's `AGENTS.md` and the user's
release-workflow and no-commit-attribution notes to this repository.
