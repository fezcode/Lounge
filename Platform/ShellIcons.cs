using System.Runtime.InteropServices;

namespace Lounge.Platform;

/// <summary>Large icons for executables, shortcuts and Store apps, straight from the
/// Windows shell (IShellItemImageFactory, up to 256 px), as BGRA pixels.</summary>
public static class ShellIcons
{
    public static (byte[] Bgra, int Width, int Height)? Get(string pathOrParsingName, int size = 256)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(pathOrParsingName, IntPtr.Zero, ref iid, out var factory);
            if (hr != 0 || factory is null) return null;
            try
            {
                hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbmp);
                if (hr != 0 || hbmp == IntPtr.Zero) return null;
                try { return FromHBitmap(hbmp); }
                finally { DeleteObject(hbmp); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { return null; }
    }

    private static (byte[], int, int)? FromHBitmap(IntPtr hbmp)
    {
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var bytes = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbmp, 0, (uint)h, bytes, ref info, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        // The shell returns premultiplied BGRA; icons without alpha come back with A=0.
        var anyAlpha = false;
        for (int i = 3; i < bytes.Length; i += 4) if (bytes[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
        return (bytes, w, h);
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    /// <summary>Microsoft Store apps from the shell's AppsFolder: (display name, AppUserModelID).
    /// Must run on an STA thread (the UI thread is one).</summary>
    public static IReadOnlyList<(string Name, string Aumid)> StoreApps()
    {
        var list = new List<(string, string)>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return list;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string path = item.Path;
                // Packaged apps are "Family!App"; desktop programs are paths or GUIDs.
                if (path.Contains('!') && !path.Contains('\\')) list.Add((name, path));
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        return list.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Start Menu shortcuts (user and all users), for the "Add app" picker.</summary>
    public static IReadOnlyList<(string Name, string Path)> StartMenuApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string)>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(lnk);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("readme", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(name)) list.Add((name, lnk));
            }
        }
        return list.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
