using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Screen = Lounge.Shell.Screen;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Audio;
using Lounge.Core.Input;
using Lounge.Core.Library;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Lounge.Media;
using Lounge.Shell;

namespace Lounge.Screens;

/// <summary>Full-screen playback. A plays and pauses; RT/LT fast-forward and rewind with
/// speed following trigger pressure (2x plays at double speed, beyond that it scrubs
/// through keyframes with a preview strip, then settles exactly where you let go);
/// the D-pad skips; Y opens audio, subtitles, speed and picture; X shows details.</summary>
public sealed class PlayerScreen : Screen
{
    private IVideoSurface _surface;
    private readonly Grid _videoHost = new() { Background = Brushes.Black };
    private readonly Image _subs = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private WriteableBitmap? _subsBitmap;
    private long _subsGeneration = -1;
    private readonly Grid _osd = new();
    private readonly Spring _osdSpring;
    private readonly TextBlock _title = Ui.Text("", T.Title, Brushes.White, FontWeight.Bold);
    private readonly TextBlock _subtitle = Ui.Text("", T.Body, new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)));
    private readonly TextBlock _elapsed = Ui.Text("0:00", T.Body, Brushes.White, FontWeight.SemiBold);
    private readonly TextBlock _remaining = Ui.Text("", T.Body, new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), FontWeight.SemiBold);
    private readonly Border _progressFill = new() { Background = Brushes.White, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _scrubHead = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
    private readonly Canvas _chapterTicks = new() { IsHitTestVisible = false };
    private readonly Grid _progressTrack = new() { Height = 8 };
    private readonly Border _seekBadge = new();
    private readonly TextBlock _seekText = Ui.Text("", 44, Brushes.White, FontWeight.Bold);
    private readonly Spring _badgeSpring;
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Spring _stripSpring;
    private readonly Border _nextUp = new();
    private readonly TextBlock _nextUpTitle = Ui.Text("", T.Body, Brushes.White, FontWeight.SemiBold);
    private readonly Spring _nextUpSpring;
    private readonly Border _info = new();
    private readonly TextBlock _infoText = new() { FontSize = T.Caption, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, LineHeight = 34 };
    private readonly Control _pauseGlyph;
    private readonly Spring _pauseSpring;

    private string _path;
    private bool _alive = true;
    private double _lastInteraction;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrameTime;
    private bool _framesSeen;
    private IDisposable? _glWatchdog;

    // trigger seeking
    private enum SeekMode { None, FastPlay, Scrub }
    private SeekMode _seekMode;
    private int _seekDir;
    private double _seekSpeed;
    private double _scrubPos;
    private double _lastScrubSend;
    private double _baseSpeed = 1;
    private Thumbnailer? _thumbs;
    private double _lastStripAt = -1;
    private bool _stripBusy;

    // skipping
    private double _skipTarget = double.NaN;
    private double _skipAt;
    private int _lastSubStream = -1;
    private bool _nextUpShown;
    private bool _endHandled;

    public override string Context => "Player";
    public override string Title => "Player";
    public override bool WantsAmbient => false;
    public override bool ShowsChrome => false;
    public override bool ShowsBackdrop => false;

    public PlayerScreen(string path, bool? resume = null)
    {
        _path = path;
        Background = Brushes.Black;
        _surface = MakeSurface();
        _videoHost.Children.Add(_surface.View);
        _videoHost.Children.Add(_subs);
        Children.Add(_videoHost);

        // ---- OSD
        var top = new Border
        {
            Background = Ui.VerticalFade(Colors.Black, 0.75, 0),
            Height = 260,
            VerticalAlignment = VerticalAlignment.Top,
            Child = Ui.V(6, _title, _subtitle).Margin(96, 56, 96, 0),
        };
        _progressTrack.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), CornerRadius = new CornerRadius(4) });
        _progressTrack.Children.Add(_progressFill);
        _progressTrack.Children.Add(_chapterTicks);
        var progressRow = new Grid { Height = 30 };
        progressRow.Children.Add(_progressTrack.Align(HorizontalAlignment.Stretch, VerticalAlignment.Center));
        progressRow.Children.Add(_scrubHead);
        var times = Ui.Grid("Auto,*,Auto", "Auto");
        times.Children.Add(_elapsed.Col(0));
        times.Children.Add(Hints.Bar("Player", (Act.PlayPause, "Pause"), (Act.SeekForward, "Fast forward"), (Act.Tracks, "Audio & subtitles"), (Act.Info, "Info"), (Act.Stop, "Stop"))
            .Align(HorizontalAlignment.Center, VerticalAlignment.Center).Col(1));
        times.Children.Add(_remaining.Col(2));
        var bottom = new Border
        {
            Background = Ui.VerticalFade(Colors.Black, 0, 0.85),
            Height = 360,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = Ui.V(18, _strip, progressRow, times).Align(HorizontalAlignment.Stretch, VerticalAlignment.Bottom).Margin(96, 0, 96, 56),
        };
        _osd.Children.Add(top);
        _osd.Children.Add(bottom);
        _osd.IsHitTestVisible = false;
        Children.Add(_osd);
        // Subtitles rise above the controls while they show, so nothing hides a line.
        var subsLift = new TranslateTransform();
        _subs.RenderTransform = subsLift;
        _osdSpring = new Spring(1, v => { _osd.Opacity = Math.Clamp(v, 0, 1); subsLift.Y = -200 * Math.Clamp(v, 0, 1); }, 220, 26);
        _elapsed.MinWidth = 160;
        _remaining.MinWidth = 160;
        _remaining.TextAlignment = TextAlignment.Right;
        _stripSpring = new Spring(0, v => { _strip.Opacity = Math.Clamp(v, 0, 1); _strip.Height = 150 * Math.Clamp(v, 0, 1); }, 280, 28);

        // ---- seek badge
        _seekBadge.Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        _seekBadge.CornerRadius = new CornerRadius(50);
        _seekBadge.Padding = new Thickness(44, 18);
        _seekBadge.HorizontalAlignment = HorizontalAlignment.Center;
        _seekBadge.VerticalAlignment = VerticalAlignment.Center;
        _seekBadge.Child = _seekText;
        _seekBadge.IsHitTestVisible = false;
        Children.Add(_seekBadge);
        _badgeSpring = new Spring(0, v => { _seekBadge.Opacity = Math.Clamp(v, 0, 1); _seekBadge.RenderTransform = new ScaleTransform(0.9 + 0.1 * v, 0.9 + 0.1 * v); }, 400, 30);

        _pauseGlyph = new Border
        {
            Width = 150, Height = 150, CornerRadius = new CornerRadius(75),
            Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            Child = Icons.Make("pause", 70, Brushes.White).Align(HorizontalAlignment.Center, VerticalAlignment.Center),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        Children.Add(_pauseGlyph);
        _pauseSpring = new Spring(0, v => _pauseGlyph.Opacity = Math.Clamp(v, 0, 1), 300, 26);

        // ---- next up
        _nextUp.Background = new SolidColorBrush(Color.FromArgb(220, 20, 20, 24));
        _nextUp.CornerRadius = new CornerRadius(T.Radius);
        _nextUp.Padding = new Thickness(30, 22);
        _nextUp.HorizontalAlignment = HorizontalAlignment.Right;
        _nextUp.VerticalAlignment = VerticalAlignment.Bottom;
        _nextUp.Margin = new Thickness(0, 0, 96, 300);
        _nextUp.Child = Ui.V(8, Ui.Text("NEXT UP", T.Caption, new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), FontWeight.Bold, letterSpacing: 3), _nextUpTitle,
            Hints.Bar("Player", (Act.NextItem, "Play now")));
        _nextUp.IsHitTestVisible = false;
        Children.Add(_nextUp);
        _nextUpSpring = new Spring(0, v => { _nextUp.Opacity = Math.Clamp(v, 0, 1); _nextUp.RenderTransform = new TranslateTransform(60 * (1 - v), 0); }, 260, 26);

        // ---- info
        _info.Background = new SolidColorBrush(Color.FromArgb(200, 10, 10, 14));
        _info.CornerRadius = new CornerRadius(T.Radius);
        _info.Padding = new Thickness(32, 26);
        _info.HorizontalAlignment = HorizontalAlignment.Left;
        _info.VerticalAlignment = VerticalAlignment.Top;
        _info.Margin = new Thickness(96, 200, 0, 0);
        _info.MaxWidth = 900;
        _info.Child = _infoText;
        _info.IsVisible = false;
        Children.Add(_info);

        L.Player.StateChanged += OnState;
        L.Player.TracksChanged += OnTracks;
        Load(path, resume);
        _ = Loop();
    }

    private IVideoSurface MakeSurface()
    {
        if (L.Settings.Bool(K.GpuConvert))
        {
            var gl = new GlVideoSurface { Adjust = Adjust() };
            // If OpenGL never initialises (no ANGLE, remote desktop), fall back to the CPU path.
            _glWatchdog = DispatcherTimer.RunOnce(() =>
            {
                if (gl.Initialised && gl.Healthy) return;
                SwitchToCpu(gl.Error ?? "OpenGL is unavailable");
            }, TimeSpan.FromSeconds(2.5));
            return gl;
        }
        return new CpuVideoSurface { Adjust = Adjust() };
    }

    private void SwitchToCpu(string why)
    {
        if (_surface is CpuVideoSurface) return;
        _videoHost.Children.Remove(_surface.View);
        _surface = new CpuVideoSurface();
        _videoHost.Children.Insert(0, _surface.View);
        // The decoder must hand over BGRA now; reopen at the same spot.
        var pos = L.Player.Position;
        var opts = L.PlayerOptions();
        opts.GpuConvert = false;
        Reopen(opts, pos);
        L.Window.Toast("Using the CPU video path (" + why + ").", "info", 4);
    }

    private static PictureAdjust Adjust() => new(
        L.Settings.Double(K.Brightness), L.Settings.Double(K.Contrast), L.Settings.Double(K.Saturation),
        L.Settings.Double(K.Hue), L.Settings.Double(K.Gamma));

    // ------------------------------------------------------------------ opening

    public void Load(string path, bool? resume)
    {
        _path = path;
        _endHandled = false;
        _nextUpShown = false;
        _nextUpSpring.To(0);
        var item = L.Library.Get(path);
        _title.Text = item?.Title ?? LibraryScanner.CleanTitle(Path.GetFileNameWithoutExtension(path));
        _subtitle.Text = item is not null ? item.FolderName : Path.GetDirectoryName(path) ?? "";
        _thumbs?.Dispose();
        _thumbs = null;
        var opts = L.PlayerOptions();
        if (_surface is CpuVideoSurface) opts.GpuConvert = false;

        double? resumeAt = null;
        var point = L.Settings.Bool(K.ResumePlayback) ? L.History.ResumePoint(path) : null;
        if (point is { } p && resume != false)
        {
            if (resume == true || !L.Settings.Bool(K.ResumePrompt)) resumeAt = p;
            else
            {
                L.Window.Ask("Resume?", $"You stopped at {Ui.Time(p)}.", [$"Resume from {Ui.Time(p)}", "Start over"], 1,
                    i => Reopen(opts, i == 0 ? p : 0), "play");
                ShowOsd();
                return;
            }
        }
        Reopen(opts, resumeAt ?? 0);
        ShowOsd();
    }

    private void Reopen(PlayerOptions opts, double at)
    {
        var path = _path;
        _framesSeen = false;
        _ = Task.Run(() =>
        {
            var ok = L.Player.Open(path, opts, at);
            Dispatcher.UIThread.Post(() =>
            {
                if (!_alive) return;
                if (!ok) { Failed(L.Player.LastError ?? "This file can't be played."); return; }
                if (at > 0.5) L.Window.Toast($"Resumed at {Ui.Time(at)}", "play", 2.5);
                L.Player.Play();
                BuildChapterTicks();
                KeepAwake(true);
                _thumbs = null;
                _ = Task.Run(() => { var t = Thumbnailer.Open(path, 256); Dispatcher.UIThread.Post(() => { if (_alive && _path == path) _thumbs = t; else t?.Dispose(); }); });
            });
        });
    }

    private void Failed(string message)
    {
        L.Play(UiSound.Error);
        var tivi = FindTivi();
        var buttons = new List<string> { "Back" };
        if (L.Queue.PeekNext() is not null) buttons.Insert(0, "Skip to next");
        if (tivi is not null) buttons.Add("Open in tivi");
        L.Window.Ask("Can't play this file", message, buttons.ToArray(), 0, i =>
        {
            var choice = buttons[Math.Max(0, i)];
            if (choice == "Skip to next" && Playback.PlayNext()) return;
            if (choice == "Open in tivi" && tivi is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tivi, $"\"{_path}\"") { UseShellExecute = true });
            Stop();
        }, "warning");
    }

    private static string? FindTivi()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "tivi", "tivi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "tivi", "tivi.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void BuildChapterTicks()
    {
        _chapterTicks.Children.Clear();
        var dur = L.Player.Duration;
        if (dur <= 0) return;
        var width = Math.Max(1, _progressTrack.Bounds.Width > 0 ? _progressTrack.Bounds.Width : L.Window.DesignWidth - 192);
        foreach (var (start, _) in L.Player.Chapters.Skip(1))
            _chapterTicks.Children.Add(new Border { Width = 3, Height = 8, Background = Brushes.Black, [Canvas.LeftProperty] = width * start / dur });
    }

    // ------------------------------------------------------------------ frame loop

    private async Task Loop()
    {
        while (_alive)
        {
            var tcs = new TaskCompletionSource();
            var top = TopLevel.GetTopLevel(this);
            if (top is null) { await Task.Delay(16); continue; }
            top.RequestAnimationFrame(_ => tcs.TrySetResult());
            await tcs.Task;
            if (!_alive) break;
            try { Frame(); }
            catch (Exception e)
            {
                // One bad frame must not stop playback; note it once for self-checks.
                if (SelfCheck.Active) SelfCheck.Report("player-error", new { error = e.ToString() });
            }
        }
    }

    private void Frame()
    {
        var now = _sw.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastFrameTime, 0, 0.1);
        _lastFrameTime = now;
        var p = L.Player;

        if (p.TakeFrame() is { } frame)
        {
            if (!_framesSeen) { _framesSeen = true; L.Player.Subtitles.SetVideoSize(frame.Width, frame.Height); }
            if (_surface is CpuVideoSurface && frame.Pixels != FramePixels.Bgra) frame.Dispose();
            else _surface.Present(frame);
        }
        p.Tick();

        // Trigger scrubbing advances the target smoothly.
        if (_seekMode == SeekMode.Scrub)
        {
            _scrubPos = Math.Clamp(_scrubPos + _seekDir * _seekSpeed * dt, 0, Math.Max(0, p.Duration - 0.5));
            if (now - _lastScrubSend > 0.08) { p.ScrubTo(_scrubPos); _lastScrubSend = now; }
            UpdateStrip();
        }

        var pos = _seekMode == SeekMode.Scrub ? _scrubPos : !double.IsNaN(_skipTarget) && now - _skipAt < 0.8 ? _skipTarget : p.Position;
        var dur = p.Duration;
        _elapsed.Text = Ui.Time(pos);
        _remaining.Text = dur > 0 ? "-" + Ui.Time(dur - pos) : "";
        var width = _progressTrack.Bounds.Width;
        var frac = dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0;
        _progressFill.Width = width * frac;
        _scrubHead.Margin = new Thickness(width * frac - 11, 0, 0, 0);
        _scrubHead.Opacity = _seekMode == SeekMode.Scrub ? 1 : 0;

        RenderSubtitles(pos);

        // Next up, as on a streaming service.
        var nextUpAt = L.Settings.Int(K.NextUpSeconds);
        var next = L.Queue.PeekNext();
        var show = next is not null && dur > 0 && dur - pos <= nextUpAt && pos > 5 && L.Settings.Bool(K.AutoQueue);
        if (show != _nextUpShown)
        {
            _nextUpShown = show;
            _nextUpTitle.Text = next is null ? "" : LibraryScanner.CleanTitle(Path.GetFileNameWithoutExtension(next));
            _nextUpSpring.To(show ? 1 : 0);
        }

        // Hide the OSD after a while.
        var osdFor = L.Settings.Int(K.OsdSeconds);
        if (p.IsPlaying && _seekMode == SeekMode.None && now - _lastInteraction > osdFor && _osd.Opacity > 0.99 && !OverlayOpen)
            _osdSpring.To(0);
        _pauseSpring.To(!p.IsPlaying && p.State == PlayerState.Paused && _seekMode == SeekMode.None && _framesSeen ? 1 : 0);
        if (_info.IsVisible) UpdateInfo();
        if (SelfCheck.Active && !_checkDone && _framesSeen && p.IsPlaying && p.Position > 2.0) RunSelfCheck();
    }

    private bool _checkDone;

    /// <summary>LOUNGE_SELFCHECK: capture what the video surface really drew, report, exit.</summary>
    private void RunSelfCheck()
    {
        _checkDone = true;
        var p = L.Player;
        void Finish(string renderer, int frames)
        {
            SelfCheck.Report("player", new
            {
                renderer, frames, position = p.Position, hw = p.HardwareDecoder, width = p.VideoWidth, height = p.VideoHeight,
                subtitles = p.Subtitles.HasTrack, subtitlesVisible = _subs.IsVisible, state = p.State.ToString(),
            });
            DispatcherTimer.RunOnce(() => L.Window.Exit(), TimeSpan.FromMilliseconds(300));
        }
        if (_surface is GlVideoSurface gl)
        {
            gl.CaptureNext = (rgba, w, h) => Dispatcher.UIThread.Post(() =>
            {
                // GL rows are bottom-up RGBA; flip into top-down BGRA.
                var bgra = new byte[rgba.Length];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int s = ((h - 1 - y) * w + x) * 4, d = (y * w + x) * 4;
                        bgra[d] = rgba[s + 2]; bgra[d + 1] = rgba[s + 1]; bgra[d + 2] = rgba[s]; bgra[d + 3] = 255;
                    }
                using (var wb = ImageCache.FromBgra(bgra, w, h)) wb.Save(SelfCheck.PathFor("gl-frame.png"));
                Finish("gpu", gl.FramesDrawn);
            });
            gl.InvalidateVisual();
            gl.RequestNextFrameRendering();
        }
        else
        {
            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var rtb = new RenderTargetBitmap(size);
            rtb.Render(this);
            rtb.Save(SelfCheck.PathFor("cpu-frame.png"));
            Finish("cpu", 0);
        }
    }

    private bool OverlayOpen => L.Window.Top != this;

    private void RenderSubtitles(double pos)
    {
        var area = _videoHost.Bounds;
        if (area.Width <= 0 || !L.Player.Subtitles.HasTrack) { _subs.IsVisible = false; return; }
        var vw = L.Player.VideoWidth > 0 ? L.Player.VideoWidth : 16;
        var vh = L.Player.VideoHeight > 0 ? L.Player.VideoHeight : 9;
        var aspect = vw / (double)vh;
        double rw = area.Width, rh = area.Width / aspect;
        if (rh > area.Height) { rh = area.Height; rw = rh * aspect; }
        var scale = L.Window.PixelScale;
        int pw = Math.Max(2, (int)(rw * scale)), ph = Math.Max(2, (int)(rh * scale));
        var data = L.Player.Subtitles.Render(pos, pw, ph, out var changed);
        var gen = L.Player.Subtitles.Generation;
        if (data is null) { _subs.IsVisible = false; return; }
        if (!changed && gen == _subsGeneration && _subs.IsVisible) return;
        _subsGeneration = gen;
        if (_subsBitmap is null || _subsBitmap.PixelSize.Width != pw || _subsBitmap.PixelSize.Height != ph)
        {
            _subsBitmap = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _subs.Source = _subsBitmap;
        }
        using (var fb = _subsBitmap.Lock())
            Marshal.Copy(data, 0, fb.Address, Math.Min(data.Length, fb.RowBytes * ph));
        _subs.Width = rw;
        _subs.Height = rh;
        _subs.IsVisible = true;
        _subs.InvalidateVisual();
    }

    private void UpdateStrip()
    {
        if (_thumbs is null || _stripBusy) return;
        var interval = Math.Max(5, _seekSpeed * 2.5);
        var anchor = Math.Round(_scrubPos / interval) * interval;
        if (Math.Abs(anchor - _lastStripAt) < 0.01) return;
        _lastStripAt = anchor;
        _stripBusy = true;
        var t = _thumbs;
        _ = Task.Run(() =>
        {
            var shots = Enumerable.Range(-2, 5).Select(k => Math.Max(0, anchor + k * interval)).Select(x => t.At(x)).ToList();
            Dispatcher.UIThread.Post(() =>
            {
                _stripBusy = false;
                if (!_alive || _seekMode != SeekMode.Scrub) return;
                _strip.Children.Clear();
                for (int i = 0; i < shots.Count; i++)
                {
                    var s = shots[i];
                    var center = i == 2;
                    var w = center ? 240 : 200;
                    var img = new Border
                    {
                        Width = w, Height = w * 9 / 16.0, CornerRadius = new CornerRadius(12), ClipToBounds = true,
                        BorderBrush = center ? Brushes.White : null, BorderThickness = new Thickness(center ? 3 : 0),
                        Background = s is null ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : new ImageBrush(ImageCache.FromBgra(s.Bgra, s.Width, s.Height)) { Stretch = Stretch.UniformToFill },
                        Opacity = center ? 1 : 0.7,
                        VerticalAlignment = VerticalAlignment.Bottom,
                    };
                    _strip.Children.Add(img);
                }
            });
        });
    }

    // ------------------------------------------------------------------ input

    private void ShowOsd()
    {
        _lastInteraction = _sw.Elapsed.TotalSeconds;
        _osdSpring.To(1);
    }

    public override bool Handle(ActionEvent e)
    {
        var p = L.Player;
        if (e.Action is not (Act.VolumeUp or Act.VolumeDown)) ShowOsd();
        switch (e.Action)
        {
            case Act.SeekForward or Act.SeekBackward:
                TriggerSeek(e);
                return true;
        }
        if (e.Phase is ActionPhase.Released or ActionPhase.Changed) return true;

        switch (e.Action)
        {
            case Act.PlayPause or Act.Accept:
                if (e.Phase == ActionPhase.Repeated) return true;
                if (_nextUpShown && e.Action == Act.Accept) { Playback.PlayNext(); return true; }
                p.TogglePause();
                L.Play(UiSound.Toggle);
                return true;
            case Act.SkipForward or Act.Right: Skip(L.Settings.Int(K.SkipSeconds)); return true;
            case Act.SkipBack or Act.Left: Skip(-L.Settings.Int(K.SkipSeconds)); return true;
            case Act.BigSkipForward or Act.Up: Skip(L.Settings.Int(K.BigSkipSeconds)); return true;
            case Act.BigSkipBack or Act.Down: Skip(-L.Settings.Int(K.BigSkipSeconds)); return true;
            case Act.NextItem:
                if (!Playback.PlayNext()) L.Window.Toast("This is the last one in the queue.", "info");
                return true;
            case Act.PrevItem: Playback.PlayPrevious(); return true;
            case Act.Tracks: OpenTracks(); return true;
            case Act.Info:
                _info.IsVisible = !_info.IsVisible;
                UpdateInfo();
                return true;
            case Act.ToggleSubtitles: ToggleSubtitles(); return true;
            case Act.ContextMenu: Options(); return true;
            case Act.Stop or Act.Back:
                L.Play(UiSound.Back);
                Stop();
                return true;
        }
        return false;
    }

    private void Skip(double seconds)
    {
        var now = _sw.Elapsed.TotalSeconds;
        var from = !double.IsNaN(_skipTarget) && now - _skipAt < 0.8 ? _skipTarget : L.Player.Position;
        _skipTarget = Math.Clamp(from + seconds, 0, Math.Max(0, L.Player.Duration - 1));
        _skipAt = now;
        L.Player.Seek(_skipTarget);
        Badge((seconds > 0 ? "+" : "âˆ’") + (Math.Abs(seconds) >= 60 ? $"{Math.Abs(seconds) / 60:0} min" : $"{Math.Abs(seconds):0} s"));
        L.Play(UiSound.Move);
    }

    private void Badge(string text, bool stay = false)
    {
        _seekText.Text = text;
        _badgeSpring.To(1);
        if (!stay) DispatcherTimer.RunOnce(() => { if (_seekMode == SeekMode.None) _badgeSpring.To(0); }, TimeSpan.FromMilliseconds(700));
    }

    private double[] Bands()
    {
        try { return L.Settings.String(K.SeekBands).Split(',').Select(double.Parse).ToArray(); }
        catch (FormatException) { return [2, 4, 8, 16, 32]; }
    }

    private void TriggerSeek(ActionEvent e)
    {
        var p = L.Player;
        var dir = e.Action == Act.SeekForward ? 1 : -1;
        if (e.Phase == ActionPhase.Released)
        {
            if (_seekDir != dir) return;
            EndTriggerSeek();
            return;
        }
        if (_seekMode != SeekMode.None && _seekDir != dir) EndTriggerSeek();
        var bands = Bands();
        var band = Math.Clamp((int)(e.Value * bands.Length), 0, bands.Length - 1);
        var speed = bands[band];
        _seekDir = dir;
        var wantFastPlay = dir > 0 && band == 0 && speed <= 2 && p.HasAudio;

        if (wantFastPlay)
        {
            if (_seekMode == SeekMode.Scrub) { p.EndScrub(_scrubPos); _seekMode = SeekMode.None; }
            if (_seekMode != SeekMode.FastPlay)
            {
                _baseSpeed = p.Speed;
                _seekMode = SeekMode.FastPlay;
                p.SetSpeed(speed);
                if (!p.IsPlaying) p.Play();
                L.Rumble(0.2f, 40);
            }
        }
        else
        {
            if (_seekMode == SeekMode.FastPlay) { p.SetSpeed(_baseSpeed); _seekMode = SeekMode.None; }
            if (_seekMode != SeekMode.Scrub)
            {
                _scrubPos = p.Position;
                p.BeginScrub();
                _seekMode = SeekMode.Scrub;
                _lastStripAt = -1;
                _stripSpring.To(1);
                L.Rumble(0.25f, 40);
            }
        }
        if (Math.Abs(speed - _seekSpeed) > 0.01 && _seekMode == SeekMode.Scrub) L.Rumble(0.15f, 25);
        _seekSpeed = speed;
        Badge((dir > 0 ? "â–¶â–¶  " : "â—€â—€  ") + $"{speed:0}Ã—", stay: true);
    }

    private void EndTriggerSeek()
    {
        var p = L.Player;
        if (_seekMode == SeekMode.FastPlay) p.SetSpeed(_baseSpeed);
        else if (_seekMode == SeekMode.Scrub) p.EndScrub(_scrubPos);
        _seekMode = SeekMode.None;
        _stripSpring.To(0);
        _badgeSpring.To(0);
        _strip.Children.Clear();
    }

    private void ToggleSubtitles()
    {
        var p = L.Player;
        if (p.Subtitles.HasTrack)
        {
            _lastSubStream = p.SubtitleStream;
            p.SelectSubtitle(-1);
            L.Window.Toast("Subtitles off", "subtitles", 1.6);
        }
        else
        {
            var pick = _lastSubStream >= 0 ? _lastSubStream : p.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Subtitle)?.StreamIndex ?? -1;
            if (pick < 0) { L.Window.Toast("This video has no subtitles. Load a file from the Y menu.", "subtitles"); return; }
            p.SelectSubtitle(pick);
            L.Window.Toast("Subtitles on", "subtitles", 1.6);
        }
    }

    // ------------------------------------------------------------------ panels

    private void OpenTracks()
    {
        var p = L.Player;
        var audio = p.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        var subs = p.Tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();
        string AudioName() => audio.FirstOrDefault(t => t.StreamIndex == p.AudioStream)?.Label ?? "None";
        string SubName() => p.SubtitleStream == -2 ? p.Subtitles.ExternalName ?? "File" :
            subs.FirstOrDefault(t => t.StreamIndex == p.SubtitleStream)?.Label ?? "Off";
        void CycleAudio(int d)
        {
            if (audio.Count < 2) return;
            var i = audio.FindIndex(t => t.StreamIndex == p.AudioStream);
            p.SelectAudio(audio[(i + d + audio.Count) % audio.Count].StreamIndex);
        }
        void CycleSubs(int d)
        {
            var options = new List<int> { -1 };
            options.AddRange(subs.Select(s => s.StreamIndex));
            var i = options.IndexOf(p.SubtitleStream);
            var next = options[((i < 0 ? 0 : i) + d + options.Count) % options.Count];
            p.SelectSubtitle(next);
        }
        void AdjustPicture(string key, int d)
        {
            L.Settings.Step(key, d);
            _surface.Adjust = Adjust();
        }
        string Show(string key) => SettingsSchema.Get(key).ShowValue(L.Settings.Get(key));
        SidePanel? panel = null;
        panel = new SidePanel("Audio & subtitles",
        [
            new PanelRow { Header = "Sound", Label = "Audio track", Icon = "audio", Value = AudioName, Step = CycleAudio,
                Activate = () => ChooseTrack("Audio track", audio, p.AudioStream, s => { p.SelectAudio(s); panel!.Refresh(); }) },
            new PanelRow { Header = "Subtitles", Label = "Subtitles", Icon = "subtitles", Value = SubName, Step = CycleSubs,
                Activate = () => ChooseSubtitles(subs, () => panel!.Refresh()) },
            new PanelRow { Label = "Subtitle timing", Icon = "clock", Value = () => p.Subtitles.DelayMs == 0 ? "In sync" : $"{(p.Subtitles.DelayMs > 0 ? "+" : "")}{p.Subtitles.DelayMs} ms",
                Step = d => p.Subtitles.DelayMs = Math.Clamp(p.Subtitles.DelayMs + d * 100, -600_000, 600_000) },
            new PanelRow { Label = "Subtitle size", Icon = "subtitles", Value = () => Show(K.SubFontScale), Step = d => L.Settings.Step(K.SubFontScale, d) },
            new PanelRow { Header = "Playback", Label = "Speed", Icon = "speed", Value = () => $"{p.Speed:0.##}Ã—", Step = d => p.SetSpeed(p.Speed + d * 0.25) },
            new PanelRow { Label = "Keep pitch", Icon = "audio", Value = () => L.Settings.Bool(K.PitchCorrect) ? "On" : "Off",
                Step = _ => { L.Settings.Step(K.PitchCorrect, 1); p.SetPitchCorrect(L.Settings.Bool(K.PitchCorrect)); } },
            new PanelRow { Header = "Picture", Label = "Brightness", Icon = "sun", Value = () => Show(K.Brightness), Step = d => AdjustPicture(K.Brightness, d) },
            new PanelRow { Label = "Contrast", Icon = "sun", Value = () => Show(K.Contrast), Step = d => AdjustPicture(K.Contrast, d) },
            new PanelRow { Label = "Saturation", Icon = "sun", Value = () => Show(K.Saturation), Step = d => AdjustPicture(K.Saturation, d) },
            new PanelRow { Label = "Hue", Icon = "sun", Value = () => Show(K.Hue), Step = d => AdjustPicture(K.Hue, d) },
            new PanelRow { Label = "Gamma", Icon = "sun", Value = () => Show(K.Gamma), Step = d => AdjustPicture(K.Gamma, d) },
            new PanelRow { Label = "Reset picture", Icon = "refresh", Value = () => "",
                Activate = () => { foreach (var k in new[] { K.Brightness, K.Contrast, K.Saturation, K.Hue, K.Gamma }) L.Settings.Reset(k); _surface.Adjust = Adjust(); panel!.Refresh(); } },
        ]);
        L.Window.AddOverlay(panel);
    }

    private static void ChooseTrack(string title, List<TrackInfo> tracks, int current, Action<int> pick) =>
        L.Window.Menu(title, tracks.Select(t => new MenuEntry(t.Label, "audio", () => pick(t.StreamIndex), Checked: t.StreamIndex == current)).ToList());

    private void ChooseSubtitles(List<TrackInfo> subs, Action refresh)
    {
        var p = L.Player;
        var items = new List<MenuEntry> { new("Off", "close", () => { p.SelectSubtitle(-1); refresh(); }, Checked: !p.Subtitles.HasTrack) };
        items.AddRange(subs.Select(t => new MenuEntry(t.Label, "subtitles", () => { p.SelectSubtitle(t.StreamIndex); refresh(); }, Checked: t.StreamIndex == p.SubtitleStream)));
        items.Add(new MenuEntry("Load a subtitle file...", "folder", () =>
        {
            L.Window.CloseOverlays();
            L.Window.Push(new FilesScreen(Path.GetDirectoryName(_path), new FilePick(FilePickKind.Subtitle, "Choose subtitles", f =>
            {
                if (p.LoadSubtitleFile(f)) L.Window.Toast($"Subtitles: {Path.GetFileName(f)}", "subtitles");
                else L.Window.Toast("Those subtitles couldn't be read.", "warning");
            })));
        }));
        L.Window.Menu("Subtitles", items);
    }

    private void Options()
    {
        var p = L.Player;
        var items = new List<MenuEntry>
        {
            new(p.IsPlaying ? "Pause" : "Play", p.IsPlaying ? "pause" : "play", () => p.TogglePause()),
            new("Audio, subtitles and picture", "subtitles", OpenTracks),
        };
        if (p.Chapters.Count > 1)
            items.Add(new MenuEntry("Chapters", "chapters", () => L.Window.Menu("Chapters",
                p.Chapters.Select(c => new MenuEntry(c.Title, "chapters", () => p.Seek(c.Start), Ui.Time(c.Start))).ToList())));
        if (L.Queue.Items.Count > 1)
            items.Add(new MenuEntry("Up next", "list", () => L.Window.Menu("Queue", L.Queue.Items.Select((q, i) => new MenuEntry(
                LibraryScanner.CleanTitle(Path.GetFileNameWithoutExtension(q)), i == L.Queue.Index ? "play" : "video",
                () => { L.Queue.Jump(i); Load(q, resume: null); }, Checked: i == L.Queue.Index)).ToList())));
        items.Add(new MenuEntry("Play from the start", "refresh", () => p.Seek(0)));
        if (FindTivi() is { } tivi)
            items.Add(new MenuEntry("Open in tivi", "external", () =>
            {
                var at = p.Position;
                p.Pause();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tivi, $"\"{_path}\"") { UseShellExecute = true });
                _ = at;
            }));
        items.Add(new MenuEntry("Stop", "stop", Stop, Danger: true));
        L.Window.Menu(_title.Text ?? "Playing", items);
    }

    private void UpdateInfo()
    {
        var p = L.Player;
        var v = p.Tracks.FirstOrDefault(t => t.StreamIndex == p.VideoStream);
        var a = p.Tracks.FirstOrDefault(t => t.StreamIndex == p.AudioStream);
        _infoText.Text =
            $"{Path.GetFileName(_path)}\n" +
            (v is null ? "" : $"Video   {v.Width}Ã—{v.Height} {v.Codec.ToUpperInvariant()} Â· {(p.HardwareDecoder ?? "software decoding")} Â· {(_surface is GlVideoSurface ? "GPU colour" : "CPU colour")}\n") +
            (a is null ? "" : $"Audio   {a.Label}\n") +
            $"Speed   {p.Speed:0.##}Ã—   Â·   {Ui.Time(p.Position)} / {Ui.Time(p.Duration)}\n" +
            $"Folder  {Path.GetDirectoryName(_path)}";
    }

    // ------------------------------------------------------------------ state

    private void OnState(PlayerState s) => Dispatcher.UIThread.Post(() =>
    {
        if (!_alive) return;
        if (s == PlayerState.Ended && !_endHandled)
        {
            _endHandled = true;
            Playback.RememberCurrent(reachedEnd: true);
            if (L.Settings.Bool(K.AutoQueue) && L.Queue.PeekNext() is not null) Playback.PlayNext();
            else DispatcherTimer.RunOnce(() => { if (_alive && L.Player.State == PlayerState.Ended) Stop(); }, TimeSpan.FromSeconds(2));
        }
        if (s == PlayerState.Paused) ShowOsd();
        KeepAwake(s == PlayerState.Playing);
    });

    private void OnTracks() => Dispatcher.UIThread.Post(() => { if (_alive) BuildChapterTicks(); });

    public void Stop()
    {
        if (!_alive) return;
        Playback.RememberCurrent();
        L.Window.Remove(this);
    }

    public override void OnClosed()
    {
        if (!_alive) return;
        _alive = false;
        _glWatchdog?.Dispose();
        L.Player.StateChanged -= OnState;
        L.Player.TracksChanged -= OnTracks;
        Playback.RememberCurrent();
        L.Player.Close();
        _thumbs?.Dispose();
        _surface.Clear();
        KeepAwake(false);
        L.Window.UpdateAmbient();
    }

    private static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);

    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
