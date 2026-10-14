using System.Runtime.InteropServices;

namespace Lounge.Media;

/// <summary>The handful of libass-9 calls tivi uses.</summary>
internal static unsafe class LibAss
{
    private const string Dll = "libass-9.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct AssImage
    {
        public int w, h, stride;
        public byte* bitmap;
        public uint color;       // 0xRRGGBBAA, AA = transparency
        public int dst_x, dst_y;
        public AssImage* next;
        public int type;
    }

    public const int FontProviderAutodetect = 1;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr ass_library_init();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_library_done(IntPtr lib);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_set_message_cb(IntPtr lib, IntPtr cb, IntPtr data);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr ass_renderer_init(IntPtr lib);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_renderer_done(IntPtr r);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_set_frame_size(IntPtr r, int w, int h);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_set_storage_size(IntPtr r, int w, int h);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_set_use_margins(IntPtr r, int use);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_set_margins(IntPtr r, int t, int b, int l, int rr);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void ass_set_fonts(IntPtr r, string? defaultFont, string? defaultFamily, int provider, string? config, int update);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr ass_new_track(IntPtr lib);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_free_track(IntPtr track);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_process_codec_private(IntPtr track, byte* data, int size);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_process_data(IntPtr track, byte* data, int size);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_process_chunk(IntPtr track, byte* data, int size, long timecode, long duration);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr ass_read_memory(IntPtr lib, byte* buf, nuint size, string? codepage);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern AssImage* ass_render_frame(IntPtr r, IntPtr track, long now, int* detectChange);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void ass_flush_events(IntPtr track);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MessageCallback(int level, IntPtr fmt, IntPtr args, IntPtr data);
}
