// A queue entry keeps its byte/item reservation until the consumer releases its lease.
// An oversized item may enter only when empty: a single spectrum is indivisible.
internal sealed class BoundedPipelineQueue<T> : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<(T Value, long Bytes)> entries = new();
    private readonly int maxItems;
    private readonly long maxBytes;
    private readonly CancellationToken token;
    private readonly CancellationTokenRegistration registration;
    private readonly Action<T>? disposeItem;
    private int outstanding;
    private long bytes;
    private bool complete;
    public long PeakBytes { get; private set; }

    public BoundedPipelineQueue(int maxItems, long maxBytes, CancellationToken token, Action<T>? disposeItem = null)
    {
        if (maxItems < 1 || maxBytes < 1) throw new ArgumentOutOfRangeException();
        this.maxItems = maxItems;
        this.maxBytes = maxBytes;
        this.token = token;
        this.disposeItem = disposeItem;
        registration = token.Register(() => { lock (gate) Monitor.PulseAll(gate); });
    }

    // Ownership transfers only when Add returns successfully.
    public void Add(T item, long itemBytes)
    {
        if (itemBytes < 0) throw new ArgumentOutOfRangeException(nameof(itemBytes));
        lock (gate)
        {
            while (!complete && (outstanding >= maxItems ||
                   (outstanding > 0 && itemBytes > maxBytes - bytes)))
            {
                token.ThrowIfCancellationRequested();
                Monitor.Wait(gate);
            }
            token.ThrowIfCancellationRequested();
            if (complete) throw new InvalidOperationException("Pipeline queue is complete.");
            entries.Enqueue((item, itemBytes));
            outstanding++;
            bytes += itemBytes;
            PeakBytes = Math.Max(PeakBytes, bytes);
            Monitor.PulseAll(gate);
        }
    }

    public Lease? Read()
    {
        lock (gate)
        {
            while (entries.Count == 0 && !complete)
            {
                token.ThrowIfCancellationRequested();
                Monitor.Wait(gate);
            }
            token.ThrowIfCancellationRequested();
            if (entries.Count == 0) return null;
            var entry = entries.Dequeue();
            return new Lease(this, entry.Value, entry.Bytes);
        }
    }

    public void Complete()
    {
        lock (gate) { complete = true; Monitor.PulseAll(gate); }
    }

    private void Release(T item, long itemBytes)
    {
        try { disposeItem?.Invoke(item); }
        finally
        {
            lock (gate)
            {
                bytes -= itemBytes;
                outstanding--;
                Monitor.PulseAll(gate);
            }
        }
    }

    // Called after all stages have stopped. Leases already read belong to consumers.
    public void Dispose()
    {
        Complete();
        while (entries.TryDequeue(out var entry)) Release(entry.Value, entry.Bytes);
        registration.Dispose();
    }

    internal sealed class Lease : IDisposable
    {
        private BoundedPipelineQueue<T>? owner;
        public T Value { get; }
        private readonly long bytes;
        internal Lease(BoundedPipelineQueue<T> owner, T value, long bytes)
        { this.owner = owner; Value = value; this.bytes = bytes; }
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(Value, bytes);
    }
}
