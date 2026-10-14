using NAudio.CoreAudioApi;

namespace Lounge.Platform;

/// <summary>The Windows master volume of the default output (what RB/LB change).</summary>
public sealed class SystemVolume : IDisposable
{
    private readonly MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;

    public SystemVolume()
    {
        try { _enumerator = new MMDeviceEnumerator(); }
        catch (Exception) { _enumerator = null; }
    }

    private AudioEndpointVolume? Endpoint
    {
        get
        {
            try
            {
                // The default device can change (headphones plugged in), so re-resolve each time.
                _device?.Dispose();
                _device = _enumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return _device?.AudioEndpointVolume;
            }
            catch (Exception) { return null; }
        }
    }

    public bool Available => Endpoint is not null;

    /// <summary>0..100, or -1 without an output device.</summary>
    public int Level
    {
        get => Endpoint is { } e ? (int)Math.Round(e.MasterVolumeLevelScalar * 100) : -1;
        set { if (Endpoint is { } e) { e.MasterVolumeLevelScalar = Math.Clamp(value, 0, 100) / 100f; if (value > 0) e.Mute = false; } }
    }

    public bool Muted
    {
        get => Endpoint?.Mute ?? false;
        set { if (Endpoint is { } e) e.Mute = value; }
    }

    public int Step(int delta)
    {
        var current = Level;
        if (current < 0) return -1;
        Level = current + delta;
        return Level;
    }

    public void Dispose()
    {
        _device?.Dispose();
        _enumerator?.Dispose();
    }
}
