using System.Runtime.InteropServices;
using Lounge.Core.Media;

namespace Lounge.Core.Files;

public sealed record FileEntry(string Path, string Name, bool IsDirectory, long Size, DateTime Modified, MediaKind Kind, bool Hidden)
{
    public string Extension => IsDirectory ? "" : System.IO.Path.GetExtension(Name);
    public string Stem => IsDirectory ? Name : System.IO.Path.GetFileNameWithoutExtension(Name);
}

public sealed record Place(string Name, string Path, string Icon);

public enum ConflictChoice { Replace, KeepBoth, Skip, Cancel }

public readonly record struct FileProgress(int Done, int Total, string Current, long BytesDone, long BytesTotal);

public sealed record OpResult(bool Ok, string? Error = null, string? NewPath = null)
{
    public static OpResult Success(string? path = null) => new(true, null, path);
    public static OpResult Fail(string error) => new(false, error);
}

/// <summary>File operations the controller file explorer needs. Everything returns a
/// message for the user instead of throwing, because a toast is all the user sees.</summary>
public static class FileOps
{
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Null when <paramref name="name"/> is a valid Windows file name, else why not.</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A name can't be empty.";
        if (name.Length > 255) return "That name is too long.";
        var bad = name.FirstOrDefault(c => Invalid.Contains(c) || c < 32);
        if (bad != default) return bad < 32 ? "Names can't contain control characters." : $"Names can't contain {bad}";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "Names can't end with a dot or a space.";
        if (name is "." or "..") return "That name is reserved.";
        var stem = name.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem)) return $"{stem} is a name Windows reserves.";
        return null;
    }

    /// <summary>Puts the extension back when it is locked, so a rename can't break a file type.</summary>
    public static string Compose(string typed, string originalExtension, bool lockExtension) =>
        lockExtension && !string.IsNullOrEmpty(originalExtension) ? typed.TrimEnd() + originalExtension : typed.Trim();

    public static OpResult Rename(string path, string newName)
    {
        if (ValidateName(newName) is { } why) return OpResult.Fail(why);
        var isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) return OpResult.Fail("That item no longer exists.");
        var parent = Path.GetDirectoryName(path.TrimEnd('\\'))!;
        var target = Path.Combine(parent, newName);
        if (string.Equals(path, target, StringComparison.Ordinal)) return OpResult.Success(target);
        var caseOnly = string.Equals(path, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            return OpResult.Fail($"\"{newName}\" already exists here.");
        try
        {
            if (isDir)
            {
                if (caseOnly)
                {
                    var tmp = path + ".lounge-rename";
                    Directory.Move(path, tmp);
                    Directory.Move(tmp, target);
                }
                else Directory.Move(path, target);
            }
            else File.Move(path, target);
            return OpResult.Success(target);
        }
        catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or 33) { return OpResult.Fail("It's in use by another program."); }
        catch (IOException e) { return OpResult.Fail(e.Message); }
        catch (UnauthorizedAccessException) { return OpResult.Fail("Windows didn't allow that."); }
    }

    /// <summary>"New folder", "New folder (2)", ... whichever is free.</summary>
    public static string UniqueName(string parent, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (int i = 2; File.Exists(Path.Combine(parent, candidate)) || Directory.Exists(Path.Combine(parent, candidate)); i++)
            candidate = $"{stem} ({i}){ext}";
        return candidate;
    }

    public static OpResult NewFolder(string parent, string name)
    {
        if (ValidateName(name) is { } why) return OpResult.Fail(why);
        var target = Path.Combine(parent, name);
        if (Directory.Exists(target) || File.Exists(target)) return OpResult.Fail($"\"{name}\" already exists here.");
        try { Directory.CreateDirectory(target); return OpResult.Success(target); }
        catch (IOException e) { return OpResult.Fail(e.Message); }
        catch (UnauthorizedAccessException) { return OpResult.Fail("Windows didn't allow that."); }
    }

    /// <summary>Copies or moves files and folders into <paramref name="destination"/>.
    /// Conflicts are resolved by <paramref name="onConflict"/>; Cancel stops the whole
    /// operation. Reports progress per file.</summary>
    public static async Task<OpResult> TransferAsync(
        IReadOnlyList<string> sources, string destination, bool move,
        Func<string, Task<ConflictChoice>> onConflict,
        IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        var plan = new List<(string Src, string Dst, long Size)>();
        foreach (var s in sources)
        {
            var full = Path.GetFullPath(s);
            var name = Path.GetFileName(full.TrimEnd('\\'));
            var dst = Path.Combine(destination, name);
            if (Directory.Exists(full))
            {
                if (Path.GetFullPath(destination).TrimEnd('\\').StartsWith(full.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFullPath(destination).TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return OpResult.Fail("A folder can't go inside itself.");
                foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    plan.Add((f, Path.Combine(dst, Path.GetRelativePath(full, f)), new FileInfo(f).Length));
                if (!Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories).Any())
                    plan.Add((full, dst, -1));   // empty folder: just create it
            }
            else if (File.Exists(full)) plan.Add((full, dst, new FileInfo(full).Length));
        }

        long total = plan.Where(p => p.Size > 0).Sum(p => p.Size), done = 0;
        int count = 0;
        try
        {
            foreach (var (src, dstRaw, size) in plan)
            {
                ct.ThrowIfCancellationRequested();
                var dst = dstRaw;
                progress?.Report(new FileProgress(count, plan.Count, Path.GetFileName(src), done, total));
                if (size < 0) { Directory.CreateDirectory(dst); count++; continue; }
                if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                {
                    if (move) { count++; continue; }
                    dst = Path.Combine(Path.GetDirectoryName(dst)!, UniqueName(Path.GetDirectoryName(dst)!, Path.GetFileName(dst)));
                }
                else if (File.Exists(dst))
                {
                    switch (await onConflict(dst))
                    {
                        case ConflictChoice.Cancel: return OpResult.Fail("Stopped.");
                        case ConflictChoice.Skip: count++; done += size; continue;
                        case ConflictChoice.KeepBoth:
                            dst = Path.Combine(Path.GetDirectoryName(dst)!, UniqueName(Path.GetDirectoryName(dst)!, Path.GetFileName(dst)));
                            break;
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (move && string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dst), StringComparison.OrdinalIgnoreCase))
                    File.Move(src, dst, overwrite: true);
                else
                {
                    await CopyFileAsync(src, dst, b => progress?.Report(new FileProgress(count, plan.Count, Path.GetFileName(src), done + b, total)), ct);
                    if (move) File.Delete(src);
                }
                done += size; count++;
            }
            if (move)
                foreach (var s in sources.Where(Directory.Exists))
                    if (!Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories).Any())
                        Directory.Delete(s, true);
            progress?.Report(new FileProgress(plan.Count, plan.Count, "", total, total));
            return OpResult.Success();
        }
        catch (OperationCanceledException) { return OpResult.Fail("Stopped."); }
        catch (IOException e) { return OpResult.Fail(e.Message); }
        catch (UnauthorizedAccessException) { return OpResult.Fail("Windows didn't allow that."); }
    }

    private static async Task CopyFileAsync(string src, string dst, Action<long> onBytes, CancellationToken ct)
    {
        const int Buffer = 1 << 20;
        await using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, true);
        await using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, Buffer, true);
        var buf = new byte[Buffer];
        long copied = 0;
        int n;
        while ((n = await input.ReadAsync(buf, ct)) > 0)
        {
            await output.WriteAsync(buf.AsMemory(0, n), ct);
            copied += n;
            onBytes(copied);
        }
        File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
    }

    /// <summary>Sends to the Recycle Bin, or deletes outright when <paramref name="permanent"/>.</summary>
    public static OpResult Delete(IReadOnlyList<string> paths, bool permanent)
    {
        if (paths.Count == 0) return OpResult.Success();
        if (permanent)
        {
            try
            {
                foreach (var p in paths)
                {
                    if (Directory.Exists(p)) Directory.Delete(p, true);
                    else if (File.Exists(p)) File.Delete(p);
                }
                return OpResult.Success();
            }
            catch (IOException e) { return OpResult.Fail(e.Message); }
            catch (UnauthorizedAccessException) { return OpResult.Fail("Windows didn't allow that."); }
        }

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths.Select(Path.GetFullPath)) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var rc = SHFileOperation(ref op);
        if (rc != 0) return OpResult.Fail($"Couldn't move that to the Recycle Bin (code {rc}).");
        return op.fAnyOperationsAborted ? OpResult.Fail("Stopped.") : OpResult.Success();
    }

    public static IReadOnlyList<FileEntry> List(string folder, bool showHidden)
    {
        var dir = new DirectoryInfo(folder);
        var entries = new List<FileEntry>();
        foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            var hidden = (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
            if (hidden && !showHidden) continue;
            var isDir = (info.Attributes & FileAttributes.Directory) != 0;
            entries.Add(new FileEntry(info.FullName, info.Name, isDir, isDir ? 0 : ((FileInfo)info).Length,
                info.LastWriteTime, isDir ? MediaKind.Folder : MediaKinds.Of(info.Name), hidden));
        }
        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, NaturalComparer.Instance)
            .ToList();
    }

    public static IReadOnlyList<Place> Places()
    {
        var places = new List<Place>();
        void Known(string name, Environment.SpecialFolder f, string icon)
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) places.Add(new Place(name, p, icon));
        }
        Known("Home", Environment.SpecialFolder.UserProfile, "home");
        Known("Desktop", Environment.SpecialFolder.Desktop, "desktop");
        Known("Documents", Environment.SpecialFolder.MyDocuments, "document");
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) places.Add(new Place("Downloads", downloads, "download"));
        Known("Videos", Environment.SpecialFolder.MyVideos, "video");
        Known("Music", Environment.SpecialFolder.MyMusic, "music");
        Known("Pictures", Environment.SpecialFolder.MyPictures, "image");
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                places.Add(new Place($"{label} ({d.Name.TrimEnd('\\')})", d.RootDirectory.FullName,
                    d.DriveType == DriveType.Removable ? "usb" : d.DriveType == DriveType.Network ? "network" : "drive"));
            }
            catch (IOException) { }
        }
        return places;
    }

    public static string FormatSize(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
