using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoFisher.CommonCore.RawFileReader;
using ThermoFisher.CommonCore.RawFileReader.Facade.Interfaces;
using ThermoFisher.CommonCore.RawFileReader.Readers;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: ReadPatternProbe RAW_PATH [MAX_SCANS]");
    return 2;
}
try
{
    string path = Path.GetFullPath(args[0]);
    int maxScans = args.Length == 2 ? int.Parse(args[1]) : int.MaxValue;
    if (maxScans < 1) throw new ArgumentOutOfRangeException(nameof(maxScans));
    var baseline = Digest(path, maxScans, null);
    var trace = new Trace(path, new FileInfo(path).Length);
    var manager = DispatchProxy.Create<IViewCollectionManager, ManagerProxy>();
    // This mapped manager is internal in SDK 7.1.21. Reflection is deliberately
    // isolated to this experimental audit; production must not depend on it.
    var managerType = typeof(RawFileReaderAdapter).Assembly.GetType(
        "ThermoFisher.CommonCore.RawFileReader.Readers.MemoryMappedRawFileManager", throwOnError: true)!;
    var mappedManager = (IViewCollectionManager)managerType.GetProperty("Instance",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
    ((ManagerProxy)(object)manager).Initialize(mappedManager, trace);
    var delegated = Digest(path, maxScans, manager);
    if (baseline.hash != delegated.hash || baseline.scans != delegated.scans)
        throw new InvalidDataException("Delegated and default SDK scan/metadata digests differ.");
    if (trace.ReadCalls == 0)
        throw new InvalidOperationException("SDK did not use the delegated readers for this file; no cache inference is valid.");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        file = path,
        note = "Experimental pass-through SDK read trace. Simulations do not perform cache reads or prefetch. Requested bytes and simulated fetches are not network transfers; timing includes hashing/reflection instrumentation and is not converter timing. Filesystem/server cache state unknown.",
        scans = baseline.scans, digest_sha256 = baseline.hash, digest_match = true,
        default_probe_ms = baseline.ms, delegated_instrumented_probe_ms = delegated.ms,
        baseline_sdk_allocated_bytes = baseline.allocations,
        trace.ReadCalls, trace.KnownReadCalls, trace.RequestedBytes, trace.EofPaddedBytes,
        trace.UnknownReadMethods, trace.UnmodeledReads,
        trace.ReaderViewCount, trace.ReaderViews,
        simulations = trace.Caches.Select(c => c.Report())
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex)
{
    while (ex is TargetInvocationException { InnerException: not null }) ex = ex.InnerException;
    Console.Error.WriteLine($"Read-pattern probe failed: {ex}");
    return 1;
}

static (string hash, int scans, double ms, Dictionary<string, long> allocations) Digest(
    string path, int limit, IViewCollectionManager? manager)
{
    var clock = Stopwatch.StartNew();
    using var raw = manager == null ? RawFileReaderAdapter.FileFactory(path)
        : RawFileReaderAdapter.DelegatedAccessFileFactory(path, manager);
    if (!raw.IsOpen || raw.IsError) throw new IOException($"SDK failed to open {path}");
    raw.SelectInstrument(Device.MS, 1);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var allocations = new Dictionary<string, long>();
    T Measure<T>(string name, Func<T> action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        T value = action();
        allocations[name] = allocations.GetValueOrDefault(name) +
            (GC.GetAllocatedBytesForCurrentThread() - before);
        return value;
    }
    void Integer(long value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, value); hash.AppendData(b); }
    void Number(double value) => Integer(BitConverter.DoubleToInt64Bits(value));
    void Text(string value) { byte[] b = Encoding.UTF8.GetBytes(value); Integer(b.Length); hash.AppendData(b); }
    void Peaks(double[]? values) { Integer(values?.Length ?? -1); if (values != null) hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan())); }
    int scans = 0;
    for (long scan = raw.RunHeaderEx.FirstSpectrum; scan <= raw.RunHeaderEx.LastSpectrum && scans < limit; scan++, scans++)
    {
        int n = (int)scan;
        Integer(n);
        var stats = Measure("scan_statistics", () => raw.GetScanStatsForScanNumber(n));
        Number(stats.BasePeakMass); Number(stats.BasePeakIntensity); Integer(stats.PacketType);
        Number(stats.StartTime); Number(stats.LowMass); Number(stats.HighMass); Number(stats.TIC);
        var centroid = Measure("centroid_stream", () =>
            raw.GetCentroidStream(n, raw.IncludeReferenceAndExceptionData));
        Integer(centroid?.Length ?? -1);
        Peaks(centroid?.Masses); Peaks(centroid?.Intensities);
        var filter = Measure("scan_filter", () => raw.GetFilterForScanNumber(n));
        Text(Measure("filter_string", filter.ToString));
        var scanEvent = Measure("scan_event", () => raw.GetScanEventForScanNumber(n));
        Integer((int)scanEvent.MSOrder);
        if ((int)scanEvent.MSOrder > 1)
        {
            Number(scanEvent.GetMass(0)); Number(scanEvent.GetIsolationWidth(0));
            Number(scanEvent.GetIsolationWidthOffset(0)); Number(scanEvent.GetEnergy(0));
        }
        var trailer = Measure("trailer", () => raw.GetTrailerExtraInformation(n));
        Integer(trailer.Length);
        foreach (string label in trailer.Labels) Text(label);
        foreach (string value in trailer.Values) Text(value);
    }
    return (Convert.ToHexString(hash.GetHashAndReset()), scans,
        clock.Elapsed.TotalMilliseconds, allocations);
}

// The real SDK reader remains responsible for all parsing, errors, lifetime and
// buffer ownership. These proxies forward every call, including by-ref arguments.
public class ManagerProxy : DispatchProxy
{
    private IViewCollectionManager inner = null!;
    private Trace trace = null!;
    public void Initialize(IViewCollectionManager inner, Trace trace) { this.inner = inner; this.trace = trace; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        object? result = ProxyCall.Invoke(inner, method!, args);
        if (result is IReadWriteAccessor reader)
            return ReaderProxy.Wrap(reader, args![1]!.ToString()!, trace);
        return result;
    }
}

public class ReaderProxy : DispatchProxy
{
    private IReadWriteAccessor inner = null!;
    private Trace trace = null!;
    private string source = "";
    private long initialOffset;
    public static IReadWriteAccessor Wrap(IReadWriteAccessor reader, string source, Trace trace)
    {
        var proxy = DispatchProxy.Create<IReadWriteAccessor, ReaderProxy>();
        var state = (ReaderProxy)(object)proxy;
        state.inner = reader; state.trace = trace; state.source = source;
        state.initialOffset = reader.InitialOffset;
        trace.AddView(source, reader.InitialOffset, reader.Length, reader.PreferLargeReads);
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        long before = args is { Length: > 1 } && args[1] is long l ? l : 0;
        object? result = ProxyCall.Invoke(inner, method!, args);
        if (result is IReadWriteAccessor subview) return Wrap(subview, source, trace);
        if (args is { Length: > 0 } && args[0] is long offset &&
            (method!.Name.StartsWith("Read", StringComparison.Ordinal) || method.Name is "RentBytes" or "SafeReadLargeData"))
        {
            long? length = ReadLength(method, args, result, before);
            // Accessor calls are relative to the mapped view. InitialOffset is
            // the view's physical base in the RAW file.
            trace.Read(source, checked(initialOffset + offset), length, method.Name);
        }
        return result;
    }

    private static long? ReadLength(MethodInfo method, object?[] args, object? result, long before)
    {
        switch (method.Name)
        {
            case "ReadByte": return 1;
            case "ReadShort": case "ReadUnsignedShort": return 2;
            case "ReadInt": case "ReadUnsignedInt": case "ReadFloat": return 4;
            case "ReadDouble": return 8;
            case "ReadBytes": case "ReadLargeData": case "SafeReadLargeData": return ((byte[])result!).Length;
            case "RentBytes": return (int)args[1]!;
            case "ReadInts": case "ReadUnsignedInts": case "ReadFloats": return 4L * (int)args[1]!;
            case "ReadDoubles": return 8L * (int)args[1]!;
        }
        if (method.GetParameters().Length > 1 && method.GetParameters()[1].ParameterType == typeof(long).MakeByRefType())
            return (long)args[1]! - before;
        // Structure encodings/revisions may not match managed sizeof(T). Leave
        // them explicitly unknown rather than inventing exact byte counts.
        return null;
    }
}

static class ProxyCall
{
    public static object? Invoke(object inner, MethodInfo method, object?[]? args)
    {
        try { return method.Invoke(inner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
}

public sealed class Trace
{
    private readonly object gate = new();
    private readonly string path;
    private readonly long fileLength;
    public long ReadCalls { get; private set; }
    public long KnownReadCalls { get; private set; }
    public long RequestedBytes { get; private set; }
    public long EofPaddedBytes { get; private set; }
    public long ReaderViewCount { get; private set; }
    public long UnmodeledReads { get; private set; }
    public Dictionary<string, long> UnknownReadMethods { get; } = new();
    public List<object> ReaderViews { get; } = new();
    public CacheModel[] Caches { get; } = (from block in new[] { 64 << 10, 256 << 10, 1 << 20 }
        from ahead in new[] { 0, 1, 2 } select new CacheModel(block, 64 << 20, ahead)).ToArray();
    public Trace(string path, long fileLength) { this.path = path; this.fileLength = fileLength; }
    public void AddView(string source, long offset, long length, bool preferLargeReads)
    {
        lock (gate)
        {
            ReaderViewCount++;
            if (ReaderViews.Count < 64) ReaderViews.Add(new { source, offset, length, preferLargeReads });
        }
    }
    public void Read(string source, long offset, long? length, string method)
    {
        lock (gate)
        {
            ReadCalls++;
            if (length is not >= 0)
            { UnknownReadMethods[method] = UnknownReadMethods.GetValueOrDefault(method) + 1; return; }
            KnownReadCalls++;
            RequestedBytes += length.Value;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if ((!source.Equals(path, comparison) && !source.EndsWith("__" + path, comparison)) ||
                offset < 0 || offset > fileLength)
            { UnmodeledReads++; return; }
            long physicalLength = Math.Min(length.Value, fileLength - offset);
            EofPaddedBytes += length.Value - physicalLength;
            foreach (var cache in Caches) cache.Read(path, offset, physicalLength, fileLength);
        }
    }
}

// Online simulation stores only a bounded block index and counters, not RAW
// contents or an unbounded access trace. Prefetch is optimistic and instantaneous:
// the model measures locality/amplification, not latency overlap or actual I/O.
public sealed class CacheModel
{
    private readonly int blockSize, budget, ahead;
    private readonly Dictionary<(string, long), LinkedListNode<Entry>> blocks = new();
    private readonly LinkedList<Entry> lru = new();
    private long resident, peak, requestedBlocks, demandMisses, demandBytes, prefetchedBytes, usefulPrefetchBytes, unusedPrefetchBytes;
    private sealed record Entry((string, long) Key, long Bytes) { public bool Prefetched { get; set; } }
    public CacheModel(int blockSize, int budget, int ahead) { this.blockSize = blockSize; this.budget = budget; this.ahead = ahead; }
    public void Read(string source, long offset, long count, long end)
    {
        if (count == 0) return;
        long last = (offset + count - 1) / blockSize;
        for (long block = offset / blockSize; block <= last; block++)
        {
            requestedBlocks++;
            var key = (source, block);
            if (blocks.TryGetValue(key, out var hit))
            {
                if (hit.Value.Prefetched) { usefulPrefetchBytes += hit.Value.Bytes; hit.Value.Prefetched = false; }
                lru.Remove(hit); lru.AddLast(hit);
                continue;
            }
            demandMisses++;
            Fetch(source, block, end, false);
            for (int i = 1; i <= ahead; i++) Fetch(source, block + i, end, true);
        }
    }
    private void Fetch(string source, long block, long end, bool prefetch)
    {
        var key = (source, block);
        if (blocks.ContainsKey(key)) return;
        long bytes = Math.Min(blockSize, end - block * blockSize);
        if (bytes <= 0) return;
        while (resident + bytes > budget && lru.First is { } first)
        {
            lru.RemoveFirst(); blocks.Remove(first.Value.Key); resident -= first.Value.Bytes;
            if (first.Value.Prefetched) unusedPrefetchBytes += first.Value.Bytes;
        }
        var entry = new Entry(key, bytes) { Prefetched = prefetch };
        blocks.Add(key, lru.AddLast(entry)); resident += bytes; peak = Math.Max(peak, resident);
        if (prefetch) prefetchedBytes += bytes; else demandBytes += bytes;
    }
    public object Report() => new
    {
        block_bytes = blockSize, budget_bytes = budget, ahead_blocks = ahead, peak_resident_bytes = peak,
        requested_blocks = requestedBlocks, demand_misses = demandMisses,
        simulated_demand_bytes = demandBytes, simulated_prefetch_bytes = prefetchedBytes,
        simulated_total_fetch_bytes = demandBytes + prefetchedBytes,
        useful_prefetch_bytes = usefulPrefetchBytes,
        unused_prefetch_bytes = unusedPrefetchBytes + lru.Where(e => e.Prefetched).Sum(e => e.Bytes)
    };
}
