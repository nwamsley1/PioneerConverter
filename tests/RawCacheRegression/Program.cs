using System.Buffers.Binary;

string path = Path.Combine(Path.GetTempPath(), $"pioneer__raw-cache-{Guid.NewGuid():N}.bin");
byte[] expected = new byte[6 * 4096 + 731];
new Random(1729).NextBytes(expected);
File.WriteAllBytes(path, expected);

try
{
    using (var cache = new ByteBlockCache(path, 4096, 8 * 4096, 1))
    {
        for (int offset = 0; offset < expected.Length; offset += 4096)
            if (cache.ReadBytes(offset, 1)[0] != expected[offset])
                throw new InvalidOperationException($"Sequential cached byte differs at {offset}.");
        string stats = cache.FormatStatistics();
        if (ParseStat(stats, "demand_loads") != 1 ||
            ParseStat(stats, "prefetch_loads") != 6 ||
            ParseStat(stats, "useful_prefetches") != 6)
            throw new InvalidOperationException($"Read-ahead did not continue after consuming a prefetched block: {stats}");
    }

    using (var cache = new ByteBlockCache(path, 4096, 3 * 4096, 1))
    {
        var random = new Random(314159);
        var requests = Enumerable.Range(0, 400).Select(_ =>
        {
            int count = random.Next(1, 9000);
            int offset = random.Next(0, expected.Length - count + 1);
            return (offset, count);
        }).ToArray();
        await Parallel.ForEachAsync(requests, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (request, _) =>
            {
                byte[] actual = cache.ReadBytes(request.offset, request.count);
                if (!actual.AsSpan().SequenceEqual(expected.AsSpan(request.offset, request.count)))
                    throw new InvalidOperationException($"Cached bytes differ at {request.offset}+{request.count}.");
                return ValueTask.CompletedTask;
            });
        string stats = cache.FormatStatistics();
        long peak = ParseStat(stats, "peak_bytes");
        if (peak > 3 * 4096) throw new InvalidOperationException($"Cache exceeded its byte budget: {stats}");
        if (ParseStat(stats, "reused_buffers") == 0)
            throw new InvalidOperationException($"Evicted block buffers were not reused: {stats}");

        const int baseOffset = 123;
        using var accessor = new CachedReadAccessor(cache, path, baseOffset, expected.Length, preferLargeReads: true);
        if (!accessor.PreferLargeReads || accessor.InitialOffset != baseOffset)
            throw new InvalidOperationException("Delegated accessor properties differ.");
        byte[] relative = accessor.ReadBytes(777, 321);
        if (!relative.AsSpan().SequenceEqual(expected.AsSpan(baseOffset + 777, 321)))
            throw new InvalidOperationException("Offset view did not translate relative offsets.");
        int integer = accessor.ReadInt(1000);
        if (integer != BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(baseOffset + 1000, 4)))
            throw new InvalidOperationException("Scalar read differs.");

        int available = 17;
        byte[] padded = accessor.ReadBytes(expected.Length - baseOffset - available, 100);
        if (!padded.AsSpan(0, available).SequenceEqual(expected.AsSpan(expected.Length - available)) ||
            padded.AsSpan(available).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidOperationException("EOF-crossing array read does not match mapped-reader zero padding.");
    }

    using (var manager = new CachedViewManager(path, 4096, 2 * 4096, 0))
    {
        using var view = manager.GetRandomAccessViewer(Guid.NewGuid(), path, false,
            ThermoFisher.CommonCore.RawFileReader.Readers.DataFileAccessMode.Read,
            ThermoFisher.CommonCore.RawFileReader.Readers.PersistenceMode.NonPersisted);
        if (!manager.WasUsed || !view.ReadBytes(0, 64).AsSpan().SequenceEqual(expected.AsSpan(0, 64)))
            throw new InvalidOperationException("View manager did not serve the requested stream.");
        using var aliasView = manager.GetRandomAccessViewer(Guid.NewGuid(), "alias__" + path, false,
            ThermoFisher.CommonCore.RawFileReader.Readers.DataFileAccessMode.Read,
            ThermoFisher.CommonCore.RawFileReader.Readers.PersistenceMode.NonPersisted);
        if (!aliasView.ReadBytes(0, 64).AsSpan().SequenceEqual(expected.AsSpan(0, 64)))
            throw new InvalidOperationException("View manager did not normalize an SDK stream alias.");
    }

    using (var cache = new ByteBlockCache(path, 4096, 4096, 0))
    {
        using (var truncate = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            truncate.SetLength(4096);
        Task failedRead = Task.Run(() => cache.ReadBytes(8192, 32));
        try { await failedRead.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (EndOfStreamException) { }
        if (!failedRead.IsFaulted || failedRead.Exception?.GetBaseException() is not EndOfStreamException)
            throw new InvalidOperationException("A truncated backing file did not fail promptly with EndOfStreamException.");
    }

    Console.WriteLine("PASS: continuing bounded read-ahead, concurrent block reads, view offsets, EOF padding, scalar values, disposal, and I/O failure propagation.");
}
finally
{
    File.Delete(path);
}

static long ParseStat(string line, string name)
{
    string prefix = name + "=";
    string value = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Single(part => part.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
    return long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
