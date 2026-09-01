using System.Buffers;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Web;
using Grpc.Core;
using Grpc.Net.Client;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.WebSockets;
using NBombedGrpcServer;
using static NBombedGrpcServer.Operation;

namespace NBombLauncher;

/// <summary>
/// Orchestrates NBomber test scenarios for HTTP/1.1 and HTTP/2, WebSocket and gRPC
/// servers.
///
/// <para>
/// <b>Session model.</b> One scenario iteration is one session: a connection is opened,
/// <see cref="LaunchConfig.OpsPerSession"/> operations are performed on it then it is
/// closed. Each of those three phases is a separate <see cref="Step"/>, so NBomber times them
/// independently and reports a latency distribution for each. The <c>operation</c> percentiles
/// are therefore single operation percentiles, computed over
/// <c>iterations x OpsPerSession</c> samples rather than a session mean divided by the
/// operation count.
/// </para>
///
/// <para>
/// <b>The scenario data is not the session.</b> This was verified rather than assumed. NBomber's
/// scenario level row aggregates every <i>step execution</i>, so for a session of n operations
/// its request count is <c>iterations x (n + 2)</c> and its latency is the mean over all of
/// those steps, not the duration of a single session. Consequences for reading a report:
/// <list type="bullet">
/// <item><description>
/// The cost of a session is <b>computed</b> as <c>connect + n x operation + close</c> from the
/// three step rows. There is no row that reports it directly.
/// </description></item>
/// <item><description>
/// The single operation cost is the <c>operation</c> row.
/// </description></item>
/// <item><description>
/// The scenario level RPS is step executions per second. It is neither sessions per second nor
/// operations per second and quoting it as either is wrong by a factor that depends on n.
/// </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Phase boundaries are not identical across protocols.</b> The split is as honest as each
/// protocol allows and the residual asymmetries are dictated by the protocol.
/// <list type="bullet">
/// <item><description>
/// <c>connect</c> covers DNS and TCP for every target plus the TLS handshake when the scheme is
/// secure. This holds for HTTP and gRPC alike because both hand a preestablished transport to
/// their handler. WebSocket additionally pays an HTTP Upgrade round trip answered by
/// <c>101 Switching Protocols</c>, which is a property of the protocol and not an overhead of the harness.
/// </description></item>
/// <item><description>
/// <c>operation</c> covers one request and one response. For every HTTP/2 based target
/// (http2 and grpc) the <b>first</b> sample of a session additionally carries the HTTP/2
/// connection preface and the SETTINGS exchange: no public API performs them before the first
/// request is dispatched. With <c>-n 1</c> every sample pays it, with <c>-n > 1</c> one in
/// n does, which shows up as a gap between the <c>operation</c> mean and its median.
/// </description></item>
/// <item><description>
/// <c>close</c> is a round trip only for WebSocket whose close frame is answered by the
/// server before <c>CloseAsync</c> returns. For HTTP and gRPC it is local teardown plus a FIN
/// with no wait for the peer.
/// </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Operations within a session are sequential</b>, so HTTP/2 and gRPC never multiplex: the
/// next operation starts only once the previous one has completed. That is the behavior of
/// HTTP/1.1 which cannot multiplex, but for the other two it means their headline feature is
/// not exercised and every conclusion about them carries the clause "one operation at a time per
/// connection". Concurrency at the server is still present, because at a high arrival rate many
/// sessions overlap; what is missing is concurrency <i>inside</i> one connection.
/// </para>
///
/// <para>
/// <b>Fairness.</b> Every protocol opens a fresh connection per session, reads the full
/// response body, is bound by the same operation time limit and performs no format conversion
/// the others do not also perform. No protocol specific worker plugin is attached: instrumenting
/// only one of the four targets would add overhead to that target alone and pollute the
/// comparison. What is <i>not</i> equalized is anything that is an original property
/// of the protocol: HTTP/2 flow control, the WebSocket upgrade round trip, minimal-API routing
/// and model binding against a raw WebSocket message loop, Protobuf framing.
/// </para>
/// </summary>
public sealed class NBomberLauncher
{
    private readonly LaunchConfig _config;

    // PHASE NAMES
    //
    // They are shared constants rather than literals because NBomber keys its step
    // histograms by string.
    // The name "global information" is reserved by NBomber and rejected at runtime.

    /// <summary>Name of the connection establishment phase as it appears in the report.</summary>
    private const string ConnectPhase = "connect";

    /// <summary>Name of the operation phase entered once per operation in a session.</summary>
    private const string OperationPhase = "operation";

    /// <summary>Name of the close phase as it appears in the report.</summary>
    private const string ClosePhase = "close";

    /// <summary>Sortable timestamp used for report folder names and for the test info file.</summary>
    private const string FolderTimestampFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>
    /// Local time base, the same that the runtime sampler and the counter CSV use.
    /// </summary>
    private const string InstantFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>
    /// Initializes the launcher for a single test run.
    /// </summary>
    /// <param name="config">The validated test parameters.</param>
    public NBomberLauncher(LaunchConfig config) => _config = config;

    // PUBLIC API

    /// <summary>
    /// Waits for the server, measures the link and then executes the configured test run
    /// <see cref="LaunchConfig.Repeat"/> times, writing a test info file beside each report.
    /// </summary>
    public async Task RunAsync()
    {
        // Cleartext HTTP/2 (h2c) required this switch on .NET Core 3.x. On .NET 5+
        // the request Version/VersionPolicy pair supersedes it and this does nothing, but
        // setting it costs nothing and removes a confusing failure mode.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        Uri endpoint = new Uri(_config.EndpointUrl);

        // Resolved here one time so neither the readiness poll nor the link probe have the cost of a name lookup per
        // attempt. Measuring DNS inside the link probe results in having two thirds of the figure be a lookup
        // that has nothing to do with what the link probe is supposed to describe.
        IPEndPoint address = await ResolveAsync(endpoint);

        await WaitForServerAsync(endpoint, address);
        string linkReport = await ProbeLinkAsync(address);

        for (int repetition = 1; repetition <= _config.Repeat; repetition++)
        {
            if (_config.Repeat > 1)
                Console.WriteLine($"--- repetition {repetition}/{_config.Repeat} ---");

            string reportFolder = CreateReportFolderPath(repetition);

            DateTime startedUtc = DateTime.UtcNow;

            // Started per repetition rather than once for the whole invocation, this way each report
            // folder describes the load its own test run put on the generator. Its window is the one
            // recorded below as Started/Ended.
            ClientLoadMonitor clientLoad = ClientLoadMonitor.Start(_config.AffinityMask);

            NBomberRunner
                .RegisterScenarios(GetScenarios())

                // Without calling these two methods the report has no indication about which test configuration produced it:
                // NBomber's defaults are a simple standard string. The report folder name has only a timestamp.
                // They also populate the test_suite and test_name columns of the CSV report.
                .WithTestSuite(_config.TestSuiteName)
                .WithTestName(_config.TestName)

                .WithReportFormats(_config.ReportFormats)
                .WithReportFolder(reportFolder)

                // Single interval statistics, without which a stress test reports only one average
                // over every plateau and the saturation knee has to be read manually.
                .WithReportingInterval(TimeSpan.FromSeconds(5))
                .Run();

            DateTime finishedUtc = DateTime.UtcNow;
            string clientLoadReport = clientLoad.StopAndDescribe("  ");

            // Written also to the console as well as on the file: whether the generator was the
            // limit decides if the test is worth keeping.
            Console.Write(clientLoadReport);
            WriteRunInfo(reportFolder, repetition, startedUtc, finishedUtc, linkReport, clientLoadReport);
        }
    }

    // BENCHMARK PREPARATION

    /// <summary>
    /// Blocks the load generator until the server accepts a TCP connection on the resolved endpoint, then waits
    /// <see cref="LaunchConfig.StartDelaySeconds"/> more.
    ///
    /// <para>
    /// This synchronization method is an alternative to having to manually press buttons and running commands on the terminal.
    /// Keypressing means that the user has a limited time window to start the server and the client and a coordination error
    /// could truncate the server's counter without producing any error. The test would then lose its counters for a potentially
    /// important part. Polling the port removes the human factor from the timing loop.
    /// </para>
    ///
    /// <para>
    /// The extra delay exists because the counter collector starts a few seconds after the server
    /// binds its port: connecting the instant the port answers would put the first seconds of
    /// load outside the recorded window.
    /// </para>
    /// </summary>
    /// <param name="endpoint">The endpoint, for the messages.</param>
    /// <param name="address">The resolved address to poll.</param>
    /// <exception cref="TimeoutException">Thrown when the server never answered.</exception>
    private async Task WaitForServerAsync(Uri endpoint, IPEndPoint address)
    {
        TimeSpan limit = TimeSpan.FromMinutes(2);
        Stopwatch waited = Stopwatch.StartNew();
        bool announced = false;

        while (true)
        {
            if (await CanConnectAsync(address, TimeSpan.FromSeconds(2)))
                break;

            if (waited.Elapsed > limit)
            {
                throw new TimeoutException(
                    $"No server answered on {endpoint.Host}:{endpoint.Port} within {limit.TotalMinutes:N0} minutes. "
                  + "Check that run-server.ps1 was started with the matching --protocol and --tls.");
            }
            if (!announced)
            {
                Console.WriteLine($"Waiting for {endpoint.Host}:{endpoint.Port} to accept connections...");
                announced = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        
        Console.WriteLine($"Server is up on {endpoint.Host}:{endpoint.Port}.");
        if (_config.StartDelaySeconds > 0)
        {
            Console.WriteLine($"Waiting {_config.StartDelaySeconds}s so the counter collector is recording...");
            await Task.Delay(TimeSpan.FromSeconds(_config.StartDelaySeconds));
        }
    }

    /// <summary>
    /// Times a short series of TCP connections and returns a human readable summary.
    ///
    /// <para>
    /// This catches the mistake that could be otherwise invisible: with a wired and a wireless
    /// interface both up nothing reports that a test run used the wrong one.
    /// </para>
    ///
    /// <para>
    /// This is to know the condition of the link and for deciding whether two test run on the same
    /// benchmark are comparable.
    /// </para>
    /// </summary>
    /// <param name="address">The resolved address to probe.</param>
    private static async Task<string> ProbeLinkAsync(IPEndPoint address)
    {
        const int samples = 20;
        List<double> milliseconds = new List<double>(samples);

        for (int i = 0; i < samples; i++)
        {
            double? elapsed = await TimeConnectAsync(address, TimeSpan.FromSeconds(2));

            if (elapsed.HasValue)
                milliseconds.Add(elapsed.Value);
        }
        if (milliseconds.Count == 0)
            return "  Link probe     : failed, no connection completed";

        milliseconds.Sort();
        double min = milliseconds[0];
        double median = milliseconds[milliseconds.Count / 2];
        double p95 = milliseconds[Math.Min((int)(milliseconds.Count * 0.95), milliseconds.Count - 1)];
        string line = $"  Link probe     : TCP connect over {milliseconds.Count} samples, "
                    + $"min {min.ToString("N3", CultureInfo.InvariantCulture)} ms, "
                    + $"median {median.ToString("N3", CultureInfo.InvariantCulture)} ms, "
                    + $"p95 {p95.ToString("N3", CultureInfo.InvariantCulture)} ms";
        Console.WriteLine(line.TrimStart());
        return line;
    }

    /// <summary>
    /// Times a single TCP connection excluding everything that is not the connection itself.
    ///
    /// <para>
    /// The socket and the cancellation source are built before the stopwatch starts otherwise the measurement is polluted.
    /// </para>
    /// </summary>
    /// <returns>Elapsed milliseconds or <c>null</c> when the connection did not complete.</returns>
    private static async Task<double?> TimeConnectAsync(IPEndPoint address, TimeSpan timeout)
    {
        using Socket socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        using CancellationTokenSource cancellation = new CancellationTokenSource(timeout);

        try
        {
            long start = Stopwatch.GetTimestamp();
            await socket.ConnectAsync(address, cancellation.Token);
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts one TCP connection and reports whether it completed within the given time.
    /// </summary>
    private static async Task<bool> CanConnectAsync(IPEndPoint address, TimeSpan timeout)
    {
        using Socket socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        using CancellationTokenSource cancellation = new CancellationTokenSource(timeout);

        try
        {
            await socket.ConnectAsync(address, cancellation.Token);
            return true;
        }
        catch (Exception)
        {
            // Any failure here means that the server is not ready yet.
            return false;
        }
    }

    /// <summary>
    /// Resolves the endpoint host to an address once per test run.
    /// </summary>
    private static async Task<IPEndPoint> ResolveAsync(Uri endpoint)
    {
        if (IPAddress.TryParse(endpoint.Host, out IPAddress? literal))
            return new IPEndPoint(literal, endpoint.Port);

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(endpoint.Host);

        if (addresses.Length == 0)
            throw new InvalidOperationException($"Host '{endpoint.Host}' did not resolve to any address.");

        return new IPEndPoint(addresses[0], endpoint.Port);
    }

    // HELPERS

    /// <summary>
    /// Builds the path of a timestamped subfolder inside <see cref="LaunchConfig.ReportDirectory"/>
    /// so that successive test runs never overwrite each other's reports.
    /// Folder name format: <c>yyyy-MM-dd_HH-mm-ss</c>, sortable, with the repetition index
    /// appended when more than one test run was requested.
    /// </summary>
    private string CreateReportFolderPath(int repetition)
    {
        string folderName = DateTime.Now.ToString(FolderTimestampFormat, CultureInfo.InvariantCulture);

        if (_config.Repeat > 1)
            folderName += $"_rep{repetition}";

        return Path.Combine(_config.ReportDirectory, folderName);
    }

    /// <summary>
    /// Writes <c>run-info.txt</c> beside the report: the full configuration, the exact window the
    /// measured phase occupied, the link probe, the load the generator itself carried and the
    /// client environment.
    ///
    /// <para>
    /// The window is the most important part. The server's collector records a
    /// generous span by design and this file is the indication of the time window the report has to be looked in, so
    /// trimming is done from a recorded fact rather than from an assumption about how long the
    /// operator took. Plain aligned text rather than JSON because these files are read by the user.
    /// </para>
    /// </summary>
    private void WriteRunInfo(
        string reportFolder, int repetition, DateTime startedUtc, DateTime finishedUtc,
        string linkReport, string clientLoadReport)
    {
        try
        {
            Directory.CreateDirectory(reportFolder);

            StringBuilder info = new StringBuilder();
            info.AppendLine("NBomb run info");
            info.AppendLine();
            info.AppendLine(_config.ToSummary());
            info.AppendLine();
            info.AppendLine($"  Repetition     : {repetition} of {_config.Repeat}");

            // Local time.
            info.AppendLine($"  Started (local): {startedUtc.ToLocalTime().ToString(InstantFormat, CultureInfo.InvariantCulture)}");
            info.AppendLine($"  Ended   (local): {finishedUtc.ToLocalTime().ToString(InstantFormat, CultureInfo.InvariantCulture)}");
            info.AppendLine(linkReport);
            info.Append(clientLoadReport);
            info.AppendLine();
            info.AppendLine("Client environment");
            info.Append(EnvironmentInfo.Describe("  "));

            // UTF-8 for compatibility with text reading programs.
            File.WriteAllText(
                Path.Combine(reportFolder, "run-info.txt"),
                info.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            // A missing info file must not fail a test that already produced its measurements.
            Console.Error.WriteLine($"[run-info] could not be written: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the scenario definitions matching the configured protocol.
    /// </summary>
    private ScenarioProps[] GetScenarios() => _config.Protocol switch
    {
        Protocol.Http1     => [DefineHttpScenario(Protocol.Http1)],
        Protocol.Http2     => [DefineHttpScenario(Protocol.Http2)],
        Protocol.Websocket => [DefineWebSocketScenario()],
        Protocol.Grpc      => [DefineGrpcScenario()],
        _ => []
    };

    /// <summary>
    /// Appends the workload control parameters to an HTTP endpoint URL as query-string values.
    /// Built once per test rather than per iteration so that string formatting never appears
    /// inside a measured phase.
    /// </summary>
    /// <param name="baseUrl">The endpoint URL to modify.</param>
    private string BuildHttpUrl(string baseUrl)
    {
        UriBuilder uriBuilder = new UriBuilder(baseUrl);
        NameValueCollection query = HttpUtility.ParseQueryString(uriBuilder.Query);
        query["payload"]   = ((int)_config.Payload).ToString();
        query["intensity"] = ((int)_config.Intensity).ToString();
        query["kind"]      = ((int)_config.Kind).ToString();
        query["mode"]      = ((int)_config.Execution).ToString();
        uriBuilder.Query = query.ToString();
        return uriBuilder.Uri.ToString();
    }

    /// <summary>
    /// Opens a TCP connection to <paramref name="host"/>:<paramref name="port"/> and, when
    /// <paramref name="useTls"/> is set, completes the TLS handshake on it with an explicit ALPN
    /// list, returning a stream ready to be handed to
    /// <see cref="SocketsHttpHandler.ConnectCallback"/>.
    ///
    /// <para>
    /// <b>Why the handshake happens here.</b> <see cref="SocketsHttpHandler"/> default to
    /// dial lazily inside the first send which would put the TCP and TLS handshake inside the
    /// first operation sample and leave the connect phase with nothing to measure. Since .NET 7 a
    /// callback that returns an already authenticated <see cref="SslStream"/> makes the handler
    /// skip its own TLS, so the handshake can be moved into the connect phase without being paid
    /// twice.
    /// </para>
    ///
    /// <para>
    /// <b>ALPN is not optional.</b> The handler decides between HTTP/2 and HTTP/1.1 from the
    /// protocol negotiated on the stream that it is given. Omitting the protocol list would negotiate
    /// nothing and, under <see cref="HttpVersionPolicy.RequestVersionExact"/>, bring every request
    /// on the connection to fail.
    /// </para>
    ///
    /// <para>
    /// <b>Ownership.</b> The returned stream owns the socket and the <see cref="SslStream"/>,
    /// when present, owns the <see cref="NetworkStream"/>. Disposing the returned stream therefore
    /// closes everything. On any failure nothing is returned and everything already created is
    /// disposed here.
    /// </para>
    /// </summary>
    /// <param name="host">Host name or literal address of the endpoint.</param>
    /// <param name="port">TCP port of the endpoint.</param>
    /// <param name="useTls">Whether to complete a TLS handshake on the connected socket.</param>
    /// <param name="alpn">Application protocol to offer, <c>h2</c> or <c>http/1.1</c>.</param>
    /// <param name="cancellationToken">Aborts a connect or handshake that never completes.</param>
    /// <returns>A connected and, when requested, authenticated stream.</returns>
    private static async Task<Stream> OpenTransportAsync(
        string host,
        int port,
        bool useTls,
        SslApplicationProtocol alpn,
        CancellationToken cancellationToken)
    {
        // NoDelay mirrors what SocketsHttpHandler sets on the sockets it opens itself. Without it
        // this path would measure Nagle's algorithm.
        Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(host, port, cancellationToken);
            NetworkStream networkStream = new NetworkStream(socket, ownsSocket: true);
            
            if (!useTls)
                return networkStream;
            
            SslStream sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
            try
            {
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ApplicationProtocols = [alpn]
                }, cancellationToken);
            }
            catch
            {
                // Disposing the SslStream cascades to the NetworkStream and to the socket. Leaving
                // it to the finalizer would hold a socket handle open until the next collection,
                // which at a few thousand sessions per second exhausts the ephemeral port range.
                await sslStream.DisposeAsync();
                throw;
            }
            return sslStream;
        }
        catch
        {
            // Reached when the connect phase failed, before any stream wrapped the socket.
            // Dispose is idempotent so this is also harmless after the inner handler above ran.
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds a cancellation source that fires at the configured operation time limit, linked to
    /// the scenario's own token so the end of the simulation still stops the work.
    /// </summary>
    private CancellationTokenSource OperationTimeLimit(IScenarioContext context)
    {
        CancellationTokenSource timeLimit =
            CancellationTokenSource.CreateLinkedTokenSource(context.ScenarioCancellationToken);
        timeLimit.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        return timeLimit;
    }

    // SCENARIO BUILDERS

    /// <summary>
    /// Builds an HTTP load scenario associated to a single HTTP version split into the three
    /// measured phases <c>connect</c>, <c>operation</c> and <c>close</c>.
    ///
    /// <para>
    /// A dedicated <see cref="SocketsHttpHandler"/> is constructed per session and disposed at
    /// the end of it. This construction yields a fresh connection for
    /// both HTTP/1.1 and HTTP/2: a handler owns its own connection pool so a new handler forces
    /// a new TCP (and TLS) handshake. A shared pooled client would reuse connections across
    /// iterations and the <c>Connection: close</c> header is not an alternative because
    /// HTTP/2 forbids it.
    /// </para>
    ///
    /// <para>
    /// The transport is established by the connect phase and handed to the handler through
    /// <see cref="SocketsHttpHandler.ConnectCallback"/>, this way the handshake is attributed to the
    /// phase that performs it instead of slowing down the first operation. For HTTP/2 the connection
    /// preface and the SETTINGS exchange still land in that first operation: the handler performs
    /// them when it dispatches the first request.
    /// </para>
    ///
    /// <para>
    /// The negotiated version is asserted on every response.
    /// <see cref="HttpVersionPolicy.RequestVersionExact"/> only constrains the request side, so
    /// without the assertion a downgrade to HTTP/1.1 would invalidate an entire test
    /// without any visible symptom.
    /// </para>
    /// </summary>
    /// <param name="protocol">Either <see cref="Protocol.Http1"/> or <see cref="Protocol.Http2"/>.</param>
    private ScenarioProps DefineHttpScenario(Protocol protocol)
    {
        Version httpVersion = protocol == Protocol.Http2
            ? HttpVersion.Version20
            : HttpVersion.Version11;

        // The httpVersion must be the same on both sides: the handler picks the protocol from what ALPN negotiated on
        // the stream it is handed and a mismatch fails every request on the connection.
        SslApplicationProtocol alpn = protocol == Protocol.Http2
            ? SslApplicationProtocol.Http2
            : SslApplicationProtocol.Http11;

        string scenarioName = LaunchConfig.Canonical(protocol);
        string url = BuildHttpUrl(_config.EndpointUrl);
        Uri endpoint = new Uri(url);
        bool useTls = endpoint.Scheme == Uri.UriSchemeHttps;
        int operations = _config.OpsPerSession;

        ScenarioProps scenario = Scenario.Create(scenarioName, async context =>
        {
            // Session state shared between the three phases by ordinary closure capture: the
            // steps are lambdas nested inside this one so no NBomber specific mechanism is
            // needed. Nullable because the connect phase is what assigns it and the compiler does
            // not treat an assignment made inside a lambda as definite.
            Stream? transport = null;

            // 0 means this body still owns the transport, 1 means
            // the handler took it and will dispose it together with its connection pool.
            int transportHandedOff = 0;

            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                // Guarantee the session's operations share exactly one connection, so that the
                // connect phase accounts for the whole session's handshake cost.
                MaxConnectionsPerServer = 1,
                EnableMultipleHttp2Connections = false,

                ConnectCallback = (_, _) =>
                {
                    // The preestablished transport can be consumed only once. The two settings
                    // above mean the handler should ask only once per session, but if the peer
                    // drops the connection in the middle of the session the handler quietly dials again.
                    // It would then be handed a stream that is already reading from and every operation from
                    // that point on would be timed against a connection that no connect phase sample covers.
                    // Interlocked rather than a bool because the handler may invoke this from
                    // a different thread rather than the one that ran the connect phase.
                    Stream? established = transport;

                    if (Interlocked.Exchange(ref transportHandedOff, 1) != 0 || established is null)
                    {
                        throw new InvalidOperationException(
                            "SocketsHttpHandler requested a second connection: the transport "
                          + "established by the connect phase has already been handed over.");
                    }

                    return ValueTask.FromResult<Stream>(established);
                }
            };

            // disposeHandler: false keeps the ownership chain written down in the close phase
            // and in the finally below instead of splitting it between HttpClient and this body.
            HttpClient client = new HttpClient(handler, disposeHandler: false)
            {
                // Every operation also carries its own linked time limit, but a
                // client whose timeout is left at the 100 second default would be the only one of
                // the four protocols with a different limit hiding behind the first.
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };

            try
            {
                Response<object> connectResult = await Step.Run<object>(ConnectPhase, context, async () =>
                {
                    try
                    {
                        using CancellationTokenSource timeLimit = OperationTimeLimit(context);

                        transport = await OpenTransportAsync(
                            endpoint.Host, endpoint.Port, useTls, alpn, timeLimit.Token);

                        return Response.Ok<object>(statusCode: "CONNECTED", sizeBytes: 0);
                    }
                    catch (OperationCanceledException)
                    {
                        // NBomber files a canceled step as a timeout under its own status code.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogError(ConnectPhase, ex);
                        return Response.Fail<object>(statusCode: "CONNECT_FAILED", message: ex.Message);
                    }
                });

                // Iteration auto restart is disabled so a failed phase returns here normally and
                // nothing stops the session. The null test tells the compiler the connect phase ran.
                if (connectResult.IsError || transport is null)
                    return Response.Fail(statusCode: "CONNECT_FAILED", message: connectResult.Message);

                for (int i = 0; i < operations; i++)
                {
                    int operationNumber = i + 1;
                    Response<object> operationResult = await Step.Run<object>(OperationPhase, context, async () =>
                    {
                        try
                        {
                            using CancellationTokenSource deadline = OperationTimeLimit(context);
                            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url)
                            {
                                Version = httpVersion,
                                VersionPolicy = HttpVersionPolicy.RequestVersionExact
                            };
                            request.Headers.Add("Accept", "text/plain");
                            using HttpResponseMessage response = await client.SendAsync(
                                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

                            if (!response.IsSuccessStatusCode)
                            {
                                return Response.Fail<object>(
                                    statusCode: ((int)response.StatusCode).ToString(),
                                    message: $"Failed response status on operation {operationNumber}/{operations}.");
                            }
                            if (response.Version != httpVersion)
                            {
                                return Response.Fail<object>(
                                    statusCode: "WRONG_HTTP_VERSION",
                                    message: $"Negotiated HTTP/{response.Version} but HTTP/{httpVersion} was required.");
                            }

                            long received = await DrainAsync(response.Content, deadline.Token);

                            // Single response reported.
                            return Response.Ok<object>(statusCode: "200", sizeBytes: (int)received);
                        }
                        catch (OperationCanceledException)
                        {
                            // Also covers HttpClient.Timeout which surfaces as a cancellation.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            LogError(OperationPhase, ex);
                            return Response.Fail<object>(statusCode: "OPERATION_FAILED", message: ex.Message);
                        }
                    });

                    if (operationResult.IsError)
                        return Response.Fail(statusCode: "OPERATION_FAILED", message: operationResult.Message);
                }

                Response<object> closeResult = await Step.Run<object>(ClosePhase, context, () =>
                {
                    try
                    {
                        // HTTP has no close handshake. Disposing the handler drops the pooled
                        // connection which disposes the stream ConnectCallback handed over and,
                        // through NetworkStream(ownsSocket: true), closes the socket. What this
                        // phase measures is local teardown plus a FIN with no wait for the peer.
                        client.Dispose();
                        handler.Dispose();

                        return Task.FromResult(Response.Ok<object>(statusCode: "CLOSED", sizeBytes: 0));
                    }
                    catch (Exception ex)
                    {
                        LogError(ClosePhase, ex);
                        return Task.FromResult(
                            Response.Fail<object>(statusCode: "CLOSE_FAILED", message: ex.Message));
                    }
                });

                if (closeResult.IsError)
                    return Response.Fail(statusCode: "CLOSE_FAILED", message: closeResult.Message);

                // sizeBytes stays 0 because NBomber accumulates the single operation sizes
                // reported above into the scenario level row by itself.
                return Response.Ok(statusCode: "OK", sizeBytes: 0);
            }
            finally
            {
                // Every call here is idempotent: it costs nothing on the path where the close
                // phase already ran. It is a failsafe standing between a failed operation
                // and a leaked socket. It sits outside every Step.Run, so a teardown forced by a
                // failure is never recorded as a successful close.
                client.Dispose();
                handler.Dispose();

                // Only reachable when the handler never took the transport, in other words when connect
                // succeeded but no request ever went out. Once ConnectCallback has handed it over
                // the handler owns it and disposing it here would race the connection pool.
                if (Volatile.Read(ref transportHandedOff) == 0)
                    transport?.Dispose();
            }
        });

        return ApplyProfile(scenario);
    }

    /// <summary>
    /// Reads a response body completely and returns the number of bytes received without materializing it.
    ///
    /// <para>
    /// The full response still travels on the network and is still read, which is what makes the comparison
    /// with the other two protocols honest; what is avoided is the extra managed copy.
    /// </para>
    /// </summary>
    private static async Task<long> DrainAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream body = await content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;

        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
                total += read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    /// <summary>
    /// Builds a WebSocket load scenario split into the three measured phases <c>connect</c>,
    /// <c>operation</c> and <c>close</c>.
    ///
    /// <para>
    /// The command string is sent for every operation rather than negotiated once, so the
    /// server stays stateless.
    /// </para>
    ///
    /// <para>
    /// This is the only scenario whose <c>connect</c> and <c>close</c> phases both measure an
    /// application level round trip: the upgrade request is answered by <c>101</c> and the close
    /// frame is answered by the server's own close frame before the call returns. Both are designed
    /// properties of the protocol and both are why its two outer phases cost theoretically more than the
    /// corresponding HTTP and gRPC ones.
    /// </para>
    /// </summary>
    private ScenarioProps DefineWebSocketScenario()
    {
        string url = _config.EndpointUrl;
        int operations = _config.OpsPerSession;

        // Format expected by the server: "<payload>,<intensity>,<kind>,<mode>"
        string command = string.Join(',',
            (int)_config.Payload, (int)_config.Intensity, (int)_config.Kind, (int)_config.Execution);

        ScenarioProps scenario = Scenario.Create(LaunchConfig.Canonical(Protocol.Websocket), async context =>
        {
            // Constructing the client involves no socket, so it is allocated outside the connect
            // phase to avoid billing an allocation to the handshake that would inflate the phase this protocol
            // is most often compared on. The using declaration has effect on every exit path, including
            // the early failure returns below, so no try/finally is needed here.
            using WebSocket webSocket = new WebSocket(new WebSocketConfig());
            Response<object> connectResult = await Step.Run<object>(ConnectPhase, context, async () =>
            {
                try
                {
                    await webSocket.Connect(url);
                    return Response.Ok<object>(statusCode: "CONNECTED", sizeBytes: 0);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogError(ConnectPhase, ex);
                    return Response.Fail<object>(statusCode: "CONNECT_FAILED", message: ex.Message);
                }
            });

            if (connectResult.IsError)
                return Response.Fail(statusCode: "CONNECT_FAILED", message: connectResult.Message);
            for (int i = 0; i < operations; i++)
            {
                int operationNumber = i + 1;
                Response<object> operationResult = await Step.Run<object>(OperationPhase, context, async () =>
                {
                    try
                    {
                        using CancellationTokenSource timeLimit = OperationTimeLimit(context);
                        await webSocket.Send(command);
                        using WebSocketResponse result = await webSocket.Receive(timeLimit.Token);

                        // An empty response body indicates a server failure.
                        if (result.Data.Length == 0)
                        {
                            return Response.Fail<object>(
                                statusCode: "EMPTY_RESPONSE",
                                message: $"Empty body on operation {operationNumber}/{operations}.");
                        }

                        return Response.Ok<object>(statusCode: "OK", sizeBytes: result.Data.Length);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogError(OperationPhase, ex);
                        return Response.Fail<object>(statusCode: "OPERATION_FAILED", message: ex.Message);
                    }
                });
                if (operationResult.IsError)
                    return Response.Fail(statusCode: "OPERATION_FAILED", message: operationResult.Message);
            }

            Response<object> closeResult = await Step.Run<object>(ClosePhase, context, async () =>
            {
                try
                {
                    // The only close phase that measures a round trip: the call sends
                    // a close frame and does not return until the server's close frame arrives,
                    // which the /ws handler sends back.
                    await webSocket.Close();
                    return Response.Ok<object>(statusCode: "CLOSED", sizeBytes: 0);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogError(ClosePhase, ex);
                    return Response.Fail<object>(statusCode: "CLOSE_FAILED", message: ex.Message);
                }
            });

            if (closeResult.IsError)
                return Response.Fail(statusCode: "CLOSE_FAILED", message: closeResult.Message);
            return Response.Ok(statusCode: "OK", sizeBytes: 0);
        });

        return ApplyProfile(scenario);
    }

    /// <summary>
    /// Builds a gRPC unary call load scenario, split into the three measured phases
    /// <c>connect</c>, <c>operation</c> and <c>close</c>.
    ///
    /// <para>
    /// A channel is created per session so that connection establishment is measured on the same
    /// terms as the other protocols. The transport is preestablished by the connect phase and
    /// handed to the channel's handler through <see cref="SocketsHttpHandler.ConnectCallback"/>.
    /// The alternative, <see cref="GrpcChannel.ConnectAsync"/>, is not used because its transport tracks
    /// socket connectivity only and cannot observe whether TLS and
    /// HTTP were negotiated, so it would leave the TLS handshake inside the first operation for
    /// this protocol alone.
    /// </para>
    ///
    /// <para>
    /// Teardown uses <c>ShutdownAsync</c> before <c>Dispose</c>: disposing alone is synchronous
    /// and can block while calls still finishing are cleaned up. Both are inside the close phase so
    /// that cost is attributed rather than hidden but as with HTTP there is no close handshake to
    /// wait for.
    /// </para>
    /// </summary>
    private ScenarioProps DefineGrpcScenario()
    {
        string url = _config.EndpointUrl;
        Uri endpoint = new Uri(url);
        bool useTls = endpoint.Scheme == Uri.UriSchemeHttps;
        int operations = _config.OpsPerSession;

        // Built once per run rather than per iteration so that message construction never appears
        // inside a measured phase. Sharing it across concurrent iterations is safe because it is
        // readonly: gRPC serializes it per call and nothing modifies it after construction.
        PayloadRequest request = new PayloadRequest
        {
            PayloadSize   = (int)_config.Payload,
            Intensity     = (int)_config.Intensity,
            WorkloadKind  = (int)_config.Kind,
            ExecutionMode = (int)_config.Execution
        };

        ScenarioProps scenario = Scenario.Create(LaunchConfig.Canonical(Protocol.Grpc), async context =>
        {
            // Same ownership handoff as the HTTP scenario: see the comments there for why the
            // marker is interlocked and why the transport must be consumed only once.
            Stream? transport = null;
            int transportHandedOff = 0;
            GrpcChannel? channel = null;

            try
            {
                Response<object> connectResult = await Step.Run<object>(ConnectPhase, context, async () =>
                {
                    try
                    {
                        using CancellationTokenSource timeLimit = OperationTimeLimit(context);

                        // gRPC is HTTP/2 only, over TLS as well as in cleartext, so the ALPN offer
                        // is fixed rather than inferred from the protocol.
                        transport = await OpenTransportAsync(
                            endpoint.Host, endpoint.Port, useTls, SslApplicationProtocol.Http2,
                            timeLimit.Token);

                        channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions
                        {
                            HttpHandler = new SocketsHttpHandler
                            {
                                MaxConnectionsPerServer = 1,
                                EnableMultipleHttp2Connections = false,
                                ConnectCallback = (_, _) =>
                                {
                                    Stream? established = transport;

                                    if (Interlocked.Exchange(ref transportHandedOff, 1) != 0 || established is null)
                                    {
                                        throw new InvalidOperationException(
                                            "SocketsHttpHandler requested a second connection: the transport "
                                          + "established by the connect phase has already been handed over.");
                                    }
                                    return ValueTask.FromResult<Stream>(established);
                                }
                            },
                            DisposeHttpClient = true
                        });
                        return Response.Ok<object>(statusCode: "CONNECTED", sizeBytes: 0);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogError(ConnectPhase, ex);
                        return Response.Fail<object>(statusCode: "CONNECT_FAILED", message: ex.Message);
                    }
                });

                if (connectResult.IsError || channel is null)
                    return Response.Fail(statusCode: "CONNECT_FAILED", message: connectResult.Message);

                // Alias are not nullable. Nullable flow analysis restarts at every lambda boundary, so
                // without it the phases below would have to dereference through a null forgiving
                // operator that no longer asserts anything the compiler checked.
                GrpcChannel connectedChannel = channel;
                OperationClient client = new OperationClient(connectedChannel);
                for (int i = 0; i < operations; i++)
                {
                    Response<object> operationResult = await Step.Run<object>(OperationPhase, context, async () =>
                    {
                        try
                        {
                            // A gRPC call without a time limit never gives up. The same value the
                            // other three protocols use is passed here explicitly.
                            PayloadResponse response = await client.GetPayloadAsync(
                                request,
                                deadline: DateTime.UtcNow.AddSeconds(_config.TimeoutSeconds),
                                cancellationToken: context.ScenarioCancellationToken);

                            // Read Length only. Materializing the ByteString into a string here
                            // would reintroduce on the client the conversion cost that was
                            // removed from the server to make this comparison fair.
                            return Response.Ok<object>(statusCode: "OK", sizeBytes: response.Payload.Length);
                        }
                        catch (RpcException ex)
                        {
                            LogError(OperationPhase, ex);
                            return Response.Fail<object>(
                                statusCode: ex.StatusCode.ToString(), message: ex.Status.Detail);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            LogError(OperationPhase, ex);
                            return Response.Fail<object>(statusCode: "OPERATION_FAILED", message: ex.Message);
                        }
                    });
                    if (operationResult.IsError)
                        return Response.Fail(statusCode: "OPERATION_FAILED", message: operationResult.Message);
                }
                Response<object> closeResult = await Step.Run<object>(ClosePhase, context, async () =>
                {
                    try
                    {
                        // ShutdownAsync drains running calls; Dispose is what releases
                        // the invoker (DisposeHttpClient is set) and closes the socket.
                        await connectedChannel.ShutdownAsync();
                        connectedChannel.Dispose();
                        return Response.Ok<object>(statusCode: "CLOSED", sizeBytes: 0);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogError(ClosePhase, ex);
                        return Response.Fail<object>(statusCode: "CLOSE_FAILED", message: ex.Message);
                    }
                });
                if (closeResult.IsError)
                    return Response.Fail(statusCode: "CLOSE_FAILED", message: closeResult.Message);
                return Response.Ok(statusCode: "OK", sizeBytes: 0);
            }
            finally
            {
                // Dispose is idempotent but ShutdownAsync after Dispose is not. Calling only Dispose
                // here keeps the failure path from throwing ObjectDisposedException over the error
                // that arisen.
                channel?.Dispose();

                // Only reachable when the channel never dispatched a call and the handler never
                // took the transport.
                if (Volatile.Read(ref transportHandedOff) == 0)
                    transport?.Dispose();
            }
        });
        return ApplyProfile(scenario);
    }

    // SHARED UTILITIES

    /// <summary>
    /// Applies the configured load profile and warmup policy to a scenario.
    /// </summary>
    /// <param name="scenario">The scenario to configure.</param>
    /// <returns>The scenario with policy applied.</returns>
    private ScenarioProps ApplyProfile(ScenarioProps scenario)
    {
        scenario = scenario
            .WithLoadSimulations(BuildLoadSimulations())

            // NBomber's default is to raise an internal exception during Step.Run when a
            // phase reports a failure, unwinding the scenario body from wherever it happens to be.
            // There are two reasons why this is turned off. First, the phases have to be skipped and torn down in
            // order, which the explicit IsError checks express and an exception could not. Second,
            // the iteration NBomber then records on the scenario level row carries an empty status
            // code, hiding which phase failed. The exception type is internal to NBomber, so
            // it cannot be caught and thrown again either. The cost is that every Step.Run
            // result must be inspected because with this off nothing stops a session.
            .WithRestartIterationOnFail(false);

        return _config.Warmup
            ? scenario.WithWarmUpDuration(TimeSpan.FromSeconds(5))
            : scenario.WithoutWarmUp();
    }

    /// <summary>
    /// Builds the load simulation sequence for the configured profile.
    ///
    /// <para>
    /// Both profiles use <see cref="Simulation.Inject"/>, an <b>open</b> workload model in which
    /// the arrival rate is independent from the performance of the server responds. This is the correct model
    /// for locating saturation and avoiding coordinated omission: a closed model would stop
    /// issuing requests while the server is slow and therefore not track the most important latencies.
    /// </para>
    ///
    /// <para>
    /// The openness applies to <i>sessions</i>. Operations inside a session are sequential, so
    /// with <c>--ops-per-session > 1</c> the arrival of individual operations is partly gated
    /// by how fast the server answers the previous one. The <c>global information</c> row is the
    /// open model figure; the <c>operation</c> row carries a residual amount of coordinated
    /// omission that grows with the session length.
    /// </para>
    ///
    /// <para>
    /// The interval is 1 second.
    /// </para>
    ///
    /// <para>
    /// The stress profile is a staircase of constant rate plateaus rather than a smooth ramp because a
    /// ramp makes the saturation point very difficult to localize: every second offers a
    /// different rate, nothing reaches steady state and queueing from one second contaminates
    /// the next. Each plateau instead holds a fixed rate long enough for the thread-pool
    /// to settle and the pauses between plateaus let queues drain.
    /// It stays in any case exploratory: its summary rows average every plateau together,
    /// so a more precise saturation curve is a series of separate load tests, one per rate.
    /// </para>
    /// </summary>
    private LoadSimulation[] BuildLoadSimulations()
    {
        TimeSpan interval = TimeSpan.FromSeconds(1);

        if (_config.Profile == LoadProfile.Load)
        {
            return
            [
                Simulation.Inject(
                    rate: _config.Rps,
                    interval: interval,
                    during: TimeSpan.FromSeconds(_config.DurationSeconds))
            ];
        }

        List<LoadSimulation> simulations = new List<LoadSimulation>(_config.Steps * 2);
        for (int step = 1; step <= _config.Steps; step++)
        {
            int rate = Math.Max(1, (int)Math.Round((double)_config.MaxRps * step / _config.Steps));

            simulations.Add(Simulation.Inject(
                rate: rate,
                interval: interval,
                during: TimeSpan.FromSeconds(_config.StepDurationSeconds)));

            // Drain between plateaus but not after the last one.
            if (step < _config.Steps)
                simulations.Add(Simulation.Pause(TimeSpan.FromSeconds(5)));
        }
        return simulations.ToArray();
    }

    /// <summary>
    /// Writes a phase level error to the console in red for immediate visual
    /// feedback during a test run.
    /// </summary>
    /// <param name="phase">Name of the phase that failed.</param>
    /// <param name="ex">The exception that was caught.</param>
    private static void LogError(string phase, Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[{phase}] Exception: {ex.Message}");
        Console.ResetColor();
    }
}