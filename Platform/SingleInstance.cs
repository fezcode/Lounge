using System.IO.Pipes;
using System.Text;

namespace Lounge.Platform;

/// <summary>One Lounge per user. A second launch (from the agent, a shortcut, or "open
/// with") hands its arguments to the running copy over a named pipe and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    /// <summary>Raised on a background thread with the second instance's arguments.</summary>
    public event Action<string[]>? Activated;

    public SingleInstance(string name)
    {
        var user = Environment.UserName.Replace('\\', '_');
        _mutex = new Mutex(true, $@"Local\{name}-{user}", out var created);
        IsFirst = created;
        _pipe = $"{name}-{user}-activate";
    }

    public void Listen()
    {
        if (!IsFirst) return;
        var t = new Thread(() =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = reader.ReadToEnd();
                    Activated?.Invoke(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { Thread.Sleep(200); }
            }
        }) { IsBackground = true, Name = "single-instance" };
        t.Start();
    }

    public static bool Forward(string name, string[] args)
    {
        var user = Environment.UserName.Replace('\\', '_');
        try
        {
            using var client = new NamedPipeClientStream(".", $"{name}-{user}-activate", PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(string.Join('\n', args.Length == 0 ? ["--activate"] : args));
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
