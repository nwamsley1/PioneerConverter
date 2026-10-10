using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ThermoFisher.CommonCore.RawFileReader.Facade.Interfaces;
using ThermoFisher.CommonCore.RawFileReader.Readers;

internal sealed class CachedViewManager : IViewCollectionManager, IDisposable
{
    private readonly string path;
    private readonly ByteBlockCache cache;
    private readonly bool preferLargeReads;
    private bool disposed;
    private long viewers;

    public CachedViewManager(string path) : this(path,
        EnvironmentInt("PIONEER_RAW_CACHE_BLOCK_BYTES", 4 << 20),
        EnvironmentInt("PIONEER_RAW_CACHE_BUDGET_BYTES", 64 << 20),
        EnvironmentInt("PIONEER_RAW_CACHE_READ_AHEAD_BLOCKS", 1),
        EnvironmentBool("PIONEER_RAW_PREFER_LARGE_READS", true))
    { }

    internal CachedViewManager(string path, int blockBytes,
        int budgetBytes, int readAheadBlocks, bool preferLargeReads = true)
    {
        this.path = Path.GetFullPath(path);
        this.preferLargeReads = preferLargeReads;
        cache = new ByteBlockCache(this.path, blockBytes, budgetBytes, readAheadBlocks);
    }

    private static int EnvironmentInt(string name, int defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (!int.TryParse(value, out int parsed))
            throw new ArgumentException($"{name} must be an integer.");
        return parsed;
    }

    private static bool EnvironmentBool(string name, bool defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (!bool.TryParse(value, out bool parsed))
            throw new ArgumentException($"{name} must be true or false.");
        return parsed;
    }

    public bool WasUsed => Interlocked.Read(ref viewers) != 0;
    public Dictionary<string, string> ExtensionAttributes { get; } = new();
    public bool GetIgnorePlatformKeepNameCaseIntactFlag() => false;
    public bool IsOpen(string streamId) => !disposed;
    public string GetErrors(string streamId) => string.Empty;
    public void Close(string streamId, bool forceClose) { }

    public IReadWriteAccessor GetRandomAccessViewer(Guid id, string streamId,
        bool writable, DataFileAccessMode accessMode, PersistenceMode persistenceMode) =>
        Create(streamId, 0, cache.Length, writable);

    public IReadWriteAccessor GetRandomAccessViewer(Guid id, string streamId,
        long offset, long length, bool writable, DataFileAccessMode accessMode,
        PersistenceMode persistenceMode) => Create(streamId, offset, length, writable);

    private IReadWriteAccessor Create(string streamId, long offset, long length, bool writable)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (writable) throw new NotSupportedException("The Pioneer RAW cache is read-only.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool matches;
        try { matches = Path.GetFullPath(streamId).Equals(path, comparison); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { matches = false; }
        if (!matches) matches = streamId.EndsWith("__" + path, comparison);
        if (!matches)
            throw new IOException($"Delegated RAW reader requested an unexpected stream: {streamId}");
        if (offset < 0 || offset > cache.Length || length < 0 ||
            (length != 0 && length > cache.Length - offset))
            throw new ArgumentOutOfRangeException(nameof(offset), "Delegated RAW view exceeds the file bounds.");
        Interlocked.Increment(ref viewers);
        // The bundled mapped reader reports the full physical length even for
        // offset views. Accessor calls remain relative to the view offset.
        return new CachedReadAccessor(cache, streamId, offset, cache.Length, preferLargeReads);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cache.Dispose();
    }
}

internal sealed class ByteBlockCache : IDisposable
{
    private sealed class Entry
    {
        internal readonly long Index;
        internal readonly int Bytes;
        internal readonly Task<byte[]> Load;
        internal LinkedListNode<Entry>? Node;
        internal int References;
        internal bool Prefetched;
        internal Entry(long index, int bytes, Task<byte[]> load, bool prefetched)
        { Index = index; Bytes = bytes; Load = load; Prefetched = prefetched; }
    }

    private readonly object gate = new();
    private readonly FileStream file;
    private readonly SafeFileHandle handle;
    private readonly Dictionary<long, Entry> entries = new();
    private readonly LinkedList<Entry> lru = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly int blockBytes, budgetBytes, readAheadBlocks;
    private long reservedBytes, peakBytes, logicalReads, demandLoads, prefetchLoads;
    private long usefulPrefetches, fetchedBytes, evictions, reusedBuffers;
    private bool disposed;

    internal ByteBlockCache(string path, int blockBytes, int budgetBytes, int readAheadBlocks)
    {
        if (blockBytes < 4096 || budgetBytes < blockBytes || budgetBytes % blockBytes != 0)
            throw new ArgumentOutOfRangeException(nameof(blockBytes));
        if (readAheadBlocks < 0 || readAheadBlocks > 4)
            throw new ArgumentOutOfRangeException(nameof(readAheadBlocks));
        this.blockBytes = blockBytes;
        this.budgetBytes = budgetBytes;
        this.readAheadBlocks = readAheadBlocks;
        file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        handle = file.SafeFileHandle;
        Length = file.Length;
    }

    internal long Length { get; }
    internal int SuggestedChunkSize => blockBytes;

    private Entry Acquire(long index, out bool continueReadAhead)
    {
        lock (gate)
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (entries.TryGetValue(index, out var existing))
                {
                    existing.References++;
                    continueReadAhead = existing.Prefetched;
                    if (existing.Prefetched)
                    {
                        existing.Prefetched = false;
                        usefulPrefetches++;
                    }
                    Touch(existing);
                    return existing;
                }

                int bytes = checked((int)Math.Min(blockBytes, Length - index * blockBytes));
                if (bytes <= 0) throw new EndOfStreamException();
                if (MakeRoom(bytes, out byte[]? reusable))
                {
                    Task<byte[]> load = LoadAsync(index, bytes, reusable);
                    var entry = new Entry(index, bytes, load, prefetched: false) { References = 1 };
                    entry.Node = lru.AddLast(entry);
                    entries.Add(index, entry);
                    reservedBytes += bytes;
                    peakBytes = Math.Max(peakBytes, reservedBytes);
                    demandLoads++;
                    continueReadAhead = true;
                    return entry;
                }
                Monitor.Wait(gate, 100);
                cancellation.Token.ThrowIfCancellationRequested();
            }
        }
    }

    private void StartPrefetch(long index)
    {
        if (index < 0 || index * blockBytes >= Length) return;
        lock (gate)
        {
            if (disposed || entries.ContainsKey(index)) return;
            int bytes = checked((int)Math.Min(blockBytes, Length - index * blockBytes));
            if (!MakeRoom(bytes, out byte[]? reusable)) return;
            Task<byte[]> load = LoadAsync(index, bytes, reusable);
            var entry = new Entry(index, bytes, load, prefetched: true);
            entry.Node = lru.AddLast(entry);
            entries.Add(index, entry);
            reservedBytes += bytes;
            peakBytes = Math.Max(peakBytes, reservedBytes);
            prefetchLoads++;
        }
    }

    private bool MakeRoom(int bytes, out byte[]? reusable)
    {
        reusable = null;
        while (reservedBytes + bytes > budgetBytes)
        {
            LinkedListNode<Entry>? node = lru.First;
            while (node != null && (node.Value.References != 0 || !node.Value.Load.IsCompleted))
                node = node.Next;
            if (node == null) return false;
            var victim = node.Value;
            lru.Remove(node);
            entries.Remove(victim.Index);
            reservedBytes -= victim.Bytes;
            evictions++;
            // The victim has no readers and its load is complete. Transfer the
            // exact-size buffer to the replacement instead of allocating
            // another large object; it never exists outside the cache budget.
            if (reusable == null && victim.Bytes == bytes &&
                victim.Load.Status == TaskStatus.RanToCompletion)
            {
                byte[] candidate = victim.Load.GetAwaiter().GetResult();
                if (candidate.Length == bytes)
                {
                    reusable = candidate;
                    reusedBuffers++;
                }
            }
        }
        return true;
    }

    private void Touch(Entry entry)
    {
        if (entry.Node == null || entry.Node.List == null) return;
        lru.Remove(entry.Node);
        lru.AddLast(entry.Node);
    }

    private void Release(Entry entry)
    {
        lock (gate)
        {
            entry.References--;
            if (entry.References < 0) throw new InvalidOperationException("Cache block released twice.");
            if (entry.References == 0)
            {
                if (entry.Load.IsFaulted || entry.Load.IsCanceled)
                {
                    if (entry.Node?.List != null) lru.Remove(entry.Node);
                    if (entries.Remove(entry.Index)) reservedBytes -= entry.Bytes;
                }
                Monitor.PulseAll(gate);
            }
        }
    }

    private async Task<byte[]> LoadAsync(long index, int bytes, byte[]? reusable)
    {
        byte[] result = reusable ?? GC.AllocateUninitializedArray<byte>(bytes);
        int read = 0;
        while (read < bytes)
        {
            int count = await RandomAccess.ReadAsync(handle, result.AsMemory(read),
                checked(index * blockBytes + read), cancellation.Token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
        Interlocked.Add(ref fetchedBytes, bytes);
        return result;
    }

    internal void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset > Length - destination.Length)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"RAW cache read [{offset}, {offset + destination.Length}) exceeds [0, {Length}).");
        Interlocked.Increment(ref logicalReads);
        int written = 0;
        while (written < destination.Length)
        {
            long position = offset + written;
            long index = position / blockBytes;
            int within = (int)(position % blockBytes);
            Entry entry = Acquire(index, out bool continueReadAhead);
            try
            {
                // Keep a bounded asynchronous chain moving when demand consumes
                // a prefetched block. Previously the chain stopped there, so
                // every other sequential block became a synchronous demand read.
                if (continueReadAhead)
                    for (int ahead = 1; ahead <= readAheadBlocks; ahead++) StartPrefetch(index + ahead);
                byte[] data = entry.Load.GetAwaiter().GetResult();
                int count = Math.Min(destination.Length - written, data.Length - within);
                data.AsSpan(within, count).CopyTo(destination[written..]);
                written += count;
            }
            finally { Release(entry); }
        }
    }

    internal byte[] ReadBytes(long offset, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        byte[] result = GC.AllocateUninitializedArray<byte>(count);
        Read(offset, result);
        return result;
    }

    public string FormatStatistics() => FormattableString.Invariant(
        $"RAW_CACHE block_bytes={blockBytes} budget_bytes={budgetBytes} peak_bytes={peakBytes} logical_reads={logicalReads} demand_loads={demandLoads} prefetch_loads={prefetchLoads} useful_prefetches={usefulPrefetches} fetched_bytes={fetchedBytes} evictions={evictions} reused_buffers={reusedBuffers}");

    public void Dispose()
    {
        List<Task<byte[]>> loads;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel();
            loads = entries.Values.Select(e => e.Load).ToList();
            Monitor.PulseAll(gate);
        }
        try { Task.WaitAll(loads.Cast<Task>().ToArray()); }
        catch { /* Conversion already observes a failed demand; speculative cancellation is expected. */ }
        file.Dispose();
        cancellation.Dispose();
    }
}

internal sealed class CachedReadAccessor : IReadWriteAccessor
{
    private readonly ByteBlockCache cache;
    private bool disposed;
    internal CachedReadAccessor(ByteBlockCache cache, string streamId, long initialOffset,
        long length, bool preferLargeReads)
    { this.cache = cache; StreamId = streamId; InitialOffset = initialOffset; Length = length; PreferLargeReads = preferLargeReads; }

    public long InitialOffset { get; }
    public long Length { get; }
    public string StreamId { get; }
    public bool PreferLargeReads { get; }
    public bool SupportsSubViews => false;
    public int SuggestedChunkSize => cache.SuggestedChunkSize;
    private void Check() => ObjectDisposedException.ThrowIf(disposed, this);
    private long Physical(long offset) => checked(InitialOffset + offset);

    private byte[] Bytes(long offset, int count)
    {
        Check();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

        // MemoryMappedViewAccessor.ReadArray returns the requested-size array and
        // leaves its unread tail zeroed when a fixed-size SDK read crosses EOF.
        // Some RAW revisions rely on that behavior for their 1 MiB data window.
        byte[] result = new byte[count];
        FillBytes(offset, result);
        return result;
    }

    private void FillBytes(long offset, Span<byte> destination)
    {
        long physical = Physical(offset);
        if (physical > cache.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        int readable = checked((int)Math.Min((long)destination.Length, cache.Length - physical));
        if (readable > 0) cache.Read(physical, destination[..readable]);
        destination[readable..].Clear();
    }
    private void Scalar(long offset, Span<byte> destination) { Check(); cache.Read(Physical(offset), destination); }
    public byte ReadByte(long offset) { Span<byte> b = stackalloc byte[1]; Scalar(offset, b); return b[0]; }
    public short ReadShort(long offset) { Span<byte> b = stackalloc byte[2]; Scalar(offset, b); return BinaryPrimitives.ReadInt16LittleEndian(b); }
    public ushort ReadUnsignedShort(long offset) { Span<byte> b = stackalloc byte[2]; Scalar(offset, b); return BinaryPrimitives.ReadUInt16LittleEndian(b); }
    public int ReadInt(long offset) { Span<byte> b = stackalloc byte[4]; Scalar(offset, b); return BinaryPrimitives.ReadInt32LittleEndian(b); }
    public uint ReadUnsignedInt(long offset) { Span<byte> b = stackalloc byte[4]; Scalar(offset, b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
    public float ReadFloat(long offset) { Span<byte> b = stackalloc byte[4]; Scalar(offset, b); return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b)); }
    public double ReadDouble(long offset) { Span<byte> b = stackalloc byte[8]; Scalar(offset, b); return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(b)); }
    public byte[] ReadBytes(long offset, int count) => Bytes(offset, count);
    public byte[] ReadLargeData(long offset, int count) => Bytes(offset, count);
    public byte[] SafeReadLargeData(long offset, int count)
    {
        if (offset >= Length) return Array.Empty<byte>();
        return Bytes(offset, checked((int)Math.Min(count, Length - offset)));
    }

    private T[] PrimitiveArray<T>(long offset, int count) where T : struct
    {
        int bytes = checked(Unsafe.SizeOf<T>() * count);
        byte[] source = Bytes(offset, bytes);
        var result = new T[count];
        source.AsSpan().CopyTo(MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }
    public double[] ReadDoubles(long offset, int count) => PrimitiveArray<double>(offset, count);
    public float[] ReadFloats(long offset, int count) => PrimitiveArray<float>(offset, count);
    public int[] ReadInts(long offset, int count) => PrimitiveArray<int>(offset, count);
    public uint[] ReadUnsignedInts(long offset, int count) => PrimitiveArray<uint>(offset, count);

    private static T MarshalValue<T>(byte[] bytes) where T : struct
    {
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { return Marshal.PtrToStructure<T>(pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }
    public T ReadStructure<T>(long offset, out long length) where T : struct
    { length = Marshal.SizeOf<T>(); return MarshalValue<T>(Bytes(offset, checked((int)length))); }
    public T ReadStructure<T>(long offset, int size) where T : struct => MarshalValue<T>(Bytes(offset, size));
    public T ReadPreviousRevisionAndConvert<T>(long offset, int previousSize) where T : struct
    {
        byte[] bytes = new byte[Marshal.SizeOf<T>()];
        FillBytes(offset, bytes.AsSpan(0, previousSize));
        return MarshalValue<T>(bytes);
    }
    public T ReadSimpleStructure<T>(long offset) where T : struct
    {
        byte[] bytes = Bytes(offset, Unsafe.SizeOf<T>());
        return MemoryMarshal.Read<T>(bytes);
    }
    public T[] ReadSimpleStructureArray<T>(long offset, int count) where T : struct => PrimitiveArray<T>(offset, count);
    public T[] ReadStructArray<T>(long offset, out long length) where T : struct
    {
        int count = ReadInt(offset);
        int size = Marshal.SizeOf<T>();
        var result = new T[count];
        for (int i = 0; i < count; i++) result[i] = MarshalValue<T>(Bytes(offset + 4L + (long)i * size, size));
        length = 4L + (long)count * size;
        return result;
    }

    public string ReadWideChars(long offset, out long length)
    {
        uint count = ReadUnsignedInt(offset);
        long charsLength = 0;
        string result = ReadWideChars(offset + 4, ref charsLength, count);
        length = 4 + charsLength;
        return result;
    }
    public string ReadString(long offset, out long length)
    {
        int count = ReadInt(offset);
        if (count < 0) throw new InvalidDataException("Negative RAW string length.");
        string result = DecodeWide(offset + 4, checked((uint)count));
        length = 4L + 2L * count;
        return result;
    }
    public string ReadWideChars(long offset, ref long length, uint count)
    { string result = DecodeWide(offset, count); length += 2L * count; return result; }
    private string DecodeWide(long offset, uint count)
    {
        if (count == 0) return string.Empty;
        byte[] bytes = Bytes(offset, checked((int)(2 * count)));
        string value = System.Text.Encoding.Unicode.GetString(bytes);
        int nul = value.IndexOf('\0');
        return nul >= 0 ? value[..nul] : value;
    }
    public string[] ReadStrings(long offset, out long length)
    {
        int count = ReadInt(offset);
        if (count < 0) throw new InvalidDataException("Negative RAW string-array length.");
        long position = offset + 4;
        var result = new string[count];
        for (int i = 0; i < count; i++) { result[i] = ReadString(position, out long used); position += used; }
        length = position - offset;
        return result;
    }

    public byte[] RentBytes(long offset, int count, IBufferPool pool)
    {
        byte[] result = pool.Rent(count);
        FillBytes(offset, result.AsSpan(0, count));
        return result;
    }
    public void ReturnRentedBytes(byte[] bytes, IBufferPool pool) => pool.Release(bytes);
    public IReadWriteAccessor CreateSubView(long offset, long length) => throw new NotSupportedException();

    private static Exception ReadOnly() => new NotSupportedException("The Pioneer RAW cache is read-only.");
    public int IncrementInt(long offset) => throw ReadOnly();
    public long WriteByte(long offset, byte value) => throw ReadOnly();
    public long WriteBytes(long offset, byte[] value) => throw ReadOnly();
    public long WriteDouble(long offset, double value) => throw ReadOnly();
    public long WriteFloat(long offset, float value) => throw ReadOnly();
    public long WriteInt(long offset, int value) => throw ReadOnly();
    public long WriteShort(long offset, short value) => throw ReadOnly();
    public long WriteStruct<T>(long offset, T value) where T : struct => throw ReadOnly();
    public long WriteStruct<T>(long offset, T value, int size) where T : struct => throw ReadOnly();
    public void Dispose() => disposed = true;
}
