using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Lounge.Screens;

/// <summary>Drives a web page from the controller without asking anything of the page.
/// Input goes in through the DevTools protocol as trusted key, mouse and text events, so
/// it works on every site: TV-style sites (YouTube TV) get arrow keys and Enter, every
/// other site gets a pointer drawn inside the page (a native web view covers anything
/// Lounge paints, so the pointer and the hints live in the page itself).</summary>
public sealed class WebPad
{
    private readonly CoreWebView2 _web;
    private double _x = -1, _y = -1;
    private double _vw = 1280, _vh = 720;
    private bool _moveInFlight, _moveDirty;
    private string? _stickDir;
    private long _stickNext;

    public bool PointerMode { get; private set; }

    public WebPad(CoreWebView2 web, bool pointer)
    {
        _web = web;
        PointerMode = pointer;
    }

    /// <summary>Runs in every page before its own scripts: the pointer, the hint bubble
    /// and a report to Lounge whenever a text field takes focus.</summary>
    public const string PageScript = """
        (() => {
          if (window.__lounge) return;
          const L = window.__lounge = {};
          let ptr, hint, timer, show = false;
          const ensure = () => {
            const host = document.body || document.documentElement;
            if (!host) return false;
            if (!ptr) {
              ptr = document.createElement('div');
              ptr.style.cssText = 'position:fixed;left:-100px;top:-100px;width:26px;height:26px;margin:-13px 0 0 -13px;border-radius:50%;' +
                'background:rgba(255,255,255,.92);box-shadow:0 0 0 3px rgba(0,0,0,.35),0 6px 18px rgba(0,0,0,.45);' +
                'z-index:2147483647;pointer-events:none;display:none;transition:transform .08s';
              hint = document.createElement('div');
              hint.style.cssText = 'position:fixed;left:50%;bottom:44px;transform:translateX(-50%);padding:14px 28px;border-radius:20px;' +
                'background:rgba(18,18,22,.88);color:#fff;font:600 20px "Segoe UI Variable","Segoe UI",system-ui,sans-serif;' +
                'z-index:2147483647;pointer-events:none;opacity:0;transition:opacity .35s;white-space:pre-wrap;text-align:center;line-height:1.5;width:max-content;max-width:min(1100px,90vw);box-shadow:0 10px 40px rgba(0,0,0,.5)';
            }
            if (ptr.parentNode !== host) { host.appendChild(ptr); host.appendChild(hint); }
            return true;
          };
          L.mode = (pointer, x, y) => { show = pointer; if (!ensure()) return; ptr.style.display = pointer ? 'block' : 'none'; ptr.style.left = x + 'px'; ptr.style.top = y + 'px'; };
          L.press = down => { if (ensure()) ptr.style.transform = down ? 'scale(.72)' : ''; };
          L.hint = (text, ms) => { if (!ensure()) return; hint.textContent = text; hint.style.opacity = 1; clearTimeout(timer); timer = setTimeout(() => hint.style.opacity = 0, ms || 5000); };
          L.size = () => innerWidth + ',' + innerHeight;
          // What kind of field has focus, for the controller keyboard (never a password's contents).
          L.focused = () => {
            const el = document.activeElement;
            if (!el || !(el.isContentEditable || el.tagName === 'TEXTAREA' || el.tagName === 'INPUT')) return '';
            const password = el.type === 'password';
            return JSON.stringify({ label: el.getAttribute('aria-label') || el.placeholder || el.name || '', value: password ? '' : (el.value || ''), password });
          };
          addEventListener('mousemove', e => { if (show && ensure()) { ptr.style.left = e.clientX + 'px'; ptr.style.top = e.clientY + 'px'; } }, true);
          const editable = el => el && (el.isContentEditable || el.tagName === 'TEXTAREA' ||
            (el.tagName === 'INPUT' && !/^(button|submit|checkbox|radio|range|color|file|image|reset|hidden)$/i.test(el.type)));
          document.addEventListener('focusin', e => {
            const el = e.target;
            if (!editable(el) || !window.chrome || !window.chrome.webview) return;
            const label = el.getAttribute('aria-label') || el.placeholder || el.name || '';
            const search = el.type === 'search' || /search|query|^q$/i.test((el.name || '') + ' ' + (el.id || '') + ' ' + label) || !!el.closest('form[role=search]');
            // A password field's contents never leave the page.
            const password = el.type === 'password';
            window.chrome.webview.postMessage(JSON.stringify({ kind: 'text', label, value: password ? '' : (el.value || ''), search, password }));
          }, true);
        })();
        """;

    public async Task Install()
    {
        await _web.AddScriptToExecuteOnDocumentCreatedAsync(PageScript);
    }

    /// <summary>Called after every navigation: re-measure the page and show the pointer.</summary>
    public async Task PageReady()
    {
        try
        {
            await _web.ExecuteScriptAsync(PageScript);
            var size = JsonSerializer.Deserialize<string>(await _web.ExecuteScriptAsync("window.__lounge.size()")) ?? "";
            var parts = size.Split(',');
            if (parts.Length == 2 && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h) && w > 0 && h > 0)
            {
                _vw = w;
                _vh = h;
            }
            if (_x < 0) { _x = _vw / 2; _y = _vh / 2; }
            _x = Math.Clamp(_x, 0, _vw - 1);
            _y = Math.Clamp(_y, 0, _vh - 1);
            await ShowMode();
        }
        catch (Exception) { }
    }

    public string HelpText => PointerMode
        ? "Left stick: pointer  ·  A: click  ·  Right stick: scroll  ·  Y: keyboard  ·  X: arrow keys  ·  B: back  ·  View: options  ·  Hold B: leave"
        : "D-pad: move  ·  A: select  ·  B: back  ·  Y: keyboard  ·  X: pointer  ·  View: options  ·  Hold B: leave";

    public async Task Hint(string text, int ms = 5000)
    {
        try { await _web.ExecuteScriptAsync($"window.__lounge && window.__lounge.hint({JsonSerializer.Serialize(text)}, {ms})"); }
        catch (Exception) { }
    }

    public async Task TogglePointer()
    {
        PointerMode = !PointerMode;
        await ShowMode();
        await Hint(HelpText, 6000);
    }

    private async Task ShowMode()
    {
        try { await _web.ExecuteScriptAsync($"window.__lounge && window.__lounge.mode({(PointerMode ? "true" : "false")}, {_x:0}, {_y:0})"); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ keys

    private static readonly Dictionary<string, (string Code, int Vk, string? Text)> Keys = new()
    {
        ["ArrowUp"] = ("ArrowUp", 38, null), ["ArrowDown"] = ("ArrowDown", 40, null),
        ["ArrowLeft"] = ("ArrowLeft", 37, null), ["ArrowRight"] = ("ArrowRight", 39, null),
        ["Enter"] = ("Enter", 13, "\r"), ["Escape"] = ("Escape", 27, null), ["Backspace"] = ("Backspace", 8, null),
    };

    /// <summary>How long a key or click is held. YouTube TV ignores Enter on some buttons
    /// ("Watch as guest") when down and up arrive in the same frame, as a real remote never does.</summary>
    public const int PressMs = 70;

    /// <summary>A key press with the key held for <paramref name="holdMs"/>, like a real button.</summary>
    public async Task Key(string key, int holdMs = PressMs)
    {
        var (code, vk, text) = Keys[key];
        object down = text is null
            ? new { type = "rawKeyDown", key, code, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk }
            : new { type = "keyDown", key, code, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk, text, unmodifiedText = text };
        await Send("Input.dispatchKeyEvent", down);
        if (holdMs > 0) await Task.Delay(holdMs);
        await Send("Input.dispatchKeyEvent", new { type = "keyUp", key, code, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk });
    }

    public Task InsertText(string text) => Send("Input.insertText", new { text });

    // ------------------------------------------------------------------ pointer

    public void MovePointer(double dx, double dy)
    {
        _x = Math.Clamp(_x + dx, 0, _vw - 1);
        _y = Math.Clamp(_y + dy, 0, _vh - 1);
        if (_moveInFlight) { _moveDirty = true; return; }
        _ = SendMove();
    }

    private async Task SendMove()
    {
        _moveInFlight = true;
        do
        {
            _moveDirty = false;
            await Send("Input.dispatchMouseEvent", new { type = "mouseMoved", x = _x, y = _y });
        } while (_moveDirty);
        _moveInFlight = false;
    }

    public async Task Click()
    {
        _ = _web.ExecuteScriptAsync("window.__lounge && window.__lounge.press(true)");
        await Send("Input.dispatchMouseEvent", new { type = "mousePressed", x = _x, y = _y, button = "left", buttons = 1, clickCount = 1 });
        await Task.Delay(PressMs);
        await Send("Input.dispatchMouseEvent", new { type = "mouseReleased", x = _x, y = _y, button = "left", buttons = 0, clickCount = 1 });
        _ = _web.ExecuteScriptAsync("window.__lounge && window.__lounge.press(false)");
    }

    public Task Scroll(double dx, double dy) =>
        Send("Input.dispatchMouseEvent", new { type = "mouseWheel", x = PointerMode ? _x : _vw / 2, y = PointerMode ? _y : _vh / 2, deltaX = dx, deltaY = dy });

    /// <summary>In key mode the left stick works like the D-pad: one step when pushed,
    /// then repeating while held.</summary>
    public string? StickToKey(float x, float y, float value, long now)
    {
        if (value < 0.5f) { _stickDir = null; return null; }
        var dir = Math.Abs(x) > Math.Abs(y) ? (x > 0 ? "ArrowRight" : "ArrowLeft") : (y > 0 ? "ArrowUp" : "ArrowDown");
        if (dir != _stickDir) { _stickDir = dir; _stickNext = now + 380; return dir; }
        if (now < _stickNext) return null;
        _stickNext = now + 140;
        return dir;
    }

    public void StickReleased() => _stickDir = null;

    private async Task Send(string method, object args)
    {
        try { await _web.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(args)); }
        catch (Exception) { }
    }
}
