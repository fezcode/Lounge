using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Lounge.Media;

namespace Lounge.Controls;

public sealed record PictureAdjust(double Brightness, double Contrast, double Saturation, double Hue, double Gamma)
{
    public static readonly PictureAdjust Neutral = new(0, 1, 1, 0, 1);
}

/// <summary>Where decoded frames are drawn. Two implementations: the GPU path (YUV planes
/// uploaded as textures, converted to RGB in a shader, as tivi does) and the CPU path
/// (sws_scale to BGRA into a bitmap). The player screen falls back to the CPU path if
/// OpenGL is not available.</summary>
public interface IVideoSurface
{
    Control View { get; }
    bool Healthy { get; }
    void Present(VideoFrame frame);
    PictureAdjust Adjust { get; set; }
    void Clear();
}

/// <summary>The CPU fallback: frames arrive as BGRA and are copied into a bitmap.</summary>
public sealed class CpuVideoSurface : Image, IVideoSurface
{
    private WriteableBitmap? _bmp;

    public CpuVideoSurface()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.MediumQuality);
    }

    public Control View => this;
    public bool Healthy => true;
    public PictureAdjust Adjust { get; set; } = PictureAdjust.Neutral;

    public void Present(VideoFrame frame)
    {
        if (frame.Pixels != FramePixels.Bgra) return;
        if (_bmp is null || _bmp.PixelSize.Width != frame.Width || _bmp.PixelSize.Height != frame.Height)
        {
            _bmp = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            Source = _bmp;
        }
        using (var fb = _bmp.Lock())
        {
            unsafe
            {
                fixed (byte* src = frame.Planes[0])
                    for (int y = 0; y < frame.Height; y++)
                        Buffer.MemoryCopy(src + (long)y * frame.Strides[0], (byte*)fb.Address + (long)y * fb.RowBytes, fb.RowBytes, frame.Width * 4);
            }
        }
        InvalidateVisual();
    }

    public void Clear() { Source = null; _bmp = null; }
}

/// <summary>The GPU path. Planes are uploaded as single/dual-channel textures (NV12: Y +
/// interleaved UV; yuv420p: Y, U, V) and a fragment shader converts to RGB with the
/// BT.709 matrix for HD and BT.601 for SD, expanding limited range, then applies the
/// picture adjustments.</summary>
public sealed unsafe class GlVideoSurface : OpenGlControlBase, IVideoSurface
{
    private VideoFrame? _pending;
    private readonly object _gate = new();
    private Gl? _gl;
    private int _program, _vbo;
    private readonly int[] _tex = new int[3];
    private int _texW, _texH;
    private FramePixels _texFmt;
    private bool _hasFrame;
    private double _aspect = 16.0 / 9;
    private int _uScale, _uY, _uU, _uV, _uMode, _uBright, _uContrast, _uSat, _uHue, _uGamma, _uBt709, _uRange;
    private int _aPos;

    public Control View => this;
    public bool Healthy { get; private set; } = true;
    public bool Initialised { get; private set; }
    public string? Error { get; private set; }
    public PictureAdjust Adjust { get; set; } = PictureAdjust.Neutral;
    public int FramesDrawn { get; private set; }

    /// <summary>Self-check: the next rendered frame is read back as RGBA (bottom-up).</summary>
    public Action<byte[], int, int>? CaptureNext { get; set; }

    public void Present(VideoFrame frame)
    {
        lock (_gate)
        {
            _pending?.Dispose();
            _pending = frame;
        }
        RequestNextFrameRendering();
    }

    public void Clear()
    {
        lock (_gate) { _pending?.Dispose(); _pending = null; }
        _hasFrame = false;
        RequestNextFrameRendering();
    }

    private const string VertexSrc = """
        attribute vec2 aPos;
        uniform vec2 scale;
        varying vec2 vUv;
        void main() {
            vUv = vec2(aPos.x * 0.5 + 0.5, 0.5 - aPos.y * 0.5);
            gl_Position = vec4(aPos * scale, 0.0, 1.0);
        }
        """;

    private const string FragmentSrc = """
        precision mediump float;
        varying vec2 vUv;
        uniform sampler2D texY;
        uniform sampler2D texU;
        uniform sampler2D texV;
        uniform float mode;      // 0 nv12, 1 yuv420p
        uniform float bt709;
        uniform float fullRange;
        uniform float brightness;
        uniform float contrast;
        uniform float saturation;
        uniform float hue;
        uniform float gamma;
        void main() {
            float y = texture2D(texY, vUv).r;
            vec2 uv;
            if (mode < 0.5) { vec4 c = texture2D(texU, vUv); uv = vec2(c.r, c.a); }
            else uv = vec2(texture2D(texU, vUv).r, texture2D(texV, vUv).r);
            if (fullRange < 0.5) { y = (y - 0.0627451) * 1.1643836; uv = (uv - 0.5) * 1.1383929; }
            else uv = uv - 0.5;
            vec3 rgb;
            if (bt709 > 0.5) rgb = vec3(y + 1.5748 * uv.y, y - 0.1873 * uv.x - 0.4681 * uv.y, y + 1.8556 * uv.x);
            else rgb = vec3(y + 1.402 * uv.y, y - 0.344136 * uv.x - 0.714136 * uv.y, y + 1.772 * uv.x);
            rgb = (rgb - 0.5) * contrast + 0.5 + brightness;
            float l = dot(rgb, vec3(0.2126, 0.7152, 0.0722));
            rgb = mix(vec3(l), rgb, saturation);
            if (abs(hue) > 0.001) {
                float a = radians(hue);
                float cs = cos(a), sn = sin(a);
                vec3 k = vec3(0.57735);
                rgb = rgb * cs + cross(k, rgb) * sn + k * dot(k, rgb) * (1.0 - cs);
            }
            rgb = pow(clamp(rgb, 0.0, 1.0), vec3(1.0 / gamma));
            gl_FragColor = vec4(rgb, 1.0);
        }
        """;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = new Gl(gl);
            var g = _gl;
            var vs = Compile(g, Gl.VERTEX_SHADER, VertexSrc);
            var fs = Compile(g, Gl.FRAGMENT_SHADER, FragmentSrc);
            _program = g.CreateProgram();
            g.AttachShader(_program, vs);
            g.AttachShader(_program, fs);
            g.LinkProgram(_program);
            int ok;
            g.GetProgramiv(_program, Gl.LINK_STATUS, &ok);
            if (ok == 0) throw new InvalidOperationException("video shader did not link");
            _aPos = g.GetAttribLocation(_program, "aPos");
            _uScale = Uniform("scale"); _uY = Uniform("texY"); _uU = Uniform("texU"); _uV = Uniform("texV"); _uMode = Uniform("mode");
            _uBright = Uniform("brightness"); _uContrast = Uniform("contrast"); _uSat = Uniform("saturation");
            _uHue = Uniform("hue"); _uGamma = Uniform("gamma"); _uBt709 = Uniform("bt709"); _uRange = Uniform("fullRange");

            int vbo;
            g.GenBuffers(1, &vbo);
            _vbo = vbo;
            fixed (int* t = _tex) g.GenTextures(3, t);
            Initialised = true;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Healthy = false;
        }

        int Uniform(string name) => _gl!.GetUniformLocation(_program, name);
    }

    private static int Compile(Gl g, int type, string src)
    {
        var shader = g.CreateShader(type);
        var bytes = System.Text.Encoding.UTF8.GetBytes(src + "\0");
        fixed (byte* p = bytes)
        {
            var ptr = (IntPtr)p;
            g.ShaderSource(shader, 1, &ptr, null);
        }
        g.CompileShader(shader);
        int ok;
        g.GetShaderiv(shader, Gl.COMPILE_STATUS, &ok);
        if (ok == 0)
        {
            var log = new byte[2048];
            int len;
            fixed (byte* l = log) g.GetShaderInfoLog(shader, log.Length, &len, l);
            throw new InvalidOperationException("shader: " + System.Text.Encoding.UTF8.GetString(log, 0, Math.Max(0, len)));
        }
        return shader;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl is null) return;
        fixed (int* t = _tex) _gl.DeleteTextures(3, t);
        int vbo = _vbo;
        _gl.DeleteBuffers(1, &vbo);
        _gl.DeleteProgram(_program);
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        var g = _gl;
        if (g is null) return;
        VideoFrame? frame;
        lock (_gate) { frame = _pending; _pending = null; }
        if (frame is not null)
        {
            try { Upload(g, frame); }
            finally { frame.Dispose(); }
        }

        var scaling = VisualRoot?.RenderScaling ?? 1;
        int w = (int)(Bounds.Width * scaling), h = (int)(Bounds.Height * scaling);
        g.BindFramebuffer(Gl.FRAMEBUFFER, fb);
        g.Viewport(0, 0, w, h);
        g.ClearColor(0, 0, 0, 1);
        g.Clear(Gl.COLOR_BUFFER_BIT);
        if (!_hasFrame || w <= 0 || h <= 0) return;

        // Letterbox inside the control in clip space.
        var viewAspect = w / (double)h;
        float sx = 1, sy = 1;
        if (_aspect > viewAspect) sy = (float)(viewAspect / _aspect); else sx = (float)(_aspect / viewAspect);
        float* quad = stackalloc float[] { -1, -1, 1, -1, -1, 1, 1, 1 };
        g.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
        g.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(8 * sizeof(float)), (IntPtr)quad, Gl.DYNAMIC_DRAW);

        g.UseProgram(_program);
        g.EnableVertexAttribArray(_aPos);
        g.VertexAttribPointer(_aPos, 2, Gl.FLOAT, 0, 0, IntPtr.Zero);
        for (int i = 0; i < 3; i++)
        {
            g.ActiveTexture(Gl.TEXTURE0 + i);
            g.BindTexture(Gl.TEXTURE_2D, _tex[i]);
        }
        g.Uniform2f(_uScale, sx, sy);
        g.Uniform1i(_uY, 0); g.Uniform1i(_uU, 1); g.Uniform1i(_uV, 2);
        g.Uniform1f(_uMode, _texFmt == FramePixels.Nv12 ? 0 : 1);
        g.Uniform1f(_uBt709, _texH >= 720 ? 1 : 0);
        g.Uniform1f(_uRange, 0);
        var a = Adjust;
        g.Uniform1f(_uBright, (float)a.Brightness);
        g.Uniform1f(_uContrast, (float)a.Contrast);
        g.Uniform1f(_uSat, (float)a.Saturation);
        g.Uniform1f(_uHue, (float)a.Hue);
        g.Uniform1f(_uGamma, (float)Math.Max(0.1, a.Gamma));
        g.DrawArrays(Gl.TRIANGLE_STRIP, 0, 4);
        g.ActiveTexture(Gl.TEXTURE0);
        FramesDrawn++;
        if (CaptureNext is { } capture)
        {
            CaptureNext = null;
            var pixels = new byte[w * h * 4];
            fixed (byte* px = pixels) g.ReadPixels(0, 0, w, h, Gl.RGBA, Gl.UNSIGNED_BYTE, (IntPtr)px);
            capture(pixels, w, h);
        }
    }

    private void Upload(Gl g, VideoFrame f)
    {
        if (f.Pixels == FramePixels.Bgra) return;
        var realloc = f.Width != _texW || f.Height != _texH || f.Pixels != _texFmt;
        _texW = f.Width; _texH = f.Height; _texFmt = f.Pixels;
        _aspect = f.DisplayAspect;
        g.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        int cw = (f.Width + 1) / 2, ch = (f.Height + 1) / 2;
        Plane(g, 0, Gl.LUMINANCE, f.Width, f.Height, f.Planes[0], realloc);
        if (f.Pixels == FramePixels.Nv12)
            Plane(g, 1, Gl.LUMINANCE_ALPHA, f.Strides[1] / 2, ch, f.Planes[1], realloc);
        else
        {
            Plane(g, 1, Gl.LUMINANCE, cw, ch, f.Planes[1], realloc);
            Plane(g, 2, Gl.LUMINANCE, cw, ch, f.Planes[2], realloc);
        }
        _hasFrame = true;
    }

    private void Plane(Gl g, int index, int format, int w, int h, byte[] data, bool realloc)
    {
        g.BindTexture(Gl.TEXTURE_2D, _tex[index]);
        fixed (byte* p = data)
        {
            if (realloc)
            {
                g.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
                g.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
                g.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
                g.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
                g.TexImage2D(Gl.TEXTURE_2D, 0, format, w, h, 0, format, Gl.UNSIGNED_BYTE, (IntPtr)p);
            }
            else g.TexSubImage2D(Gl.TEXTURE_2D, 0, 0, 0, w, h, format, Gl.UNSIGNED_BYTE, (IntPtr)p);
        }
    }

    /// <summary>The GL ES 2 entry points the surface needs, resolved through Avalonia's
    /// context so they work on ANGLE.</summary>
    private sealed class Gl
    {
        public const int VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
        public const int ARRAY_BUFFER = 0x8892, DYNAMIC_DRAW = 0x88E8, FLOAT = 0x1406, TRIANGLE_STRIP = 0x0005;
        public const int TEXTURE_2D = 0x0DE1, TEXTURE0 = 0x84C0, TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800;
        public const int TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803, CLAMP_TO_EDGE = 0x812F, LINEAR = 0x2601;
        public const int LUMINANCE = 0x1909, LUMINANCE_ALPHA = 0x190A, UNSIGNED_BYTE = 0x1401, UNPACK_ALIGNMENT = 0x0CF5;
        public const int FRAMEBUFFER = 0x8D40, COLOR_BUFFER_BIT = 0x4000, RGBA = 0x1908;

        private readonly delegate* unmanaged[Stdcall]<int, int> _createShader;
        private readonly delegate* unmanaged[Stdcall]<int, int, IntPtr*, int*, void> _shaderSource;
        private readonly delegate* unmanaged[Stdcall]<int, void> _compileShader;
        private readonly delegate* unmanaged[Stdcall]<int, int, int*, void> _getShaderiv;
        private readonly delegate* unmanaged[Stdcall]<int, int, int*, byte*, void> _getShaderInfoLog;
        private readonly delegate* unmanaged[Stdcall]<int> _createProgram;
        private readonly delegate* unmanaged[Stdcall]<int, int, void> _attachShader;
        private readonly delegate* unmanaged[Stdcall]<int, void> _linkProgram;
        private readonly delegate* unmanaged[Stdcall]<int, int, int*, void> _getProgramiv;
        private readonly delegate* unmanaged[Stdcall]<int, void> _useProgram;
        private readonly delegate* unmanaged[Stdcall]<int, void> _deleteProgram;
        private readonly delegate* unmanaged[Stdcall]<int, byte*, int> _getUniformLocation;
        private readonly delegate* unmanaged[Stdcall]<int, byte*, int> _getAttribLocation;
        private readonly delegate* unmanaged[Stdcall]<int, int, void> _uniform1i;
        private readonly delegate* unmanaged[Stdcall]<int, float, void> _uniform1f;
        private readonly delegate* unmanaged[Stdcall]<int, float, float, void> _uniform2f;
        private readonly delegate* unmanaged[Stdcall]<int, int*, void> _genBuffers;
        private readonly delegate* unmanaged[Stdcall]<int, int*, void> _deleteBuffers;
        private readonly delegate* unmanaged[Stdcall]<int, int, void> _bindBuffer;
        private readonly delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, int, void> _bufferData;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, int, int, IntPtr, void> _vertexAttribPointer;
        private readonly delegate* unmanaged[Stdcall]<int, void> _enableVertexAttribArray;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, void> _drawArrays;
        private readonly delegate* unmanaged[Stdcall]<int, int*, void> _genTextures;
        private readonly delegate* unmanaged[Stdcall]<int, int*, void> _deleteTextures;
        private readonly delegate* unmanaged[Stdcall]<int, int, void> _bindTexture;
        private readonly delegate* unmanaged[Stdcall]<int, void> _activeTexture;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, void> _texParameteri;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, IntPtr, void> _texImage2D;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, IntPtr, void> _texSubImage2D;
        private readonly delegate* unmanaged[Stdcall]<int, int, void> _pixelStorei;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, int, void> _viewport;
        private readonly delegate* unmanaged[Stdcall]<float, float, float, float, void> _clearColor;
        private readonly delegate* unmanaged[Stdcall]<int, void> _clear;
        private readonly delegate* unmanaged[Stdcall]<int, int, void> _bindFramebuffer;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, int, int, int, IntPtr, void> _readPixels;

        public Gl(GlInterface gl)
        {
            IntPtr P(string n)
            {
                var p = gl.GetProcAddress(n);
                if (p == IntPtr.Zero) throw new EntryPointNotFoundException(n);
                return p;
            }
            _createShader = (delegate* unmanaged[Stdcall]<int, int>)P("glCreateShader");
            _shaderSource = (delegate* unmanaged[Stdcall]<int, int, IntPtr*, int*, void>)P("glShaderSource");
            _compileShader = (delegate* unmanaged[Stdcall]<int, void>)P("glCompileShader");
            _getShaderiv = (delegate* unmanaged[Stdcall]<int, int, int*, void>)P("glGetShaderiv");
            _getShaderInfoLog = (delegate* unmanaged[Stdcall]<int, int, int*, byte*, void>)P("glGetShaderInfoLog");
            _createProgram = (delegate* unmanaged[Stdcall]<int>)P("glCreateProgram");
            _attachShader = (delegate* unmanaged[Stdcall]<int, int, void>)P("glAttachShader");
            _linkProgram = (delegate* unmanaged[Stdcall]<int, void>)P("glLinkProgram");
            _getProgramiv = (delegate* unmanaged[Stdcall]<int, int, int*, void>)P("glGetProgramiv");
            _useProgram = (delegate* unmanaged[Stdcall]<int, void>)P("glUseProgram");
            _deleteProgram = (delegate* unmanaged[Stdcall]<int, void>)P("glDeleteProgram");
            _getUniformLocation = (delegate* unmanaged[Stdcall]<int, byte*, int>)P("glGetUniformLocation");
            _getAttribLocation = (delegate* unmanaged[Stdcall]<int, byte*, int>)P("glGetAttribLocation");
            _uniform1i = (delegate* unmanaged[Stdcall]<int, int, void>)P("glUniform1i");
            _uniform1f = (delegate* unmanaged[Stdcall]<int, float, void>)P("glUniform1f");
            _uniform2f = (delegate* unmanaged[Stdcall]<int, float, float, void>)P("glUniform2f");
            _genBuffers = (delegate* unmanaged[Stdcall]<int, int*, void>)P("glGenBuffers");
            _deleteBuffers = (delegate* unmanaged[Stdcall]<int, int*, void>)P("glDeleteBuffers");
            _bindBuffer = (delegate* unmanaged[Stdcall]<int, int, void>)P("glBindBuffer");
            _bufferData = (delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, int, void>)P("glBufferData");
            _vertexAttribPointer = (delegate* unmanaged[Stdcall]<int, int, int, int, int, IntPtr, void>)P("glVertexAttribPointer");
            _enableVertexAttribArray = (delegate* unmanaged[Stdcall]<int, void>)P("glEnableVertexAttribArray");
            _drawArrays = (delegate* unmanaged[Stdcall]<int, int, int, void>)P("glDrawArrays");
            _genTextures = (delegate* unmanaged[Stdcall]<int, int*, void>)P("glGenTextures");
            _deleteTextures = (delegate* unmanaged[Stdcall]<int, int*, void>)P("glDeleteTextures");
            _bindTexture = (delegate* unmanaged[Stdcall]<int, int, void>)P("glBindTexture");
            _activeTexture = (delegate* unmanaged[Stdcall]<int, void>)P("glActiveTexture");
            _texParameteri = (delegate* unmanaged[Stdcall]<int, int, int, void>)P("glTexParameteri");
            _texImage2D = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, IntPtr, void>)P("glTexImage2D");
            _texSubImage2D = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, IntPtr, void>)P("glTexSubImage2D");
            _pixelStorei = (delegate* unmanaged[Stdcall]<int, int, void>)P("glPixelStorei");
            _viewport = (delegate* unmanaged[Stdcall]<int, int, int, int, void>)P("glViewport");
            _clearColor = (delegate* unmanaged[Stdcall]<float, float, float, float, void>)P("glClearColor");
            _clear = (delegate* unmanaged[Stdcall]<int, void>)P("glClear");
            _bindFramebuffer = (delegate* unmanaged[Stdcall]<int, int, void>)P("glBindFramebuffer");
            _readPixels = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, IntPtr, void>)P("glReadPixels");
        }

        public int CreateShader(int t) => _createShader(t);
        public void ShaderSource(int s, int n, IntPtr* src, int* len) => _shaderSource(s, n, src, len);
        public void CompileShader(int s) => _compileShader(s);
        public void GetShaderiv(int s, int p, int* v) => _getShaderiv(s, p, v);
        public void GetShaderInfoLog(int s, int max, int* len, byte* log) => _getShaderInfoLog(s, max, len, log);
        public int CreateProgram() => _createProgram();
        public void AttachShader(int p, int s) => _attachShader(p, s);
        public void LinkProgram(int p) => _linkProgram(p);
        public void GetProgramiv(int p, int n, int* v) => _getProgramiv(p, n, v);
        public void UseProgram(int p) => _useProgram(p);
        public void DeleteProgram(int p) => _deleteProgram(p);
        public int GetUniformLocation(int p, string n) { var b = System.Text.Encoding.ASCII.GetBytes(n + "\0"); fixed (byte* x = b) return _getUniformLocation(p, x); }
        public int GetAttribLocation(int p, string n) { var b = System.Text.Encoding.ASCII.GetBytes(n + "\0"); fixed (byte* x = b) return _getAttribLocation(p, x); }
        public void Uniform1i(int l, int v) => _uniform1i(l, v);
        public void Uniform1f(int l, float v) => _uniform1f(l, v);
        public void Uniform2f(int l, float x, float y) => _uniform2f(l, x, y);
        public void GenBuffers(int n, int* b) => _genBuffers(n, b);
        public void DeleteBuffers(int n, int* b) => _deleteBuffers(n, b);
        public void BindBuffer(int t, int b) => _bindBuffer(t, b);
        public void BufferData(int t, IntPtr size, IntPtr data, int usage) => _bufferData(t, size, data, usage);
        public void VertexAttribPointer(int i, int size, int type, int norm, int stride, IntPtr ptr) => _vertexAttribPointer(i, size, type, norm, stride, ptr);
        public void EnableVertexAttribArray(int i) => _enableVertexAttribArray(i);
        public void DrawArrays(int mode, int first, int count) => _drawArrays(mode, first, count);
        public void GenTextures(int n, int* t) => _genTextures(n, t);
        public void DeleteTextures(int n, int* t) => _deleteTextures(n, t);
        public void BindTexture(int target, int t) => _bindTexture(target, t);
        public void ActiveTexture(int t) => _activeTexture(t);
        public void TexParameteri(int target, int p, int v) => _texParameteri(target, p, v);
        public void TexImage2D(int target, int level, int ifmt, int w, int h, int border, int fmt, int type, IntPtr data) => _texImage2D(target, level, ifmt, w, h, border, fmt, type, data);
        public void TexSubImage2D(int target, int level, int x, int y, int w, int h, int fmt, int type, IntPtr data) => _texSubImage2D(target, level, x, y, w, h, fmt, type, data);
        public void PixelStorei(int p, int v) => _pixelStorei(p, v);
        public void Viewport(int x, int y, int w, int h) => _viewport(x, y, w, h);
        public void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
        public void Clear(int mask) => _clear(mask);
        public void BindFramebuffer(int t, int fb) => _bindFramebuffer(t, fb);
        public void ReadPixels(int x, int y, int w, int h, int fmt, int type, IntPtr data) => _readPixels(x, y, w, h, fmt, type, data);
    }
}
