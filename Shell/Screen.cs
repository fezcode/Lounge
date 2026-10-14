using Avalonia;
using Avalonia.Controls;
using Lounge.Controls;
using Lounge.Core.Audio;
using Lounge.Core.Focus;
using Lounge.Core.Input;

namespace Lounge.Shell;

/// <summary>Something that takes controller input: a screen, a panel, a dialog, the
/// keyboard. The window offers each action to the top layer first.</summary>
public interface IInputLayer
{
    /// <summary>The keymap context in force while this layer is on top.</summary>
    string Context { get; }
    /// <summary>True when this layer handled the action.</summary>
    bool Handle(ActionEvent e);
}

/// <summary>A full screen in the navigation stack. Owns a focus scope and gets the
/// standard controller behaviour (move, select, back) for free.</summary>
public abstract class Screen : Grid, IInputLayer
{
    protected FocusScope? Scope { get; private set; }

    public virtual string Context => "Shell";
    public virtual string Title => GetType().Name;
    /// <summary>Ambient music may play while this screen is up.</summary>
    public virtual bool WantsAmbient => true;
    /// <summary>The clock and mini-player chip show over this screen.</summary>
    public virtual bool ShowsChrome => true;
    /// <summary>The theme backdrop shows behind this screen.</summary>
    public virtual bool ShowsBackdrop => true;

    public virtual void OnShown() { }
    public virtual void OnHidden() { }
    /// <summary>Called when the screen is popped for good.</summary>
    public virtual void OnClosed() { }

    protected void UseScope(FocusNode root)
    {
        Scope = new FocusScope(root)
        {
            Locate = item => item.View is Visual v && v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), this) is { } p
                ? new Pt(p.X, p.Y) : null,
        };
        Scope.Moved += (old, now) =>
        {
            if (now?.View is Control c) SmoothScroll.BringIntoView(c, AnchorX, AnchorY);
            OnFocusMoved(old, now);
        };
        Scope.Refresh();
    }

    protected virtual double AnchorX => 96;
    protected virtual double AnchorY => 120;

    protected virtual void OnFocusMoved(FocusItem? old, FocusItem? now) { }

    public FocusItem? Focused => Scope?.Focused;

    public virtual bool Handle(ActionEvent e)
    {
        if (e.Phase is ActionPhase.Released or ActionPhase.Changed) return false;
        switch (e.Action)
        {
            case Act.Up: return MoveFocus(Dir.Up);
            case Act.Down: return MoveFocus(Dir.Down);
            case Act.Left: return MoveFocus(Dir.Left);
            case Act.Right: return MoveFocus(Dir.Right);
            case Act.PageDown: for (int i = 0; i < 4; i++) MoveFocus(Dir.Down, quiet: i > 0); return true;
            case Act.PageUp: for (int i = 0; i < 4; i++) MoveFocus(Dir.Up, quiet: i > 0); return true;
            case Act.Accept:
                if (Scope?.Focused is not { } f) return false;
                (f.View as Card)?.Pulse();
                L.Play(UiSound.Select);
                f.Activate?.Invoke();
                return true;
            case Act.Secondary:
                if (Scope?.Focused?.Secondary is { } s) { L.Play(UiSound.Select); s(); return true; }
                return false;
            case Act.Back:
                return OnBack();
        }
        return false;
    }

    protected bool MoveFocus(Dir d, bool quiet = false)
    {
        if (Scope is null) return false;
        if (Scope.Move(d)) { if (!quiet) L.Play(UiSound.Move); return true; }
        return false;
    }

    /// <summary>B: leave the screen. Return false to let the window decide (exit prompt at root).</summary>
    protected virtual bool OnBack()
    {
        L.Play(UiSound.Back);
        return L.Window.Pop();
    }

    protected void FocusItem(FocusItem item) => Scope?.Focus(item);
    protected void RefreshFocus() => Scope?.Refresh();
}
