using FFmpeg.AutoGen;

namespace Lounge.Media;

internal enum QueueItemKind { Packet, Flush, Eof }

internal readonly record struct QueueItem(QueueItemKind Kind, IntPtr Packet, int Serial);

/// <summary>A bounded hand-off of demuxed packets to one decoder thread. Items carry the
/// seek serial they were read under, so a decoder can drop everything stale at once.</summary>
internal sealed unsafe class PacketQueue
{
    private readonly Queue<QueueItem> _items = new();
    private readonly int _capacity;
    private bool _closed;

    public PacketQueue(int capacity) => _capacity = capacity;

    public int Count { get { lock (_items) return _items.Count; } }
    public bool Full { get { lock (_items) return _items.Count >= _capacity; } }

    public void Put(QueueItem item)
    {
        lock (_items)
        {
            _items.Enqueue(item);
            Monitor.PulseAll(_items);
        }
    }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for an item.</summary>
    public bool TryTake(out QueueItem item, int timeoutMs)
    {
        lock (_items)
        {
            if (_items.Count == 0 && !_closed) Monitor.Wait(_items, timeoutMs);
            if (_items.Count == 0) { item = default; return false; }
            item = _items.Dequeue();
            Monitor.PulseAll(_items);
            return true;
        }
    }

    public void Clear()
    {
        lock (_items)
        {
            while (_items.Count > 0) Free(_items.Dequeue());
            Monitor.PulseAll(_items);
        }
    }

    public void Close()
    {
        lock (_items) { _closed = true; Monitor.PulseAll(_items); }
        Clear();
    }

    public void Reopen()
    {
        lock (_items) _closed = false;
    }

    public static void Free(QueueItem item)
    {
        if (item.Packet == IntPtr.Zero) return;
        var p = (AVPacket*)item.Packet;
        ffmpeg.av_packet_free(&p);
    }
}
