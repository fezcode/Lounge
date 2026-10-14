using System.Diagnostics;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Xunit;

namespace Lounge.Tests;

/// <summary>"Fully compatible with tivi": Lounge's config.ini and history.ini are read
/// and written by tivi's own C code (watch.c, viconfig.c from ../tivi), compiled into a
/// small harness with MSYS2 gcc. The harness runs with APPDATA pointed at a temp folder,
/// so tivi's real profile is never touched.</summary>
public class TiviCompatTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string? _harness;
    private readonly string _tiviData;

    public TiviCompatTests()
    {
        _tiviData = Path.Combine(_dir.Path, "fezcode", "tivi");
        Directory.CreateDirectory(_tiviData);
        _harness = Build(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string? Build(string outDir)
    {
        var gcc = new[] { @"D:\Apps\msys64\mingw64\bin\gcc.exe", @"C:\msys64\mingw64\bin\gcc.exe" }.FirstOrDefault(File.Exists);
        var tivi = Path.GetFullPath(Path.Combine(Root, "..", "tivi", "src"));
        if (gcc is null || !File.Exists(Path.Combine(tivi, "watch.c"))) return null;
        var exe = Path.Combine(outDir, "harness.exe");
        var psi = new ProcessStartInfo(gcc,
            $"-O1 -o \"{exe}\" \"{Path.Combine(Root, "tests", "tivi-compat", "harness.c")}\" \"{Path.Combine(tivi, "watch.c")}\" \"{Path.Combine(tivi, "viconfig.c")}\" -I \"{tivi}\"")
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        psi.Environment["PATH"] = Path.GetDirectoryName(gcc) + ";" + Environment.GetEnvironmentVariable("PATH");
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        if (p.ExitCode != 0) throw new InvalidOperationException("harness did not compile: " + err);
        return exe;
    }

    private string Run(params string[] args)
    {
        var psi = new ProcessStartInfo(_harness!) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["APPDATA"] = _dir.Path;
        psi.Environment["PATH"] = Path.GetDirectoryName(new[] { @"D:\Apps\msys64\mingw64\bin\gcc.exe", @"C:\msys64\mingw64\bin\gcc.exe" }.First(File.Exists)) + ";" + Environment.GetEnvironmentVariable("PATH");
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        Assert.Equal(0, p.ExitCode);
        return output.Replace("\r", "");
    }

    [Fact]
    public void Tivi_reads_the_history_Lounge_writes()
    {
        if (_harness is null) return;   // tivi or MSYS2 not on this machine
        var file = Path.Combine(_tiviData, "history.ini");
        var h = new WatchHistory(file, () => 1_700_000_000);
        h.Put(@"D:\Shows\Pipe|Name\S01E03.mkv", new WatchEntry(845.25, 2700, 250, 0));
        h.Put(@"D:\Films\Dune.mkv", new WatchEntry(60, 9000, 0, 0));
        h.Save();
        var lines = Run("read", @"d:/shows/pipe|name/s01e03.mkv", @"D:\Films\Dune.mkv", @"D:\Nope.mkv").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["845.250|2700.000|250", "60.000|9000.000|0", "missing"], lines);
    }

    [Fact]
    public void Lounge_reads_the_history_tivi_writes()
    {
        if (_harness is null) return;
        Run("write", @"E:\Anime\Ep 10.mkv");
        var h = new WatchHistory(Path.Combine(_tiviData, "history.ini"));
        Assert.True(h.TryGet(@"e:\anime\ep 10.mkv", out var e));
        Assert.Equal(123.5, e.Position, 3);
        Assert.Equal(4000, e.Duration, 3);
        Assert.Equal(-150, e.SubDelayMs);
        Assert.Equal(123.5, h.ResumePoint(@"E:\Anime\Ep 10.mkv"));
    }

    [Fact]
    public void Tivi_reads_the_config_Lounge_writes()
    {
        if (_harness is null) return;
        var s = new SettingsStore(Path.Combine(_dir.Path, "settings.json"), Path.Combine(_tiviData, "config.ini"));
        s.Set(K.PitchCorrect, false);
        s.Set(K.SubAutoLang, "de");
        s.Set(K.SubColor, "66d9ff");
        s.Set(K.PlayerVolume, 0.4);
        s.Set(K.HwDecode, false);
        s.Set(K.SubFont, "Georgia");
        var config = Run("config");
        Assert.Contains("pitch_correct=0", config);
        Assert.Contains("sub_auto_lang=de", config);
        Assert.Contains("sub_color=66d9ff", config);
        Assert.Contains("volume=0.40", config);
        Assert.Contains("hw_decode=0", config);
        Assert.Contains("sub_font=Georgia", config);
    }

    [Fact]
    public void Lounge_reads_the_config_tivi_writes()
    {
        if (_harness is null) return;
        Run("writeconfig");
        var s = new SettingsStore(Path.Combine(_dir.Path, "settings.json"), Path.Combine(_tiviData, "config.ini"));
        Assert.False(s.Bool(K.PitchCorrect));
        Assert.Equal("ffe066", s.String(K.SubColor));
        Assert.Equal("tr", s.String(K.SubAutoLang));
        Assert.Equal(0.5, s.Double(K.PlayerVolume), 3);
        Assert.True(s.Bool(K.HwDecode));
    }
}
