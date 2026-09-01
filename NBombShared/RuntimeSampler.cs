using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>
/// Periodically samples server runtime counters that Windows Performance
/// Monitor cannot observe for a .NET 10 process and writes them to a CSV file.
///
/// <para>
/// PerfMon observes the resource usage the operating system sees of a process
/// (CPU, working set, private bytes, OS thread count, I/O and network). It does not cover the
/// managed runtime: the <c>.NET CLR *</c> counter categories are a .NET Framework feature and
/// .NET Core does not publish data to them. That leaves at least two gaps that matter for this
/// project, these are the garbage collection activity (which payload size is expected to influence) and
/// thread-pool queue depth (which the blocking or async modes are expected to influence).
/// Those two gaps are properties of the system being measured, so this samples the server process only.
/// Anything the operating system already reports is left to PerfMon rather than duplicated here.
/// </para>
///
/// <para>
/// <b>Why a dedicated thread:</b> sampling runs on its own <see cref="Thread"/>
/// rather than on a timer or an <c>async</c> loop. Both of those execute on the thread pool and
/// one of the possible studied condition is a starved thread pool, so a pool scheduled sampler would
/// stall exactly during the window whose measurements matter most.
/// </para>
///
/// <para>
/// <b>Overhead:</b> one sample per second reading static counters is negligible.
/// The cost is the same in all tests so there isn't bias between comparisons, but the absolute values are modified.
/// </para>
/// </summary>
public static class RuntimeSampler
{
    /// <summary>Environment variable holding the destination CSV path. Sampling is off when unset.</summary>
    public const string OutputPathVariable = "NBOMB_SAMPLER_CSV";

    /// <summary>Environment variable overriding the sampling interval in milliseconds.</summary>
    public const string IntervalVariable = "NBOMB_SAMPLER_INTERVAL_MS";

    private const int DefaultIntervalMs = 1000; // Default 1 second

    /// <summary>
    /// Timestamp shape of the time column.
    /// </summary>
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>
    /// Starts sampling only if <see cref="OutputPathVariable"/> is set.
    ///
    /// <para>
    /// Call this once at startup in each server executables.
    /// </para>
    /// </summary>
    public static void StartIfRequested()
    {
        string? outputPath = Environment.GetEnvironmentVariable(OutputPathVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
            return;

        int intervalMs = DefaultIntervalMs;
        string? rawInterval = Environment.GetEnvironmentVariable(IntervalVariable);
        if (!string.IsNullOrWhiteSpace(rawInterval)
            && int.TryParse(rawInterval, out int parsed)
            && parsed > 0)
        {
            intervalMs = parsed;
        }

        Start(outputPath, intervalMs);
    }

    /// <summary>
    /// Starts the sampling loop on a dedicated background thread.
    /// </summary>
    /// <param name="outputPath">CSV file to create or overwrite.</param>
    /// <param name="intervalMs">Milliseconds between samples.</param>
    private static void Start(string outputPath, int intervalMs = DefaultIntervalMs)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Thread thread = new Thread(() => SampleLoop(outputPath, intervalMs))
        {
            // Background so it never keeps the process alive after the host shuts down.
            IsBackground = true,
            Name = "NBombRuntimeSampler",
            // Slightly above normal so a saturated machine still yields regular samples;
            Priority = ThreadPriority.AboveNormal
        };

        thread.Start();
    }

    private static void SampleLoop(string outputPath, int intervalMs)
    {
        try
        {
            using StreamWriter writer = new StreamWriter(outputPath, append: false, Encoding.UTF8)
            {
                // Flush every sample because a test that is killed at the end of its window must still
                // leave a complete file behind.
                AutoFlush = true
            };

            writer.WriteLine(string.Join(',',
                // Local time only.
                "timestamp_local",
                "uptime_ms",
                "threadpool_thread_count",
                "threadpool_queue_length",
                "threadpool_completed_items",
                "gc_gen0_count",
                "gc_gen1_count",
                "gc_gen2_count",
                "gc_total_allocated_bytes",
                "gc_heap_bytes",
                "gc_pause_time_percentage"));

            Stopwatch uptime = Stopwatch.StartNew();

            // Absolute time limits rather than a plain Sleep at the end of the body, this way the time
            // spent formatting and flushing does not accumulate into a drift over a long run.
            long nextDeadline = Environment.TickCount64;

            while (true)
            {
                GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();

                writer.WriteLine(string.Join(',',
                    DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                    uptime.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                    
                    ThreadPool.ThreadCount.ToString(CultureInfo.InvariantCulture),
                    ThreadPool.PendingWorkItemCount.ToString(CultureInfo.InvariantCulture),
                    ThreadPool.CompletedWorkItemCount.ToString(CultureInfo.InvariantCulture),

                    // Cumulative counters: differentiate between consecutive samples to obtain
                    // rates. Kept cumulative here so a missed sample doesn't lose events.
                    GC.CollectionCount(0).ToString(CultureInfo.InvariantCulture),
                    GC.CollectionCount(1).ToString(CultureInfo.InvariantCulture),
                    GC.CollectionCount(2).ToString(CultureInfo.InvariantCulture),
                    GC.GetTotalAllocatedBytes(precise: false).ToString(CultureInfo.InvariantCulture),

                    // Read from GCMemoryInfo rather than GC.GetTotalMemory(false) which can
                    // itself perturb what it measures. Note this is the heap size as of the last
                    // collection and not a snapshot.
                    gcInfo.HeapSizeBytes.ToString(CultureInfo.InvariantCulture),

                    // This value must be read as an absolute and not as a rate. It is a running figure
                    // maintained by the runtime, so it must never be differentiated
                    // the way the counters above are.
                    gcInfo.PauseTimePercentage.ToString(CultureInfo.InvariantCulture)));

                nextDeadline += intervalMs;
                long remaining = nextDeadline - Environment.TickCount64;

                if (remaining > 0)
                    Thread.Sleep((int)remaining);
                else
                    // Fell behind by more than a whole interval, which on a saturated machine is
                    // possible. Resynchronization is the best option.
                    nextDeadline = Environment.TickCount64;
            }
        }
        catch (Exception ex)
        {
            // Never take the host process down because instrumentation failed. A lost counter
            // file is recoverable while a crashed test is not.
            Console.Error.WriteLine($"[RuntimeSampler] stopped: {ex.Message}");
        }
    }
}