using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Selects which kind of work the server simulates per request.
///
/// <para>
/// The two kinds are logically connected to <see cref="ExecutionMode"/>. Crossing them
/// yields four different handler strategies: CPU work run inline, CPU work
/// offloaded to the thread pool, I/O latency awaited
/// while blocking a pool thread and I/O latency awaited without holding one.
/// </para>
/// </summary>
public enum WorkloadKind
{
    /// <summary>CPU-bound: a chain of SHA-256 iterations scaled by <see cref="WorkIntensity"/>.</summary>
    Cpu = 0,

    /// <summary>I/O-bound: a simulated call scaled by <see cref="WorkIntensity"/>.</summary>
    IO = 1
}

/// <summary>
/// Selects how the server executes the work selected by <see cref="WorkloadKind"/>.
/// </summary>
public enum ExecutionMode
{
    /// <summary>
    /// Runs the work synchronously holding the calling thread for its full duration.
    /// For <see cref="WorkloadKind.IO"/> this blocks a thread-pool thread on <see cref="Thread.Sleep(int)"/>.
    /// </summary>
    Blocking = 0,

    /// <summary>
    /// Runs the work asynchronously. For <see cref="WorkloadKind.Cpu"/> this offloads to the
    /// thread pool via <see cref="Task.Run(Action)"/>, in which case there is no
    /// concurrency gain since the handler already runs on a pool thread. For
    /// <see cref="WorkloadKind.IO"/> it awaits <see cref="Task.Delay(int)"/>, releasing the thread.
    /// </summary>
    Async = 1
}

/// <summary>
/// Represents the intensity of the work the server performs per request. The unit depends on
/// <see cref="WorkloadKind"/>: SHA-256 iterations for <see cref="WorkloadKind.Cpu"/>
/// (see <see cref="OperationsHandler.CpuIterations"/>) or simulated latency in milliseconds for
/// <see cref="WorkloadKind.IO"/> (see <see cref="OperationsHandler.IoDelaysMs"/>).
///
/// <para>
/// The CPU levels are calibrated. At <see cref="Low"/> the protocol is
/// still the dominant factor while at <see cref="Extreme"/> it is irrelevant. Run <c>--calibrate</c> on the server
/// machine to obtain the specific milliseconds: the iteration counts are hardware specific.
/// </para>
/// </summary>
public enum WorkIntensity
{
    /// <summary>No workload.</summary>
    Null = 0,

    /// <summary>Minimal workload.</summary>
    Low = 1,

    /// <summary>Moderate workload.</summary>
    Medium = 2,

    /// <summary>High workload.</summary>
    High = 3,

    /// <summary>Extreme workload.</summary>
    Extreme = 4
}

/// <summary>
/// Represents the byte size of the ASCII payload returned to the caller.
/// Each level maps to a fixed character count
/// (see <see cref="OperationsHandler.PayloadSizes"/>).
/// </summary>
public enum PayloadSize
{
    /// <summary>0 byte. </summary>
    Null = 0,

    /// <summary>1 000 bytes (~1 KB).</summary>
    Small = 1,

    /// <summary>10 000 bytes (~10 KB).</summary>
    Medium = 2,

    /// <summary>100 000 bytes (~100 KB).</summary>
    Large = 3,

    /// <summary>1 000 000 bytes (~1 MB).</summary>
    Extreme = 4
}

/// <summary>
/// Generates deterministic workloads and byte payloads shared by all server implementations.
///
/// <para>
/// <b>Workload model:</b> described by <see cref="WorkloadKind"/> (CPU vs I/O) and
/// <see cref="ExecutionMode"/> (blocking vs async), scaled by <see cref="WorkIntensity"/>.
/// Response bytes are produced identically regardless of the selection.
/// </para>
///
/// <para>
/// The response payload is built once at type initialization and depends only on <see cref="PayloadSize"/>;
/// the CPU workload publishes its digest into a static sink and never influence the response. Changing
/// <c>--intensity</c> therefore cannot change the response bytes and changing
/// <c>--payload</c> cannot change how much work the server does.
/// </para>
///
/// <para>
/// The payload is cached because building it per request cost microseconds of CPU at
/// <see cref="PayloadSize.Extreme"/> and, being over the 85 000 byte LOH threshold,
/// trigger a gen2 collection every few requests. That cost is the same for all four
/// protocols but it would compress the differences under measurement and inflated the latency with GC variance.
/// Using one shared readonly array is also closer to an optimized realistic system design.
/// </para>
///
/// <para>
/// The array returned by <see cref="ExecuteBlocking"/> and
/// <see cref="ExecuteAsync"/> is shared and must be treated as readonly. All three servers
/// only read it: <c>Results.Bytes</c> writes it to the response, <c>WebSocket.SendAsync</c>
/// copies it into a frame and <c>ByteString.CopyFrom</c> copies it into the Protobuf message.
/// Altering the array would corrupt every subsequent response in the
/// process which is exactly what the WebSocket and gRPC readers rely on not happening.
/// </para>
/// </summary>
public static class OperationsHandler
{
    /// <summary>
    /// Number of consecutive SHA-256 iterations per <see cref="WorkIntensity"/> level,
    /// used when <see cref="WorkloadKind.Cpu"/> is selected.
    /// Indexed by the integer value of the enum member.
    ///
    /// <para>
    /// The following values are calibrated on a Ryzen 9 7950X. Measure again with <see cref="Calibrate"/> on any
    /// other machine and change the values here before using the system.
    /// </para>
    /// </summary>
    public static readonly int[] CpuIterations =
    [
        0,           // Null
        2000,        // Low       (~0,24 ms)
        10000,       // Medium    (~1,2 ms)
        50000,       // High      (~6 ms)
        200000       // Extreme   (~24 ms)
    ];

    /// <summary>
    /// Milliseconds of simulated downstream latency per <see cref="WorkIntensity"/> level,
    /// used when <see cref="WorkloadKind.IO"/> is selected.
    /// Indexed by the integer value of the enum member.
    /// </summary>
    public static readonly int[] IoDelaysMs =
    [
        0,      // Null
        16,     // Low     (Windows lowest possible value)
        50,     // Medium
        250,    // High
        1000    // Extreme
    ];

    /// <summary>
    /// Output payload size in bytes per <see cref="PayloadSize"/> level.
    /// Indexed by the integer value of the enum member.
    /// </summary>
    public static readonly int[] PayloadSizes =
    [
        0,           // Null    (0 KB)
        1000,        // Small   (~1 KB)
        10000,       // Medium  (~10 KB)
        100000,      // Large   (~100 KB)
        1000000      // Extreme (~1 MB)
    ];

    /// <summary>
    /// UTF-8 encoding of the fixed seed string used as the initial SHA-256 input.
    /// A constant seed guarantees reproducible payloads across tests.
    /// </summary>
    private static readonly byte[] SeedBytes = Encoding.UTF8.GetBytes("NBomberTestLoad");

    /// <summary>
    /// One immutable response body per <see cref="PayloadSize"/> level built once when the type
    /// is initialized.
    /// </summary>
    private static readonly byte[][] PayloadCache = BuildPayloadCache();

    /// <summary>
    /// Receives one byte of every completed hash chain.
    ///
    /// <para>
    /// The chain doesn't feed the response, so without an observable effect a compiler
    /// could treat it as unused code and <c>--intensity</c> would become
    /// cheaper than expected. Publishing through <see cref="Volatile.Write(ref byte, byte)"/>
    /// makes the write unremovable while costing a single store per request.
    /// </para>
    /// </summary>
    private static byte _workSink;

    /// <summary>
    /// Executes the requested work synchronously and returns the response payload.
    ///
    /// <para>
    /// This overload holds the calling thread for the entire duration of the work. Under
    /// <see cref="WorkloadKind.IO"/> it blocks a thread-pool thread on <see cref="Thread.Sleep(int)"/>,
    /// it can be observed in <c>threadpool-thread-count</c> and <c>threadpool-queue-length</c> counters.
    /// </para>
    /// </summary>
    /// <param name="payloadSize">Requested response size.</param>
    /// <param name="intensity">Requested work intensity.</param>
    /// <param name="kind">Requested kind of work.</param>
    /// <returns>
    /// The shared readonly response body for <paramref name="payloadSize"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any argument is not a member of its enum type.
    /// </exception>
    public static byte[] ExecuteBlocking(PayloadSize payloadSize, WorkIntensity intensity, WorkloadKind kind)
    {
        Validate(payloadSize, intensity, kind);

        if (kind == WorkloadKind.IO)
        {
            int delayMs = IoDelaysMs[(int)intensity];
            if (delayMs > 0)
                Thread.Sleep(delayMs);
        }
        else
        {
            ConsumeCpu(intensity);
        }

        return PayloadCache[(int)payloadSize];
    }

    /// <summary>
    /// Executes the requested work asynchronously and returns the response payload.
    ///
    /// <para>
    /// Under <see cref="WorkloadKind.IO"/> this awaits <see cref="Task.Delay(int)"/>, releasing the
    /// thread for the duration.
    /// </para>
    ///
    /// <para>
    /// Under <see cref="WorkloadKind.Cpu"/> this offloads to <see cref="Task.Run(Action)"/>.
    /// An ASP.NET Core handler already runs
    /// on a thread-pool thread so the offload moves CPU work from one pool thread to another,
    /// adding a queue hop, a task allocation and a context switch without adding any concurrency.
    /// </para>
    /// </summary>
    /// <param name="payloadSize">Requested response size.</param>
    /// <param name="intensity">Requested work intensity.</param>
    /// <param name="kind">Requested kind of work.</param>
    /// <returns>
    /// A task completing with the shared readonly response body for <paramref name="payloadSize"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any argument is not a member of its enum type.
    /// </exception>
    public static async Task<byte[]> ExecuteAsync(PayloadSize payloadSize, WorkIntensity intensity, WorkloadKind kind)
    {
        Validate(payloadSize, intensity, kind);

        if (kind == WorkloadKind.IO)
        {
            int delayMs = IoDelaysMs[(int)intensity];
            if (delayMs > 0)
                await Task.Delay(delayMs);
        }
        else
        {
            await Task.Run(() => ConsumeCpu(intensity));
        }

        return PayloadCache[(int)payloadSize];
    }

    // CORE LOGIC

    /// <summary>
    /// Uses the CPU for the number of SHA-256 rounds indicated by <paramref name="intensity"/>,
    /// then publishes one byte of the results so the chain cannot be optimized.
    /// </summary>
    /// <param name="intensity">Workload level controlling the iteration count.</param>
    /// <remarks>
    /// <para>
    /// Each digest in the chain is the input of the next round, otherwise an optimizer could be
    /// free to optimize all rounds except the last, making <c>--intensity</c> cheaper than
    /// expected.
    /// </para>
    /// <para>
    /// Uses the static <see cref="SHA256.HashData(ReadOnlySpan{byte}, Span{byte})"/> overload and
    /// two stack allocated 32 byte buffers alternated as source and destination, so the loop
    /// does not allocate anything on the heap.
    /// </para>
    /// </remarks>
    private static void ConsumeCpu(WorkIntensity intensity)
    {
        int iterations = CpuIterations[(int)intensity];
        if (iterations == 0)
            return;

        // SHA-256 always produces 32 bytes. Two stack allocated buffers
        // are needed to avoid heap allocations.
        Span<byte> bufferA = stackalloc byte[32];
        Span<byte> bufferB = stackalloc byte[32];

        // First iteration: hash the UTF-8 seed and save in bufferA.
        SHA256.HashData(SeedBytes, bufferA);
        for (int i = 1; i < iterations; i++)
        {
            if (i % 2 == 1)
                SHA256.HashData(bufferA, bufferB);
            else
                SHA256.HashData(bufferB, bufferA);
        }

        // The result is stored in bufferA when the total iteration count is
        // odd and in bufferB when even.
        Span<byte> result = iterations % 2 == 1 ? bufferA : bufferB;
        Volatile.Write(ref _workSink, result[0]);
    }

    /// <summary>
    /// Builds the immutable response body of every <see cref="PayloadSize"/> level by tiling the
    /// hex form of a single fixed digest.
    ///
    /// <para>
    /// The digest is one round over <see cref="SeedBytes"/> and is not derived from
    /// <see cref="WorkIntensity"/>. Every level is therefore identical across tests, across protocols and
    /// across intensities.
    /// </para>
    /// </summary>
    private static byte[][] BuildPayloadCache()
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(SeedBytes, digest);

        // Convert the 32 bytes digest to its 64 character hex representation
        // and encode as ASCII bytes for tiling into the output buffers.
        byte[] hexBytes = Encoding.ASCII.GetBytes(Convert.ToHexString(digest));
        byte[][] cache = new byte[PayloadSizes.Length][];
        for (int level = 0; level < PayloadSizes.Length; level++)
        {
            int size = PayloadSizes[level];
            byte[] payload = new byte[size];
            int pos = 0;
            while (pos < size)
            {
                int copy = Math.Min(hexBytes.Length, size - pos);
                hexBytes.AsSpan(0, copy).CopyTo(payload.AsSpan(pos));
                pos += copy;
            }
            cache[level] = payload;
        }
        return cache;
    }

    // CALIBRATION

    /// <summary>
    /// Times every <see cref="WorkIntensity"/> level on the current machine and returns a report.
    ///
    /// <para>
    /// The iteration counts in <see cref="CpuIterations"/> are hardware specific: the same value
    /// is a different time on a different CPU.
    /// </para>
    ///
    /// <para>
    /// The single core figure is measured; the machine limit is an estimate that assumes
    /// two-way SMT contributing about 1,25x per physical core, hence a factor of
    /// <c>ProcessorCount * 0,625</c>. Should be treated as an estimation of the order of magnitude.
    /// </para>
    /// </summary>
    /// <returns>An aligned text block ready to be printed or pasted into a file.</returns>
    public static string Calibrate()
    {
        // Warm the JIT and the hashing path so tier-0 code is not what is timed.
        for (int i = 0; i < 5; i++)
            ConsumeCpu(WorkIntensity.Medium);

        double effectiveCores = Environment.ProcessorCount * 0.625;

        StringBuilder report = new StringBuilder();
        report.AppendLine("Calibrazione del carico CPU");
        report.AppendLine($"  Macchina           : {Environment.MachineName}");
        report.AppendLine($"  Processori logici  : {Environment.ProcessorCount}");
        report.AppendLine($"  Runtime            : .NET {Environment.Version}");
        report.AppendLine($"  Server GC          : {System.Runtime.GCSettings.IsServerGC}");
        report.AppendLine();
        report.AppendLine("  Livello    Iterazioni        ms/op     req/s su 1 core   req/s stimati macchina");
        report.AppendLine("  --------   ------------   ----------   ---------------   ----------------------");

        foreach (WorkIntensity level in Enum.GetValues<WorkIntensity>())
        {
            int iterations = CpuIterations[(int)level];

            // Long levels are slow enough that one sample is already stable; short levels are
            // repeated so the timer granularity does not pollute the measure.
            int repetitions = iterations >= 50000 ? 3 : 50;

            Stopwatch stopwatch = Stopwatch.StartNew();
            for (int r = 0; r < repetitions; r++)
                ConsumeCpu(level);
            stopwatch.Stop();

            double milliseconds = stopwatch.Elapsed.TotalMilliseconds / repetitions;

            // The Null level does no work so a rate calculated from its almost zero duration
            // would be meaningless.
            bool hasWork = iterations > 0;
            string perCore = hasWork ? (1000.0 / milliseconds).ToString("N0") : "-";
            string perMachine = hasWork ? (effectiveCores * 1000.0 / milliseconds).ToString("N0") : "-";
            report.AppendLine(
                $"  {level,-8}   {iterations,12:N0}   {milliseconds,10:N3}   {perCore,15}   {perMachine,22}");
        }

        report.AppendLine();
        report.AppendLine($"  Stima macchina calcolata su {effectiveCores:N1} core efficaci");
        report.AppendLine("  (ProcessorCount x 0,625, ovvero SMT a due vie che rende ~1,25x per core fisico).");
        report.AppendLine("  Il limite effettivo deve essere confermato attraverso l'esperimento di saturazione.");

        return report.ToString();
    }

    // VALIDATION

    /// <summary>
    /// Ensures that every enum argument is a defined member of its respective type,
    /// guarding against invalid integer casts.
    /// </summary>
    /// <param name="payloadSize">Payload size to validate.</param>
    /// <param name="intensity">Work intensity to validate.</param>
    /// <param name="kind">Workload kind to validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when an argument is not a valid value of its enum type.
    /// </exception>
    private static void Validate(PayloadSize payloadSize, WorkIntensity intensity, WorkloadKind kind)
    {
        if (!Enum.IsDefined(payloadSize))
            throw new ArgumentOutOfRangeException(nameof(payloadSize),
                $"Value {(int)payloadSize} is not a valid {nameof(PayloadSize)}. " +
                $"Expected 0-{Enum.GetValues<PayloadSize>().Length - 1}.");

        if (!Enum.IsDefined(intensity))
            throw new ArgumentOutOfRangeException(nameof(intensity),
                $"Value {(int)intensity} is not a valid {nameof(WorkIntensity)}. " +
                $"Expected 0-{Enum.GetValues<WorkIntensity>().Length - 1}.");

        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind),
                $"Value {(int)kind} is not a valid {nameof(WorkloadKind)}. " +
                $"Expected 0-{Enum.GetValues<WorkloadKind>().Length - 1}.");
    }
}