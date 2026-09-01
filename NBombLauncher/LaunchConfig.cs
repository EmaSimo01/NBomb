using System.Globalization;
using NBomber.Contracts.Stats;

namespace NBombLauncher;

/// <summary>
/// Identifies which protocol to exercise during a test.
///
/// <para>
/// <see cref="Http1"/> and <see cref="Http2"/> hit the same endpoint over different
/// transports.
/// </para>
///
/// <para>
/// One protocol is exercised per test and the HTTP server binds one listener per test run as well, so
/// it has to be started with the matching <c>--protocol</c> and <c>--tls</c> pair.
/// </para>
///
/// <para>
/// Member names are PascalCase because they are C# identifiers; the command line is
/// case-insensitive and the canonical spelling in this system is the lowercase form produced by
/// <see cref="LaunchConfig.Canonical{T}"/>.
/// </para>
/// </summary>
public enum Protocol
{
    /// <summary>HTTP/1.1 : port 6000 plain, 6001 TLS.</summary>
    Http1,

    /// <summary>HTTP/2 : port 6006 cleartext h2c, 6007 TLS via ALPN.</summary>
    Http2,

    /// <summary>WebSocket : port 6002 plain, 6003 WSS.</summary>
    Websocket,

    /// <summary>gRPC : port 6004 h2c, 6005 TLS.</summary>
    Grpc
}

/// <summary>
/// Shape of the generated load.
/// </summary>
public enum LoadProfile
{
    /// <summary>
    /// Constant arrival rate for a fixed duration. Measures the latency distribution at a
    /// steady operating point.
    /// </summary>
    Load,

    /// <summary>
    /// A staircase of increasing constant-rate plateaus separated by short pauses. Locates the
    /// saturation point: the first plateau where achieved throughput stops tracking the offered
    /// rate or where tail latency breaks out.
    ///
    /// <para>
    /// Exploratory only. The summary rows of a stress test average every plateau together so the
    /// correct way to report a saturation curve is a series of separate <see cref="Load"/>
    /// tests, one per rate.
    /// </para>
    /// </summary>
    Stress
}

/// <summary>
/// Immutable set of parameters describing a single experiment.
/// Grouped into a record rather than passed as a long positional argument list for convenience.
/// </summary>
/// <param name="Host">Host name or address of the server under test.</param>
/// <param name="Tls">Whether to use transport security. Selects both the scheme and the port.</param>
/// <param name="UrlOverride">
/// Full endpoint URL replacing the canonical resolution, for a deployment that does not use the
/// standard ports. Empty in the standard case.
/// </param>
/// <param name="Protocol">Which protocol to exercise.</param>
/// <param name="Payload">Response size the server should return per operation.</param>
/// <param name="Intensity">How much work the server performs per operation.</param>
/// <param name="Kind">Whether that work is CPU-bound or I/O-bound.</param>
/// <param name="Execution">Whether the server handler executes the work blocking or async.</param>
/// <param name="OpsPerSession">
/// Operations performed per session. One scenario iteration opens a new connection, performs
/// this many operations on it and then closes it. Each of those three phases is timed separately.
///
/// <para>
/// <b>This multiplies the offered load.</b> <see cref="Rps"/> injects <i>sessions</i> per second,
/// so the server sees <c>Rps * OpsPerSession</c> operations per second while the number of
/// connections per second stays at <see cref="Rps"/>.
/// </para>
/// </param>
/// <param name="Profile">Load or stress.</param>
/// <param name="Rps">Constant arrival rate in sessions per second, used by <see cref="LoadProfile.Load"/>.</param>
/// <param name="DurationSeconds">Duration of the simulation used by <see cref="LoadProfile.Load"/>.</param>
/// <param name="MaxRps">Rate reached by the final plateau. Used by <see cref="LoadProfile.Stress"/>.</param>
/// <param name="Steps">Number of plateaus used by <see cref="LoadProfile.Stress"/>.</param>
/// <param name="StepDurationSeconds">Duration of each plateau used by <see cref="LoadProfile.Stress"/>.</param>
/// <param name="Warmup">Whether to run a 5 seconds warmup phase before the main simulation.</param>
/// <param name="TimeoutSeconds">
/// Time limit applied to every operation of every protocol. Without it the three
/// stacks would each fall back on their own default, which is 100 seconds for HTTP and no limit
/// at all for WebSocket and gRPC, and the way a protocol fails under saturation would differ for a reason that
/// is not protocol related.
/// </param>
/// <param name="Repeat">How many times to run the same test configuration in one invocation.</param>
/// <param name="StartDelaySeconds">
/// Seconds to wait after the server answers the readiness probe, before the load starts, so the
/// server's counter collector is already recording.
/// </param>
/// <param name="AffinityMask">
/// Processor affinity mask applied to this process or 0 to apply the default. Used only on the
/// loopback benchmark to give client and server independent sets of cores.
/// </param>
/// <param name="Label">Test label shared with the server's counter files. Calculated when empty.</param>
/// <param name="ReportDirectory">Root directory where timestamped report subfolders are created.</param>
/// <param name="ReportFormats">Output formats of the NBomber report.</param>
public sealed record LaunchConfig(
    string Host,
    bool Tls,
    string UrlOverride,
    Protocol Protocol,
    PayloadSize Payload,
    WorkIntensity Intensity,
    WorkloadKind Kind,
    ExecutionMode Execution,
    int OpsPerSession,
    LoadProfile Profile,
    int Rps,
    int DurationSeconds,
    int MaxRps,
    int Steps,
    int StepDurationSeconds,
    bool Warmup,
    int TimeoutSeconds,
    int Repeat,
    int StartDelaySeconds,
    long AffinityMask,
    string Label,
    string ReportDirectory,
    ReportFormat[] ReportFormats)
{
    /// <summary>
    /// Lowercase spelling of an enum value, used everywhere a name is written down rather than
    /// parsed: report folders, <c>test_name</c> and the test summary.
    ///
    /// <para>
    /// The command line accepts any casing but only lowercase spelling is documented so the
    /// values typed into a command, the values printed in a report and the values in a folder name
    /// are the same.
    /// </para>
    /// </summary>
    public static string Canonical<T>(T value) where T : struct, Enum =>
        value.ToString().ToLowerInvariant();

    /// <summary>Canonical <c>on</c>/<c>off</c> rendering of the TLS choice.</summary>
    public string TlsName => Tls ? "on" : "off";

    /// <summary>
    /// The endpoint this test dials resolved from host, protocol and TLS choice.
    ///
    /// <para>
    /// This table is where the endpoint layout is mapped on the client side, so a caller
    /// only supplies a host, a protocol and whether TLS is on. The WebSocket server maps its
    /// endpoint at <c>/ws</c> and rejects anything else, so the path is as much part of the
    /// canonical address as the port is.
    /// </para>
    ///
    /// <para>
    /// <see cref="UrlOverride"/> overrides this mapping when set.
    /// </para>
    /// </summary>
    public string EndpointUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(UrlOverride))
                return UrlOverride;

            (string scheme, int port, string path) = Protocol switch
            {
                Protocol.Http1     => (Tls ? "https" : "http", Tls ? 6001 : 6000, string.Empty),
                Protocol.Http2     => (Tls ? "https" : "http", Tls ? 6007 : 6006, string.Empty),
                Protocol.Websocket => (Tls ? "wss"   : "ws",   Tls ? 6003 : 6002, "/ws"),
                Protocol.Grpc      => (Tls ? "https" : "http", Tls ? 6005 : 6004, string.Empty),
                _ => throw new ArgumentOutOfRangeException(nameof(Protocol), Protocol, "Unsupported protocol.")
            };

            return new UriBuilder(scheme, Host, port, path).Uri.ToString();
        }
    }

    /// <summary>
    /// Operations per second generated for the server, as opposed to the sessions per
    /// second that <see cref="Rps"/> names.
    ///
    /// <para>
    /// Recorded in the summary, in <c>test_name</c> and in the run info file because it is the
    /// quantity an experiment has to hold constant and because no row of the NBomber report
    /// states it. The scenario-level row counts <i>step executions</i>, so its rate is
    /// <c>Rps x (OpsPerSession + 2)</c>; the operation count lives in the <c>operation</c> row
    /// alone. Quoting the scenario row as a throughput figure is wrong by a factor that depends
    /// on the session length.
    /// </para>
    /// </summary>
    public int OperationsPerSecond => Rps * OpsPerSession;

    /// <summary>Peak operations per second of a stress test.</summary>
    public int PeakOperationsPerSecond => MaxRps * OpsPerSession;

    /// <summary>
    /// Connections opened per second. Equal to <see cref="Rps"/> because every session opens a
    /// fresh connection. Renamed for readability reason.
    /// </summary>
    public int ConnectionsPerSecond => Rps;

    /// <summary>
    /// Name of the comparison family this test belongs to, recorded in the report body and in the
    /// <c>test_suite</c> column of the CSV report.
    ///
    /// <para>
    /// The transport and its security are what a test is compared <i>across</i> so they name the
    /// family; everything identifying the individual test is reported in <see cref="TestName"/>.
    /// </para>
    /// </summary>
    public string TestSuiteName => $"{Canonical(Protocol)}-tls_{TlsName}";

    /// <summary>
    /// Full parameter fingerprint of this test run recorded in the report body and in the
    /// <c>test_name</c> column of the CSV report.
    ///
    /// <para>
    /// Commas are excluded by design: the CSV report interpolates this value into a
    /// comma separated row without quoting it, so a comma here would shift every column after it.
    /// </para>
    /// </summary>
    public string TestName
    {
        get
        {
            string load = Profile == LoadProfile.Load
                ? $"load-r{Rps}-ops{OperationsPerSecond}-d{DurationSeconds}s"
                : $"stress-max{MaxRps}-ops{PeakOperationsPerSecond}-s{Steps}x{StepDurationSeconds}s";

            string warm = Warmup ? "-warmup" : string.Empty;

            return $"p{Canonical(Payload)}-i{Canonical(Intensity)}-k{Canonical(Kind)}"
                 + $"-x{Canonical(Execution)}-n{OpsPerSession}-{load}{warm}";
        }
    }

    /// <summary>
    /// Label shared by the client report folder and the two server's counter files, so the
    /// halves of a test can be paired. Calculated from the parameters when not supplied.
    /// </summary>
    public string EffectiveLabel =>
        string.IsNullOrWhiteSpace(Label) ? $"{TestSuiteName}-{TestName}" : Label;

    /// <summary>
    /// Renders the configuration as an aligned block for the console test summary and for the
    /// test info file. Written to be human-readable.
    /// </summary>
    public string ToSummary()
    {
        string loadLine = Profile == LoadProfile.Load
            ? $"{Rps} sessions/s for {DurationSeconds}s"
            : $"{Steps} plateaus up to {MaxRps} sessions/s, {StepDurationSeconds}s each";

        string rateLine = Profile == LoadProfile.Load
            ? $"{OperationsPerSecond} ops/s  ({Rps} sessions/s x {OpsPerSession} ops)"
            : $"{PeakOperationsPerSecond} ops/s at peak  ({MaxRps} sessions/s x {OpsPerSession} ops)";

        string affinity = AffinityMask == 0
            ? "not set"
            : "0x" + AffinityMask.ToString("X", CultureInfo.InvariantCulture);

        return $"""
                  Protocol       : {Canonical(Protocol)}
                  TLS            : {TlsName}
                  Endpoint       : {EndpointUrl}
                  Payload size   : {Canonical(Payload)}
                  Work kind      : {Canonical(Kind)}
                  Work intensity : {Canonical(Intensity)}
                  Execution      : {Canonical(Execution)}
                  Ops / session  : {OpsPerSession}
                  Profile        : {Canonical(Profile)} ({loadLine})
                  Offered load   : {rateLine}
                  Connections    : {ConnectionsPerSecond} per second
                  Warmup         : {(Warmup ? "on" : "off")}
                  Timeout        : {TimeoutSeconds}s per operation
                  Repetitions    : {Repeat}
                  Start delay    : {StartDelaySeconds}s after the readiness probe
                  Affinity       : {affinity}
                  Test suite     : {TestSuiteName}
                  Test name      : {TestName}
                  Label          : {EffectiveLabel}
                  Report formats : {string.Join(", ", ReportFormats.Select(f => f.ToString().ToLowerInvariant()))}
                  Report dir     : {ReportDirectory}
                """;
    }
}