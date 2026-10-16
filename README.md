# AllOff

A minimal Firefox/Chrome WebExtension that switches off every advertiser
consent toggle on the current page with one click.

## How it works

Clicking **Disable all consent toggles** in the popup injects a small script
into the active tab (and every frame it has permission for). The script:

1. Finds every enabled toggle that is switched on: native checkboxes
   (`input[type="checkbox"]`) plus custom div/button switches that expose
   their state accessibly (`role="switch"`/`role="checkbox"` with
   `aria-checked="true"`, `button[aria-pressed="true"]`, or Radix-style
   `data-state="checked"`).
2. Clicks each one so the site's own handlers run. If a click on a native
   input is swallowed, it forces the input off through the native `checked`
   setter and dispatches `input`/`change` events so frameworks like React
   notice.
3. Repeats up to 6 passes, pausing between toggles and passes, because consent
   UIs often re-render while you interact with them.

The popup then reports how many toggles were switched off and how many are
still on.

Nothing runs in the background — the extension only acts on the page where and
when you click the button (`activeTab` + `scripting` permissions only, no host
permissions, no data collection).

## Install (unpacked / temporary)

**Chrome / Edge**

1. Open `chrome://extensions`.
2. Enable **Developer mode**.
3. Click **Load unpacked** and select this folder.
4. Chrome may warn about the `browser_specific_settings` key — that key is for
   Firefox and is safe to ignore.

**Firefox** (142+)

1. Open `about:debugging#/runtime/this-firefox`.
2. Click **Load Temporary Add-on…** and select `manifest.json` in this folder.
3. Temporary add-ons are removed when Firefox restarts; reload it the same way.

## Usage

1. Open the site's consent / privacy settings page (e.g. the HBO Max consent
   dialog).
2. Click the AllOff toolbar icon.
3. Click **Disable all consent toggles**.
4. If the page has a "Save" / "Confirm choices" button, click that yourself —
   the extension only flips the toggles.

## Packaging for release

Zip `manifest.json`, `popup/`, and `icons/` with the manifest at the archive
root and forward-slash entry paths, then upload to
[addons.mozilla.org](https://addons.mozilla.org/developers/). Validate first
with `npx addons-linter <zip>`.

## Limitations

- With only `activeTab`, cross-origin consent iframes (some CMPs such as
  Sourcepoint host the dialog on their own domain) cannot be scripted. Frames
  on the page's own origin work.
- Browser-internal pages (`chrome://`, `about:`, extension stores) cannot be
  scripted.
- It flips *all* checkboxes/switches that are on, not just advertising ones —
  intended for use on consent pages, where that is exactly what you want.
- Fully custom widgets that expose no state at all (pure `<div>` switches with
  no `<input>` and no ARIA attributes) cannot be detected safely and must be
  clicked by hand.
