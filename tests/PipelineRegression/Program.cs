using System.Collections.Concurrent;

var tests = new (string Name, Action Run)[]
{
    ("ordered drain and exactly-once disposal", OrderedDrain),
    ("byte reservation includes consumer lease", ByteReservation),
    ("item count bounds zero-byte entries", ItemReservation),
    ("oversized item remains exclusive", OversizedItem),
    ("cancel blocked producer and retain caller ownership", CancelProducer),
    ("cancel blocked consumer", CancelConsumer),
    ("complete wakes blocked producer and consumer", CompleteWaiters),
    ("dispose abandoned queue entries", DisposeAbandoned),
    ("writer failure cancels all pipeline stages without hangs", () => StageFailure("writer")),
    ("extractor failure cancels downstream stages", () => StageFailure("extractor")),
    ("assembler failure cancels producer and consumer", () => StageFailure("assembler")),
    ("worker identities remain exclusive across generations", WorkerIdentity),
    ("worker failure drains before propagation and permits recovery", WorkerFailure),
    ("worker shutdown joins threads and rejects later work", WorkerShutdown),
    ("concurrent worker-team ownership is rejected without hanging", WorkerOwnership),
    ("invalid worker-team arguments are rejected", WorkerArguments)
};
foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}
return 0;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Join(Task task)
{
    if (!SpinWait.SpinUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5))) throw new Exception("Task hung");
    task.GetAwaiter().GetResult();
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static Task StartBlocked(Action action)
{
    var entered = new ManualResetEventSlim();
    var task = Task.Run(() => { entered.Set(); action(); });
    Check(entered.Wait(TimeSpan.FromSeconds(5)), "Task failed to start");
    Check(!task.Wait(100), "Operation should remain blocked");
    return task;
}
static void OrderedDrain()
{
    var disposed = new List<int>();
    using var queue = new BoundedPipelineQueue<int>(3, 30, CancellationToken.None, disposed.Add);
    queue.Add(1, 10); queue.Add(2, 10); queue.Add(3, 10); queue.Complete();
    for (int n = 1; n <= 3; n++)
    {
        var lease = queue.Read()!;
        Check(lease.Value == n, "FIFO violated");
        lease.Dispose(); lease.Dispose();
    }
    Check(queue.Read() == null, "Completed queue did not end");
    Check(disposed.SequenceEqual(new[] { 1, 2, 3 }), "Lease released multiple times");
    Check(queue.PeakBytes == 30, "Incorrect peak accounting");
    Throws<InvalidOperationException>(() => queue.Add(4, 1));
}
static void ByteReservation()
{
    using var queue = new BoundedPipelineQueue<int>(10, 10, CancellationToken.None);
    queue.Add(1, 8);
    var lease = queue.Read()!;
    var producer = StartBlocked(() => queue.Add(2, 3));
    lease.Dispose(); Join(producer);
    Check(queue.PeakBytes == 8, "A consumer-held item escaped the byte limit");
    queue.Read()!.Dispose();
}
static void ItemReservation()
{
    using var queue = new BoundedPipelineQueue<int>(1, 10, CancellationToken.None);
    queue.Add(1, 0);
    var lease = queue.Read()!;
    var producer = StartBlocked(() => queue.Add(2, 0));
    lease.Dispose(); Join(producer); queue.Read()!.Dispose();
}
static void OversizedItem()
{
    using var queue = new BoundedPipelineQueue<int>(10, 10, CancellationToken.None);
    queue.Add(1, 20);
    var lease = queue.Read()!;
    var producer = StartBlocked(() => queue.Add(2, 1));
    Check(queue.PeakBytes == 20, "Oversized scan rejected or miscounted");
    lease.Dispose(); Join(producer); queue.Read()!.Dispose();
    Check(queue.PeakBytes == 20, "Oversized scan shared its reservation");
}
static void CancelProducer()
{
    using var cancellation = new CancellationTokenSource();
    var disposed = new List<int>();
    using var queue = new BoundedPipelineQueue<int>(1, 10, cancellation.Token, disposed.Add);
    queue.Add(1, 10);
    var producer = StartBlocked(() => Throws<OperationCanceledException>(() => queue.Add(2, 1)));
    cancellation.Cancel(); Join(producer); queue.Dispose();
    Check(disposed.SequenceEqual(new[] { 1 }), "Failed Add transferred ownership");
}
static void CancelConsumer()
{
    using var cancellation = new CancellationTokenSource();
    using var queue = new BoundedPipelineQueue<int>(1, 10, cancellation.Token);
    var consumer = StartBlocked(() => Throws<OperationCanceledException>(() => queue.Read()));
    cancellation.Cancel(); Join(consumer);
}
static void CompleteWaiters()
{
    using var full = new BoundedPipelineQueue<int>(1, 10, CancellationToken.None);
    full.Add(1, 10);
    var producer = StartBlocked(() => Throws<InvalidOperationException>(() => full.Add(2, 1)));
    full.Complete(); Join(producer);
    using var empty = new BoundedPipelineQueue<int>(1, 10, CancellationToken.None);
    var consumer = StartBlocked(() => Check(empty.Read() == null, "Completed empty queue returned data"));
    empty.Complete(); Join(consumer);
}
static void DisposeAbandoned()
{
    var disposed = new List<int>();
    var queue = new BoundedPipelineQueue<int>(2, 10, CancellationToken.None, disposed.Add);
    queue.Add(1, 5); queue.Add(2, 5);
    using var held = queue.Read()!;
    queue.Dispose();
    Check(disposed.SequenceEqual(new[] { 2 }), "Queue disposed consumer-owned entry");
    held.Dispose(); queue.Dispose();
    Check(disposed.SequenceEqual(new[] { 2, 1 }), "Abandoned item leaked or double-disposed");
}
static void StageFailure(string failedStage)
{
    using var cancellation = new CancellationTokenSource();
    var disposed = new ConcurrentBag<int>();
    using var decoded = new BoundedPipelineQueue<int>(1, 10, cancellation.Token, disposed.Add);
    using var batches = new BoundedPipelineQueue<int>(1, 10, cancellation.Token);
    Exception? failure = null;
    void Stage(Action action)
    {
        try { action(); }
        catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); cancellation.Cancel(); }
    }
    var producer = Task.Run(() => Stage(() =>
    {
        for (int i = 0; i < 1000; i++)
        {
            if (failedStage == "extractor" && i == 3) throw new IOException("Injected extractor failure");
            decoded.Add(i, 10);
        }
        decoded.Complete();
    }));
    var assembler = Task.Run(() => Stage(() =>
    {
        while (decoded.Read() is { } item)
        {
            using (item)
            {
                if (failedStage == "assembler" && item.Value == 3) throw new IOException("Injected assembler failure");
                batches.Add(item.Value, 10);
            }
        }
        batches.Complete();
    }));
    Stage(() =>
    {
        while (batches.Read() is { } item)
        {
            using (item)
            {
                if (failedStage == "writer") throw new IOException("Injected writer failure");
            }
        }
    });
    Join(producer); Join(assembler);
    Check(failure is IOException, "Original stage failure lost");
    decoded.Dispose(); batches.Dispose();
    Check(disposed.Distinct().Count() == disposed.Count, "Failed stage double-disposed entries");
}

static void WorkerIdentity()
{
    foreach (int count in new[] { 2, 3, 16, 32 })
    {
        using var team = new ScanWorkerTeam(count);
        var ids = new int[count];
        var calls = new int[count];
        var active = new int[count];
        for (int generation = 0; generation < 25; generation++)
        {
            team.Run(i =>
            {
                Check(Interlocked.Increment(ref active[i]) == 1, "Worker accessor used concurrently");
                int id = Environment.CurrentManagedThreadId;
                if (ids[i] == 0) ids[i] = id;
                Check(ids[i] == id, "Worker moved to another thread");
                Interlocked.Increment(ref calls[i]);
                Thread.SpinWait(20);
                Interlocked.Decrement(ref active[i]);
            });
            Check(calls.All(n => n == generation + 1), "Worker generation skipped or duplicated");
            Check(active.All(n => n == 0), "Run returned with an active worker");
        }
        Check(ids.Distinct().Count() == count, "Worker indices share a thread");
    }
}

static void WorkerFailure()
{
    using var team = new ScanWorkerTeam(3);
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var run = Task.Run(() =>
    {
        try
        {
            team.Run(i =>
            {
                if (i == 0) ThrowWorkerSentinel();
                if (i == 1) { entered.Set(); release.Wait(); }
            });
            throw new Exception("Worker failure was swallowed");
        }
        catch (IOException ex)
        {
            Check(ex.Message == "worker sentinel", "Original worker failure lost");
            Check(ex.StackTrace!.Contains(nameof(ThrowWorkerSentinel)), "Worker stack trace lost");
        }
    });
    try
    {
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "Worker failed to start");
        Check(!run.Wait(50), "Failure returned before the other workers drained");
    }
    finally { release.Set(); }
    Join(run);
    int completed = 0;
    team.Run(_ => Interlocked.Increment(ref completed));
    Check(completed == 3, "Worker failure prevented the next generation");
}

static void ThrowWorkerSentinel() => throw new IOException("worker sentinel");

static void WorkerShutdown()
{
    var team = new ScanWorkerTeam(4);
    var threads = new Thread[4];
    team.Run(i => threads[i] = Thread.CurrentThread);
    team.Dispose();
    team.Dispose();
    Check(threads.All(t => !t.IsAlive), "Disposal left worker threads alive");
    Throws<ObjectDisposedException>(() => team.Run(_ => { }));
}

static void WorkerOwnership()
{
    using var team = new ScanWorkerTeam(2);
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var run = Task.Run(() => team.Run(_ => { entered.Set(); release.Wait(); }));
    try
    {
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "Worker failed to start");
        Throws<InvalidOperationException>(() => team.Run(_ => { }));
        Throws<InvalidOperationException>(() => team.Dispose());
    }
    finally { release.Set(); }
    Join(run);
    team.Run(_ => { }); // Rejected misuse must not corrupt team state.
}

static void WorkerArguments()
{
    Throws<ArgumentOutOfRangeException>(() => new ScanWorkerTeam(0));
    Throws<ArgumentOutOfRangeException>(() => new ScanWorkerTeam(-1));
    using var team = new ScanWorkerTeam(1);
    Throws<ArgumentNullException>(() => team.Run(null!));
    team.Run(_ => { });
}
