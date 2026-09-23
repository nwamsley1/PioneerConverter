using System.Runtime.ExceptionServices;

// One coordinating owner calls Run and Dispose, normally the current file's producer.
// Each index keeps its own dedicated thread for the team's lifetime, so SDK readers
// remain exclusive to their worker without relying on ThreadPool scheduling.
internal sealed class ScanWorkerTeam : IDisposable
{
    private const int Idle = 0, Running = 1, Disposing = 2, Disposed = 3;
    private readonly object gate = new();
    private readonly Thread[] threads;
    private int startedCount;
    private int operation;
    private Action<int>? work;
    private ExceptionDispatchInfo? failure;
    private long generation;
    private int pending;
    private bool stopped;

    public ScanWorkerTeam(int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        threads = new Thread[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                int index = i;
                threads[i] = new Thread(() => Worker(index))
                {
                    IsBackground = true,
                    Name = $"RAW scan {i}"
                };
                threads[i].Start();
                // No allocation may separate successful Start from tracking that thread.
                startedCount++;
            }
        }
        catch
        {
            Dispose(); // Join only threads whose Start succeeded.
            throw;
        }
    }

    // Returns only after every worker has finished this generation, including failures.
    // Cancellation belongs to the action; it must check the file's token between scans.
    public void Run(Action<int> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        int previous = Interlocked.CompareExchange(ref operation, Running, Idle);
        if (previous != Idle)
        {
            if (previous is Disposing or Disposed)
                throw new ObjectDisposedException(nameof(ScanWorkerTeam));
            throw new InvalidOperationException("ScanWorkerTeam.Run requires a single coordinating owner.");
        }

        try
        {
            lock (gate)
            {
                work = action;
                failure = null;
                pending = startedCount;
                generation++;
                Monitor.PulseAll(gate);
                while (pending != 0) Monitor.Wait(gate);
                work = null;
                failure?.Throw();
            }
        }
        finally { Volatile.Write(ref operation, Idle); }
    }

    private void Worker(int index)
    {
        long seen = 0;
        while (true)
        {
            Action<int> action;
            lock (gate)
            {
                while (!stopped && generation == seen) Monitor.Wait(gate);
                if (stopped) return;
                seen = generation;
                action = work!;
            }
            try { action(index); }
            catch (Exception ex)
            {
                lock (gate) failure ??= ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                lock (gate)
                {
                    pending--;
                    if (pending == 0) Monitor.PulseAll(gate);
                }
            }
        }
    }

    public void Dispose()
    {
        int previous = Interlocked.CompareExchange(ref operation, Disposing, Idle);
        if (previous == Disposed) return;
        if (previous != Idle)
            throw new InvalidOperationException("ScanWorkerTeam.Dispose cannot overlap Run or another Dispose call.");

        try
        {
            lock (gate) { stopped = true; Monitor.PulseAll(gate); }
            for (int i = 0; i < startedCount; i++) threads[i].Join();
        }
        finally { Volatile.Write(ref operation, Disposed); }
    }
}
