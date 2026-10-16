const api = globalThis.browser ?? globalThis.chrome;

const button = document.getElementById("disable");
const status = document.getElementById("status");

// Runs inside the page. Clicks every enabled, switched-on toggle so the
// site's own handlers fire. Covers native checkboxes plus custom div/button
// switches that expose state via ARIA or data-state; if a click on a native
// input is swallowed, forces it off via the native setter and synthesizes
// input/change events (framework-friendly). Repeats a few passes because
// consent UIs often re-render between toggles.
async function disableConsentToggles() {
  const setChecked = Object.getOwnPropertyDescriptor(
    HTMLInputElement.prototype,
    "checked"
  ).set;
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  const SEL = [
    'input[type="checkbox"]',
    '[role="switch"]',
    '[role="checkbox"]',
    '[aria-checked="true"]',
    'button[aria-pressed="true"]',
    '[data-state="checked"]',
  ].join(", ");

  const isOn = (el) =>
    el instanceof HTMLInputElement
      ? el.checked
      : el.getAttribute("aria-checked") === "true" ||
        el.getAttribute("aria-pressed") === "true" ||
        el.getAttribute("data-state") === "checked";

  const isBlocked = (el) =>
    el.disabled === true ||
    el.getAttribute("aria-disabled") === "true" ||
    el.getAttribute("role") === "radio"; // unchecking a radio is impossible

  let toggled = 0;

  for (let pass = 0; pass < 6; pass++) {
    let on = [...document.querySelectorAll(SEL)].filter(
      (el) => isOn(el) && !isBlocked(el)
    );
    // Nested matches (e.g. a role="switch" wrapper around a real checkbox)
    // would double-toggle; keep only the innermost of each pair.
    on = on.filter((el) => !on.some((o) => o !== el && el.contains(o)));
    if (!on.length) break;

    for (const el of on) {
      el.click();
      if (el instanceof HTMLInputElement && isOn(el)) {
        setChecked.call(el, false);
        el.dispatchEvent(new Event("input", { bubbles: true }));
        el.dispatchEvent(new Event("change", { bubbles: true }));
      }
      toggled++;
      await sleep(30);
    }
    await sleep(150); // let the page re-render before the next pass
  }

  const remaining = [...document.querySelectorAll(SEL)].filter(isOn).length;
  return { toggled, remaining };
}

button.addEventListener("click", async () => {
  button.disabled = true;
  status.className = "";
  status.textContent = "Working…";

  try {
    const [tab] = await api.tabs.query({ active: true, currentWindow: true });
    if (!tab?.id) throw new Error("No active tab found.");

    const results = await api.scripting.executeScript({
      target: { tabId: tab.id, allFrames: true },
      func: disableConsentToggles,
    });

    const frames = (results ?? [])
      .map((r) => r?.result)
      .filter((r) => r && typeof r.toggled === "number");

    if (!frames.length) {
      // Some browser versions don't resolve async injected functions.
      status.className = "ok";
      status.textContent = "Done.";
    } else {
      const toggled = frames.reduce((n, r) => n + r.toggled, 0);
      const remaining = frames.reduce((n, r) => n + r.remaining, 0);
      status.className = remaining ? "" : "ok";
      status.textContent = remaining
        ? `Switched off ${toggled}, but ${remaining} still on.`
        : toggled
          ? `Switched off ${toggled}. All clear.`
          : "Nothing was switched on.";
    }
  } catch (err) {
    status.className = "error";
    status.textContent = /cannot be scripted|missing host permission|Cannot access/i.test(
      String(err?.message)
    )
      ? "This page can't be scripted (browser or store pages)."
      : `Failed: ${err?.message ?? err}`;
  } finally {
    button.disabled = false;
  }
});
