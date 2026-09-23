using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: TraceSummary TRACE.nettrace[.etlx] OUTPUT.json");
    return 2;
}
var input = Path.GetFullPath(args[0]);
var etlx = input.EndsWith(".etlx", StringComparison.OrdinalIgnoreCase) ? input : input + ".etlx";
if (!File.Exists(etlx)) TraceLog.CreateFromEventPipeDataFile(input, etlx, new TraceLogOptions());
using var log = new TraceLog(etlx);
var counts = new Dictionary<string, long>();
var examples = new Dictionary<string, object>();
var threads = new Dictionary<int, ThreadSummary>();
var stacks = new Dictionary<CallStackIndex, string[]>();
var inclusive = new Dictionary<string, long>();
var exclusive = new Dictionary<string, long>();
var sampleTypes = new Dictionary<string, long>();
var categories = new Dictionary<string, long>();
var gcCounts = new Dictionary<string, long>();
var gcPauses = new List<double>();
var suspensions = new Dictionary<string, List<double>>();
var pendingSuspension = new Dictionary<(int Process, int Thread), (double Time, string Reason)>();
var gcPauseRanges = new List<(double Start, double End)>();
int unmatchedSuspensionStops = 0, overwrittenSuspensionStarts = 0;
var pendingContention = new Dictionary<int, (double Time, string[] Stack)>();
var contentions = new List<object>();
var poolEvents = new List<object>();
var threadEvents = new List<object>();
var methods = new List<MethodRange>();
var allocationTypes = new Dictionary<string, long>();
long allocatedBytes = 0, sampleCount = 0, samplesWithoutStack = 0;
double firstMs = double.PositiveInfinity, lastMs = 0;

string[] Stack(TraceEvent e)
{
    var index = e.CallStackIndex();
    if (stacks.TryGetValue(index, out var names)) return names;
    var frames = new List<string>();
    for (var frame = e.CallStack(); frame != null; frame = frame.Caller)
    {
        var address = frame.CodeAddress;
        var name = address.FullMethodName;
        frames.Add(string.IsNullOrEmpty(name) ? $"{address.ModuleName}!0x{address.Address:x}" : name);
    }
    return stacks[index] = frames.ToArray();
}
static void Increment(Dictionary<string, long> values, string key, long amount = 1) => values[key] = values.GetValueOrDefault(key) + amount;
static object? Payload(TraceEvent e, string name) => e.PayloadIndex(name) < 0 ? null : e.PayloadByName(name);
static string Text(TraceEvent e, string name) => Convert.ToString(Payload(e, name), CultureInfo.InvariantCulture) ?? "";
static double Number(TraceEvent e, string name) => Convert.ToDouble(Payload(e, name) ?? 0, CultureInfo.InvariantCulture);
static object Fields(TraceEvent e) => e.PayloadNames.ToDictionary(n => n, n => Text(e, n));
static string Category(string[] frames)
{
    bool Has(string text) => frames.Any(f => f.Contains(text, StringComparison.Ordinal));
    // These are stack-location labels, not a CPU/blocking state determination.
    if (Has("LowLevelLifoSemaphore.Wait")) return "thread_pool_semaphore_wait";
    if (Has("WaitHandle.WaitOne") || Has("Monitor.Wait") || Has("ManualResetEventSlim.Wait")) return "other_wait_stack";
    if (Has("SafeBuffer.") || Has("UnmanagedMemoryAccessor.") || Has("SafeHandle.")) return "mapped_buffer_access";
    if (Has("ReadScanRow")) return "other_scan_read_stack";
    if (Has("ArrowBatchBuilder") || Has("PeakScratch") || Has("Apache.Arrow")) return "arrow_stack";
    if (Has("SpinWait")) return "other_spin_stack";
    if (Has("EventPipeEventProvider") || Has("EventSource.Write")) return "event_instrumentation_stack";
    return "other_or_unresolved";
}

foreach (var e in log.Events)
{
    var key = e.ProviderName + "/" + e.EventName;
    Increment(counts, key);
    firstMs = Math.Min(firstMs, e.TimeStampRelativeMSec);
    lastMs = Math.Max(lastMs, e.TimeStampRelativeMSec);
    if (!threads.TryGetValue(e.ThreadID, out var thread)) threads[e.ThreadID] = thread = new ThreadSummary(e.ThreadID);
    thread.Events++;
    thread.FirstMs = Math.Min(thread.FirstMs, e.TimeStampRelativeMSec);
    thread.LastMs = Math.Max(thread.LastMs, e.TimeStampRelativeMSec);
    if (e.ProviderName == "Microsoft-DotNETCore-SampleProfiler" && e.EventName == "Thread/Sample")
    {
        sampleCount++;
        thread.Samples++;
        var type = Text(e, "Type");
        Increment(sampleTypes, type);
        Increment(thread.SampleTypes, type);
        var frames = Stack(e);
        if (frames.Length == 0) samplesWithoutStack++;
        else
        {
            Increment(exclusive, frames[0]);
            foreach (var frame in frames.Distinct()) Increment(inclusive, frame);
        }
        var category = Category(frames);
        Increment(categories, category);
        Increment(thread.Categories, category);
        continue;
    }
    if (!examples.ContainsKey(key)) examples[key] = Fields(e);
    if (e.ProviderName == "Microsoft-Windows-DotNETRuntime")
    {
        if (e.EventName == "GC/Start") Increment(gcCounts, $"generation={Text(e,"Depth")};reason={Text(e,"Reason")};type={Text(e,"Type")}");
        if (e.EventName == "GC/AllocationTick")
        {
            long amount = Convert.ToInt64(Payload(e, "AllocationAmount64") ?? Payload(e, "AllocationAmount") ?? 0);
            allocatedBytes += amount;
            Increment(allocationTypes, Text(e, "TypeName"), amount);
        }
        if (e.EventName == "GC/SuspendEEStart")
        {
            // Sampling and GC suspension requests can overlap on different threads.
            var suspensionKey = (e.ProcessID, e.ThreadID);
            if (pendingSuspension.ContainsKey(suspensionKey)) overwrittenSuspensionStarts++;
            pendingSuspension[suspensionKey] = (e.TimeStampRelativeMSec, Text(e, "Reason"));
        }
        if (e.EventName == "GC/RestartEEStop")
        {
            if (pendingSuspension.Remove((e.ProcessID, e.ThreadID), out var suspension))
            {
                var duration = e.TimeStampRelativeMSec - suspension.Time;
                if (!suspensions.TryGetValue(suspension.Reason, out var durations)) suspensions[suspension.Reason] = durations = new();
                durations.Add(duration);
                if (suspension.Reason is "SuspendForGC" or "SuspendForGCPrep")
                {
                    gcPauses.Add(duration);
                    gcPauseRanges.Add((suspension.Time, e.TimeStampRelativeMSec));
                }
            }
            else unmatchedSuspensionStops++;
        }
        if (e.EventName == "Contention/Start") pendingContention[e.ThreadID] = (e.TimeStampRelativeMSec, Stack(e));
        if (e.EventName == "Contention/Stop")
        {
            pendingContention.Remove(e.ThreadID, out var contention);
            contentions.Add(new { os_thread_id = e.ThreadID, end_ms = e.TimeStampRelativeMSec,
                duration_ms = Number(e, "DurationNs") / 1_000_000,
                matched_start = contention.Stack != null, stack = contention.Stack });
        }
        if (e.EventName.StartsWith("ThreadPool") && !e.EventName.EndsWith("/Wait"))
            poolEvents.Add(new { name = e.EventName, at_ms = e.TimeStampRelativeMSec, os_thread_id = e.ThreadID, payload = Fields(e) });
        if (e.EventName.Contains("ThreadCreated") || e.EventName is "Thread/Creating" or "Thread/Running")
            threadEvents.Add(new { name = e.EventName, at_ms = e.TimeStampRelativeMSec, os_thread_id = e.ThreadID, payload = Fields(e) });
    }
    if (e.ProviderName == "System.Threading.Tasks.TplEventSource")
    {
        if (e.EventName == "TaskExecute/Start") thread.TaskStarts++;
        if (e.EventName == "TaskScheduled/Send") thread.TaskSchedules++;
    }
    if (e.EventName is "Method/LoadVerbose" or "Method/DCStopVerbose")
    {
        ulong start = Convert.ToUInt64(Payload(e, "MethodStartAddress") ?? 0);
        ulong size = Convert.ToUInt64(Payload(e, "MethodSize") ?? 0);
        if (start > 0 && size > 0) methods.Add(new(start, size, Text(e, "MethodNamespace") + "." + Text(e, "MethodName") + " " + Text(e, "MethodSignature")));
    }
}

static object[] Ranked(Dictionary<string, long> values, int take = 100) => values.OrderByDescending(v => v.Value)
    .Take(take).Select(v => (object)new { name = v.Key, count = v.Value }).ToArray();
static object DurationSummary(List<double> values) => new { count = values.Count, total_ms = values.Sum(),
    max_ms = values.Count > 0 ? values.Max() : 0, mean_ms = values.Count > 0 ? values.Average() : 0 };
var native = new List<object>();
double gcPauseUnionMs = 0, gcPauseEnd = double.NegativeInfinity;
foreach (var range in gcPauseRanges.OrderBy(r => r.Start))
{
    gcPauseUnionMs += Math.Max(0, range.End - Math.Max(range.Start, gcPauseEnd));
    gcPauseEnd = Math.Max(gcPauseEnd, range.End);
}
var nativePath = Path.Combine(Path.GetDirectoryName(input)!, "native-sample.txt");
if (File.Exists(nativePath))
{
    var text = File.ReadAllText(nativePath);
    var begin = text.IndexOf("Sort by top of stack", StringComparison.Ordinal);
    var end = begin < 0 ? -1 : text.IndexOf("Binary Images:", begin, StringComparison.Ordinal);
    if (begin >= 0 && end >= 0)
    foreach (Match m in Regex.Matches(text[begin..end], @"^\s+(.+?)\s+(\d+)\s*$", RegexOptions.Multiline))
    {
        var frame = m.Groups[1].Value;
        var address = Regex.Match(frame, @"\[0x([0-9a-f]+)\]");
        MethodRange? method = null;
        if (address.Success)
        {
            var ip = Convert.ToUInt64(address.Groups[1].Value, 16);
            method = methods.LastOrDefault(r => ip >= r.Start && ip - r.Start < r.Size);
        }
        native.Add(new { native_frame = frame, samples = long.Parse(m.Groups[2].Value),
            mapped_jit_method = method?.Name, mapping_note = method == null ? null : "JIT method address range; native summary does not retain timestamp so tiered-code reuse is a caveat" });
    }
}
var report = new
{
    schema_version = 1, input, events = log.EventCount, events_lost = log.EventsLost, truncated = log.Truncated,
    first_event_ms = firstMs, last_event_ms = lastMs,
    interpretation = new[] {
        "Samples count observed thread stacks, including waiting/external/suspended states. They are NOT on-CPU time, elapsed milliseconds, or proof of spin contention.",
        "Inclusive counts count a method once per sample; categories prioritize waits then buffer access. No Task activity folding is applied.",
        "Managed and External sample types are runtime sampling labels, not OS scheduling states.",
        "GC suspension intervals pair per process/thread SuspendForGC/Prep through RestartEEStop, including time to suspend; union_ms avoids overlap. SuspendOther is separate and includes sampling/profiler overhead. These are not GC CPU time.",
        "AllocationTick byte totals estimate allocation volume; counter intervals can miss startup/shutdown and are summarized separately.",
        "Native sample is an early fixed window and includes waiting threads. Unknown JIT IP mapping uses traced method ranges without per-sample timestamps." },
    sampled_thread_stacks = new { total = sampleCount, samples_without_stack = samplesWithoutStack,
        sample_types = sampleTypes, categories, exclusive = Ranked(exclusive), inclusive = Ranked(inclusive) },
    threads = threads.Values.OrderByDescending(t => t.Samples),
    event_counts = counts.OrderByDescending(x => x.Value).ToDictionary(x => x.Key, x => x.Value),
    event_payload_examples = examples, gc = new { counts = gcCounts, pause = DurationSummary(gcPauses), pause_union_ms = gcPauseUnionMs,
        unmatched_suspension_stops = unmatchedSuspensionStops, overwritten_suspension_starts = overwrittenSuspensionStarts,
        unclosed_suspension_starts = pendingSuspension.Count,
        suspensions_by_reason = suspensions.ToDictionary(x => x.Key, x => DurationSummary(x.Value)),
        allocation_tick_estimated_bytes = allocatedBytes, allocation_types = Ranked(allocationTypes, 40) },
    contentions, thread_pool_events = poolEvents, thread_identity_events = threadEvents,
    native_top_frames_with_jit_mapping = native
};
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Saved {args[1]}: {log.EventCount} events, {sampleCount} thread-stack samples, {log.EventsLost} lost events.");
return 0;

sealed class ThreadSummary(int id)
{
    public int OsThreadId { get; } = id;
    public long Events { get; set; }
    public long Samples { get; set; }
    public long TaskStarts { get; set; }
    public long TaskSchedules { get; set; }
    public double FirstMs { get; set; } = double.PositiveInfinity;
    public double LastMs { get; set; }
    public Dictionary<string, long> SampleTypes { get; } = new();
    public Dictionary<string, long> Categories { get; } = new();
}
sealed record MethodRange(ulong Start, ulong Size, string Name);
