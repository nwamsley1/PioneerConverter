#!/usr/bin/env python3
"""Prepare an isolated, instrumented COPY of the within-file chunk pipeline.

Usage: instrument_scan_profile.py SOURCE_CHECKOUT NEW_DESTINATION
Build the copy, then set PIONEER_SCAN_PROFILE=/local/path/profile.json for one RAW
conversion. No RAW is copied. Production source is never changed. Unrecognized
source anchors fail before destination creation. The original production files
are retained in the copy as *.original. Profiling is inactive unless the env var
is nonempty; the diagnostic build adds no per-row allocation or output.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('source', type=Path)
parser.add_argument('destination', type=Path)
args = parser.parse_args()
source = args.source.resolve()
destination = args.destination.resolve()
s = (source / 'Program.cs').read_text(encoding='utf-8-sig')

def replace(old, new):
    global s
    count = s.count(old)
    if count != 1:
        raise SystemExit(f'Expected one source anchor, found {count}: {old[:110]!r}')
    s = s.replace(old, new)

replace('        var watch = Stopwatch.StartNew();\n        using var viewManager',
        '        var watch = Stopwatch.StartNew();\n        using var scanProfile = ScanProfileSession.Create(inputFile, scanThreads, scanChunkSize, batchSize);\n        using var viewManager')
replace('                        var rows = new ScanRow[capacity];',
        '                        var profileChunk = scanProfile?.BeginChunk(chunkCount, checked((int)start), capacity, scanWorkers?.Count ?? 1, started);\n                        var rows = new ScanRow[capacity];')
replace('                        void Extract(IRawDataPlus reader, ref int hcd, ref int fill)\n                        {',
        '                        void Extract(IRawDataPlus reader, ref int hcd, ref int fill, int profileWorkerId)\n                        {\n                            using var profileWorker = profileChunk?.BeginWorker(profileWorkerId);')
replace('                                var row = ReadScanRow(reader, checked((int)start + index), ref hcd, ref fill);',
        '                                var row = ReadScanRow(reader, checked((int)start + index), ref hcd, ref fill, profileWorker);\n                                profileWorker?.RowCompleted();')
replace('                            Extract(rawFile, ref hcdIndex, ref fillIndex);',
        '                            Extract(rawFile, ref hcdIndex, ref fillIndex, 0);')
replace('                                Extract(worker.RawFile, ref worker.HcdEnergyFieldIndex, ref worker.FillTimeFieldIndex);',
        '                                Extract(worker.RawFile, ref worker.HcdEnergyFieldIndex, ref worker.FillTimeFieldIndex, i);')
replace('                        chunks.Add(new ScanChunk(rows, count), bytes + 8L * capacity);',
        '''                        profileChunk?.ExtractionEnded(count, bytes);
                        profileChunk?.PublishStarted();
                        try { chunks.Add(new ScanChunk(rows, count), bytes + 8L * capacity); }
                        finally { profileChunk?.Published(); }''')
replace('                        using var lease = chunks.Read();',
        '''                        long profileReadStarted = scanProfile?.Timestamp() ?? 0;
                        BoundedPipelineQueue<ScanChunk>.Lease? profileLease;
                        try { profileLease = chunks.Read(); }
                        finally { scanProfile?.EndAssemblyRead(profileReadStarted); }
                        using var lease = profileLease;''')
replace('            File.Move(temporaryOutput, outputFile, overwrite: true);',
        '            File.Move(temporaryOutput, outputFile, overwrite: true);\n            scanProfile?.Succeeded();')
replace('    static ScanRow ReadScanRow(IRawDataPlus rawFile, int scanNumber, ref int hcdEnergyFieldIndex, ref int fillTimeFieldIndex)\n    {',
        '''    static ScanRow ReadScanRow(IRawDataPlus rawFile, int scanNumber, ref int hcdEnergyFieldIndex, ref int fillTimeFieldIndex, ScanProfileWorker? profileWorker = null)
    {
        profileWorker?.ReadEnter(scanNumber);
        try
        {''')
replace('        var stats = rawFile.GetScanStatsForScanNumber(scanNumber);',
        '''        long profileSdkStarted = profileWorker?.Timestamp() ?? 0;
        var stats = rawFile.GetScanStatsForScanNumber(scanNumber);
        profileWorker?.AddSdk(0, profileSdkStarted);''')
replace('        var centroid = rawFile.GetSimplifiedCentroids(scanNumber);',
        '''        profileSdkStarted = profileWorker?.Timestamp() ?? 0;
        var centroid = rawFile.GetSimplifiedCentroids(scanNumber);
        profileWorker?.AddSdk(1, profileSdkStarted);
        profileSdkStarted = profileWorker?.Timestamp() ?? 0;
        var profileHeader = rawFile.GetFilterForScanNumber(scanNumber).ToString();
        profileWorker?.AddSdk(2, profileSdkStarted);''')
replace('            Header = rawFile.GetFilterForScanNumber(scanNumber).ToString(),',
        '            Header = profileHeader,')
replace('        var scanEvent = rawFile.GetScanEventForScanNumber(scanNumber);',
        '''        profileSdkStarted = profileWorker?.Timestamp() ?? 0;
        var scanEvent = rawFile.GetScanEventForScanNumber(scanNumber);
        profileWorker?.AddSdk(3, profileSdkStarted);''')
replace('        var trailerData = rawFile.GetTrailerExtraInformation(scanNumber);',
        '''        profileSdkStarted = profileWorker?.Timestamp() ?? 0;
        var trailerData = rawFile.GetTrailerExtraInformation(scanNumber);
        profileWorker?.AddSdk(4, profileSdkStarted);''')
replace('        return row;\n    }\n\n    static bool TryReadFillTimeMs',
        '''        return row;
        }
        finally { profileWorker?.ReadExit(); }
    }

    static bool TryReadFillTimeMs''')

helper = r'''// Diagnostic-copy helper. No production build includes this generated file.
using System.Diagnostics;
using System.Text.Json;

internal sealed class ScanProfileSession : IDisposable
{
    internal readonly long Started = Stopwatch.GetTimestamp();
    private readonly string path, input;
    private readonly int requestedWorkers, chunkSize, batchSize;
    private readonly List<ScanProfileChunk> chunks = new(); // producer owns
    private readonly List<(long Start, long End, int Thread)> reads = new(); // assembler owns
    private int activeReads, peakReads;
    private bool success;
    private readonly object initialPool;
    private ScanProfileSession(string path, string input, int workers, int chunk, int batch)
    {
        this.path = path; this.input = input; requestedWorkers = workers;
        chunkSize = chunk; batchSize = batch; initialPool = PoolSnapshot();
    }
    internal static ScanProfileSession? Create(string input, int workers, int chunk, int batch)
    {
        string? path = Environment.GetEnvironmentVariable("PIONEER_SCAN_PROFILE");
        return string.IsNullOrWhiteSpace(path) ? null : new(path, input, workers, chunk, batch);
    }
    internal long Timestamp() => Stopwatch.GetTimestamp();
    internal static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    internal double Since(long ticks) => ticks == 0 ? 0 : Ms(ticks - Started);
    internal static object PoolSnapshot()
    {
        ThreadPool.GetAvailableThreads(out int available, out int ioAvailable);
        ThreadPool.GetMinThreads(out int minimum, out int ioMinimum);
        ThreadPool.GetMaxThreads(out int maximum, out int ioMaximum);
        return new { thread_count = ThreadPool.ThreadCount, available_workers = available,
            minimum_workers = minimum, maximum_workers = maximum, available_io = ioAvailable,
            minimum_io = ioMinimum, maximum_io = ioMaximum };
    }
    internal static void Raise(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
            Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
    internal void Enter() { Raise(ref peakReads, Interlocked.Increment(ref activeReads)); }
    internal void Exit() => Interlocked.Decrement(ref activeReads);
    internal ScanProfileChunk BeginChunk(int index, int first, int capacity, int workers, long start)
    {
        var chunk = new ScanProfileChunk(this, index, first, capacity, workers, start);
        chunks.Add(chunk);
        return chunk;
    }
    internal void EndAssemblyRead(long start) => reads.Add((start, Timestamp(), Environment.CurrentManagedThreadId));
    internal void Succeeded() => success = true;
    public void Dispose()
    {
        // ConvertRawFile joins all producer/assembler work before disposing this scope.
        try
        {
            var workers = chunks.SelectMany(c => c.Workers).OfType<ScanProfileWorker>().ToArray();
            var report = new {
                schema_version = 1, input_file = input, success,
                requested_workers = requestedWorkers, scan_chunk_size = chunkSize, batch_size = batchSize,
                elapsed_ms = Ms(Timestamp() - Started), active_reads_high_water = peakReads,
                active_reads_at_end = activeReads, initial_thread_pool = initialPool,
                final_thread_pool = PoolSnapshot(),
                notes = new[] {
                    "Times are monotonic milliseconds from this file's profiling scope start.",
                    "SDK buckets are summed per-worker wall durations and overlap across workers.",
                    "Queue waits measure Add/Read call durations, including uncontended call overhead.",
                    "Worker records include zero-read callbacks; participating_workers counts completed scans > 0.",
                    "No per-scan records or allocations; SDK timing adds timestamp calls and high-water counters.",
                    "last_worker_tail_ms is last minus second-last end among workers completing scans (one worker: whole worker duration)."
                },
                workers = workers.GroupBy(w => w.Id).OrderBy(g => g.Key).Select(g => new {
                    worker_id = g.Key, chunk_callbacks = g.Count(), participating_chunks = g.Count(w => w.Completed > 0),
                    scans_started = g.Sum(w => w.Attempts), scans_completed = g.Sum(w => w.Completed),
                    managed_thread_ids = g.Select(w => w.ThreadId).Distinct().OrderBy(x => x),
                    active_read_ms = Ms(g.Sum(w => w.ReadTicks)),
                    worker_callback_ms = Ms(g.Sum(w => w.End - w.Start)),
                    sdk_ms = new {
                        stats = Ms(g.Sum(w => w.Sdk[0])), centroid = Ms(g.Sum(w => w.Sdk[1])),
                        filter = Ms(g.Sum(w => w.Sdk[2])), scan_event = Ms(g.Sum(w => w.Sdk[3])),
                        trailer = Ms(g.Sum(w => w.Sdk[4])) }
                }),
                chunks = chunks.Select(c => c.Report()),
                assembly_reads = reads.Select((r, index) => new { index, start_ms = Since(r.Start),
                    end_ms = Since(r.End), wait_ms = Ms(r.End - r.Start), managed_thread_id = r.Thread }),
                producer_add_wait_ms = chunks.Sum(c => Ms(c.PublishEnd - c.PublishStart)),
                assembly_read_wait_ms = reads.Sum(r => Ms(r.End - r.Start))
            };
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Console.Error.WriteLine("Scan profile output failed: " + ex.Message); }
    }
}

internal sealed class ScanProfileChunk
{
    internal readonly ScanProfileSession Session;
    internal readonly ScanProfileWorker?[] Workers;
    private readonly int index, first, capacity;
    private readonly long start;
    private long end, bytes;
    internal long PublishStart, PublishEnd;
    private int count, activeReads, peakReads;
    private readonly object poolStart;
    private object? poolEnd;
    internal ScanProfileChunk(ScanProfileSession session, int index, int first, int capacity, int workers, long start)
    {
        Session = session; this.index = index; this.first = first; this.capacity = capacity; this.start = start;
        Workers = new ScanProfileWorker?[workers]; poolStart = ScanProfileSession.PoolSnapshot();
    }
    internal ScanProfileWorker BeginWorker(int id) { var worker = new ScanProfileWorker(this, id); Workers[id] = worker; return worker; }
    internal void Enter() { Session.Enter(); ScanProfileSession.Raise(ref peakReads, Interlocked.Increment(ref activeReads)); }
    internal void Exit() { Interlocked.Decrement(ref activeReads); Session.Exit(); }
    internal void ExtractionEnded(int count, long bytes)
    { end = Session.Timestamp(); this.count = count; this.bytes = bytes; poolEnd = ScanProfileSession.PoolSnapshot(); }
    internal void PublishStarted() => PublishStart = Session.Timestamp();
    internal void Published() => PublishEnd = Session.Timestamp();
    internal object Report()
    {
        var participating = Workers.OfType<ScanProfileWorker>().Where(w => w.Completed > 0).OrderBy(w => w.End).ToArray();
        long tail = participating.Length == 0 ? 0 : participating.Length == 1
            ? participating[0].End - participating[0].Start
            : participating[^1].End - participating[^2].End;
        return new { chunk_index = index, first_scan = first, requested_capacity = capacity,
            scans = count, decoded_bytes = bytes, start_ms = Session.Since(start), end_ms = Session.Since(end),
            extraction_ms = end == 0 ? 0 : ScanProfileSession.Ms(end - start),
            active_reads_high_water = peakReads, participating_workers = participating.Length,
            last_worker_id = participating.LastOrDefault()?.Id,
            last_worker_tail_ms = ScanProfileSession.Ms(tail),
            producer_add_start_ms = Session.Since(PublishStart), producer_add_end_ms = Session.Since(PublishEnd),
            producer_add_wait_ms = ScanProfileSession.Ms(PublishEnd - PublishStart),
            thread_pool_start = poolStart, thread_pool_end = poolEnd,
            workers = Workers.OfType<ScanProfileWorker>().Select(w => w.Report()) };
    }
}

internal sealed class ScanProfileWorker : IDisposable
{
    private readonly ScanProfileChunk chunk;
    internal readonly int Id, ThreadId = Environment.CurrentManagedThreadId;
    internal readonly long Start = Stopwatch.GetTimestamp();
    internal long End, ReadTicks;
    internal readonly long[] Sdk = new long[5];
    internal int Attempts, Completed;
    private long firstRead, lastRead, currentRead;
    private int firstScan, lastScan;
    internal ScanProfileWorker(ScanProfileChunk chunk, int id) { this.chunk = chunk; Id = id; }
    internal long Timestamp() => Stopwatch.GetTimestamp();
    internal void AddSdk(int stage, long start) => Sdk[stage] += Stopwatch.GetTimestamp() - start;
    internal void ReadEnter(int scan)
    {
        currentRead = Timestamp();
        if (Attempts == 0) { firstRead = currentRead; firstScan = scan; }
        lastScan = scan; Attempts++; chunk.Enter();
    }
    internal void ReadExit() { lastRead = Timestamp(); ReadTicks += lastRead - currentRead; chunk.Exit(); }
    internal void RowCompleted() => Completed++;
    public void Dispose() => End = Timestamp();
    internal object Report() => new {
        worker_id = Id, managed_thread_id = ThreadId, start_ms = chunk.Session.Since(Start), end_ms = chunk.Session.Since(End),
        scans_started = Attempts, scans_completed = Completed, first_scan = firstScan, last_scan = lastScan,
        first_read_start_ms = chunk.Session.Since(firstRead), last_read_end_ms = chunk.Session.Since(lastRead),
        active_read_ms = ScanProfileSession.Ms(ReadTicks),
        sdk_ms = new { stats = ScanProfileSession.Ms(Sdk[0]), centroid = ScanProfileSession.Ms(Sdk[1]),
            filter = ScanProfileSession.Ms(Sdk[2]), scan_event = ScanProfileSession.Ms(Sdk[3]), trailer = ScanProfileSession.Ms(Sdk[4]) }
    };
}
'''

source_files = ['Program.cs', 'Pipeline.cs', 'CachedRawFile.cs']
if (source / 'ScanWorkerTeam.cs').is_file():
    source_files.append('ScanWorkerTeam.cs')
destination.mkdir(parents=True, exist_ok=False)
for name in source_files:
    shutil.copy2(source / name, destination / (name + '.original'))
    if name != 'Program.cs':
        shutil.copy2(source / name, destination)
shutil.copy2(source / 'PioneerConverter.csproj', destination)
shutil.copytree(source / 'Libs', destination / 'Libs')
(destination / 'Program.cs').write_text(s)
(destination / 'ScanProfileInstrumentation.cs').write_text(helper)
(destination / 'profile-source.json').write_text(json.dumps({
    'source': str(source),
    'source_sha256': {name: hashlib.sha256((source / name).read_bytes()).hexdigest()
                      for name in source_files + ['PioneerConverter.csproj']},
    'instrumentation_tool': str(Path(__file__).resolve()),
}, indent=2) + '\n')
print(f'Prepared diagnostic copy: {destination}')
print(f'Build: dotnet build {destination}/PioneerConverter.csproj -c Release -p:NuGetAudit=false')
print('Run one file with PIONEER_SCAN_PROFILE=/local/path/profile.json. Unset to disable instrumentation.')
