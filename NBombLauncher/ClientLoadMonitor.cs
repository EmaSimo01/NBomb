using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace NBombLauncher;

/// <summary>
/// Measures the load under which the load generator itself worked during a test and writes the data into
/// <c>run-info.txt</c>.
///
/// <para>
/// <b>Why this exists.</b> Under an open workload model a throughput plateau is the result the
/// saturation experiment is looking for, but a plateau caused by the <i>client</i> running out of
/// CPU looks the same in an analysis report: the achieved rate stops following the requested one
/// and latency climbs, with nothing else in the system that can differentiate between those two.
/// Without these data a limit cannot be attributed to the server.
/// </para>
///
/// <para>
/// <b>Why it is not the shared <c>RuntimeSampler</c>.</b> That sampler covers the two gaps
/// PerfMon leaves on a .NET process under test and it is scoped for the server only. This component analyzes
/// the generator instead.
/// </para>
///
/// <para>
/// <b>Why a summary and not a CSV.</b> A test produces five files and none of them is a
/// client time series. The question this answers is: was the generator the
/// limit? A mean, a peak and a threshold are enough data to answer the question.
/// Reported next to the link probe in the file that already records whether a test is trustworthy.
/// </para>
/// </summary>
public sealed class ClientLoadMonitor
{
    /// <summary>
    /// Milliseconds between samples. It matches the PerfMon
    /// interval and the runtime sampler's default.
    /// </summary>
    private const int IntervalMs = 1000;

    /// <summary>
    /// Peak CPU above which the test is flagged. It is not a hard limit and not a failure: past this
    /// point the generator is close enough to its own limit that a plateau in the same test
    /// cannot be attributed to the server without further inspection.
    /// </summary>
    private const double WarningThresholdPercent = 80.0;

    /// <summary>
    /// Logical processors this process may actually run on, which is the divisor every percentage
    /// below uses. With <c>--affinity</c> applied this is the number of bits in the mask, not the
    /// machine's processor count: on the loopback benchmark the client is given half the CPUs and
    /// dividing by the other half's cores too would report a saturated client as half loaded.
    /// </summary>
    private readonly int _availableCpus;

    private readonly Thread _thread;

    // Signaled to end the loop. Doubles as the sleep between samples so stopping is immediate
    // instead of waiting out the remaining time of an interval.
    private readonly ManualResetEventSlim _stopRequested = new ManualResetEventSlim(false);

    // Written by the sampling thread and read by the caller only after Join(), which is what
    // publishes them safely without an Interlocked on every field.
    private double _peakCpuPercent;
    private int _peakThreadCount;
    private long _peakQueueLength;
    private TimeSpan _consumedCpu;
    private TimeSpan _window;
    private int _samples;

    private ClientLoadMonitor(int availableCpus)
    {
        _availableCpus = availableCpus;

        _thread = new Thread(SampleLoop)
        {
            // Background so an aborted test run cannot leave this thread holding the process alive.
            IsBackground = true,
            Name = "NBombClientLoadMonitor",

            // Above normal for the same reason the server's sampler is: the moment worth
            // sampling is the moment the machine is at its limit, so a normal-priority
            // sampler stops producing samples when needed. The thread sleeps between samples
            // so the priority costs nothing.
            Priority = ThreadPriority.AboveNormal
        };
    }

    /// <summary>
    /// Starts sampling on its own thread.
    /// </summary>
    /// <param name="affinityMask">
    /// The mask applied to this process or 0 when standard affinity applies. Only used to calculate
    /// how many processors the percentages should be divided by.
    /// </param>
    public static ClientLoadMonitor Start(long affinityMask)
    {
        ClientLoadMonitor monitor = new ClientLoadMonitor(CountAvailableCpus(affinityMask));
        monitor._thread.Start();
        return monitor;
    }

    /// <summary>
    /// Ends the measurement window and renders it as aligned lines ready to be written in <c>run-info.txt</c>.
    /// </summary>
    /// <param name="indent">Prefix applied to every line, matching the rest of the file.</param>
    public string StopAndDescribe(string indent = "  ")
    {
        _stopRequested.Set();

        // Instrumentation must never be the reason a finished test run fails to write its
        // report. The thread is in background mode so a wait that expires leaves nothing suspended.
        _thread.Join(TimeSpan.FromSeconds(5));
        _stopRequested.Dispose();

        StringBuilder description = new StringBuilder();

        if (_samples == 0 || _window <= TimeSpan.Zero)
        {
            description.AppendLine($"{indent}Client CPU     : not measured, the monitor produced no sample");
            return description.ToString();
        }

        double meanPercent = _consumedCpu.TotalSeconds / _window.TotalSeconds / _availableCpus * 100.0;
        description.AppendLine(
            $"{indent}Client CPU     : mean {meanPercent.ToString("N1", CultureInfo.InvariantCulture)}%, "
          + $"peak {_peakCpuPercent.ToString("N1", CultureInfo.InvariantCulture)}% "
          + $"of {_availableCpus} logical CPU{(_availableCpus == 1 ? string.Empty : "s")} "
          + $"({_samples} samples at {IntervalMs / 1000} s)");

        description.AppendLine(
            $"{indent}Client threads : thread pool peak {_peakThreadCount} threads, peak queue {_peakQueueLength}");

        // Printed only when needed.
        if (_peakCpuPercent >= WarningThresholdPercent)
        {
            description.AppendLine(
                $"{indent}Client warning : peak CPU above {WarningThresholdPercent:F0}% of the cores available to the client.");
            description.AppendLine(
                $"{indent} A throughput plateau in this run may be the load generator, not the server.");
        }

        return description.ToString();
    }

    /// <summary>
    /// Samples until stopped, keeping running aggregates in memory.
    ///
    /// <para>
    /// Nothing is written to disk while the test is in progress because a file handle flushed once a
    /// second inside the load generator is not cost free.
    /// </para>
    /// </summary>
    private void SampleLoop()
    {
        try
        {
            using Process self = Process.GetCurrentProcess();

            Stopwatch window = Stopwatch.StartNew();
            TimeSpan startCpu = self.TotalProcessorTime;

            TimeSpan previousCpu = startCpu;
            TimeSpan previousElapsed = TimeSpan.Zero;

            // Wait returns true only when the stop is signaled, covers both the sleep and
            // the loop condition.
            while (!_stopRequested.Wait(IntervalMs))
                Observe(self, window, ref previousCpu, ref previousElapsed);

            // Final call so that a test shorter than a single interval still reports thread
            // pool figures instead of nothing.
            Observe(self, window, ref previousCpu, ref previousElapsed);

            window.Stop();
            self.Refresh();

            // The mean comes from the two data to be exact over the whole window even if a sample
            // was late or skipped.
            _window = window.Elapsed;
            _consumedCpu = self.TotalProcessorTime - startCpu;
        }
        catch (Exception ex)
        {
            // Never take a completed test down because its instrumentation failed.
            Console.Error.WriteLine($"[client-load] stopped: {ex.Message}");
        }
    }

    /// <summary>
    /// Records the CPU consumed since the previous sample and the two thread-pool
    /// depths as they are at the moment.
    /// </summary>
    private void Observe(Process self, Stopwatch window, ref TimeSpan previousCpu, ref TimeSpan previousElapsed)
    {
        self.Refresh();

        TimeSpan cpu = self.TotalProcessorTime;
        TimeSpan elapsed = window.Elapsed;
        double intervalSeconds = (elapsed - previousElapsed).TotalSeconds;

        // Half an interval is the lower limit. The last observation of a test is a fraction of a
        // second after the previous one and a CPU ratio over a slice that short is dominated by
        // where the sample happened to fall rather than by how busy the process was.
        if (intervalSeconds >= IntervalMs / 2000.0)
        {
            double percent = (cpu - previousCpu).TotalSeconds / intervalSeconds / _availableCpus * 100.0;
            if (percent > _peakCpuPercent)
                _peakCpuPercent = percent;
        }

        previousCpu = cpu;
        previousElapsed = elapsed;
        
        int threads = ThreadPool.ThreadCount;
        if (threads > _peakThreadCount)
            _peakThreadCount = threads;

        long queue = ThreadPool.PendingWorkItemCount;
        if (queue > _peakQueueLength)
            _peakQueueLength = queue;

        _samples++;
    }

    /// <summary>
    /// Number of logical processors the percentages are divided by.
    ///
    /// <para>
    /// Calculated from the mask that was applied rather than read from
    /// <see cref="Environment.ProcessorCount"/>, whose value is cached on first access.
    /// </para>
    /// </summary>
    private static int CountAvailableCpus(long affinityMask)
    {
        if (affinityMask == 0)
            return Environment.ProcessorCount;

        int count = BitOperations.PopCount((ulong)affinityMask);

        // A mask is rejected at parse time unless it is greater than zero so this cannot be
        // reached; it is here so that a future change to the validation method cannot introduce a
        // division by zero into the number that decides whether a test is trustworthy.
        return count > 0 ? count : Environment.ProcessorCount;
    }
}