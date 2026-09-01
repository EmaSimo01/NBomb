using Microsoft.AspNetCore.Server.Kestrel.Core;

// HTTP Server
//
// Minimal ASP.NET Core http server exposing the same endpoint over four transport
// configurations, so that transport version's effects can be separated from
// serialization and framing effects when comparing against other protocols.
//
// One test serves only one of those configurations:
//
//   NBombedHttpServer                                : 6000  HTTP/1.1 cleartext   (default)
//   NBombedHttpServer --protocol http1 --tls off     : 6000  HTTP/1.1 cleartext
//   NBombedHttpServer --protocol http1 --tls on      : 6001  HTTP/1.1 over TLS
//   NBombedHttpServer --protocol http2 --tls off     : 6006  HTTP/2 cleartext (h2c)
//   NBombedHttpServer --protocol http2 --tls on      : 6007  HTTP/2 over TLS, ALPN offers h2 only
//   NBombedHttpServer --calibrate                    : times each --intensity level and exits
//
// Port 6006 is the counterpart of the gRPC server's port 6004: an http vs gRPC comparison across those two ports
// holds the transport constant and isolates serialization and framing.
//
// Environment variables for TLS runs:
//   NBOMB_CERT_PATH      : absolute path to the PFX file
//   NBOMB_CERT_PASSWORD  : password for the PFX file

// Workload calibration. The iteration counts --intensity provides are hardware specific.
if (ServerCli.WantsCalibration(args))
{
    Console.WriteLine(OperationsHandler.Calibrate());
    return 0;
}

// COMMAND LINE
string[] knownOptions = ["--protocol", "--tls", "--calibrate"];

if (!ServerCli.TryValidateKnown(args, knownOptions, out string? unknownError))
{
    Console.Error.WriteLine(unknownError);
    return 1;
}

if (!ServerCli.TryReadProtocol(args, ["http1", "http2"], "http1", out string protocolName, out string? protocolError))
{
    Console.Error.WriteLine(protocolError);
    return 1;
}

if (!ServerCli.TryReadTls(args, out bool useTls, out string? tlsError))
{
    Console.Error.WriteLine(tlsError);
    return 1;
}

// Runtime counter sampling. It does not start unless NBOMB_SAMPLER_CSV is set. Covers the GC and
// thread-pool figures that Performance Monitor cannot report for a .NET 10 process; PerfMon
// remains the source for OS observed resources.
RuntimeSampler.StartIfRequested();

// NOT CreateBuilder(args): the command line is parsed above, so handing it to the
// host would make its command line configuration provider try to read "--tls on" as a
// configuration key and reject a valueless flag such as "--calibrate".
WebApplicationBuilder builder = WebApplication.CreateBuilder();

// LOGGING
// Keep only console output at Error level to minimize logging overhead.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Error);

// CONFIGURATION
// Clear all default configuration sources to reduce startup overhead.
builder.Configuration.Sources.Clear();

// LISTENER SELECTION
//
// A test opens one listener so the measured process never carries the sockets and
// threads of a protocol that is not under examination and the PerfMon counters describe a single
// transport configuration. The protocol is also set explicitly rather than left at the
// Http1AndHttp2 default because a multiprotocol listener has not the same configuration as a
// single protocol one.
(int port, HttpProtocols protocols) = (protocolName, useTls) switch
{
    ("http1", false) => (6000, HttpProtocols.Http1),
    ("http1", true)  => (6001, HttpProtocols.Http1),

    // Cleartext HTTP/2 (h2c). Requires the client to connect with prior knowledge since
    // there is no ALPN negotiation without TLS.
    ("http2", false) => (6006, HttpProtocols.Http2),

    // HTTP/2 over TLS. Setting Http2 makes ALPN offer h2 alone, so a client asking for
    // http/1.1 fails the handshake instead of being served the other version.
    ("http2", true)  => (6007, HttpProtocols.Http2),

    _ => (0, HttpProtocols.None)
};

if (port == 0)
{
    Console.Error.WriteLine($"Unsupported combination: --protocol {protocolName} --tls {(useTls ? "on" : "off")}.");
    return 1;
}

// KESTREL
builder.WebHost.ConfigureKestrel(options =>
    options.ListenAnyIP(port, listenOptions =>
    {
        listenOptions.Protocols = protocols;
        if (useTls)
            listenOptions.UseHttps(ServerCli.CertPath(), ServerCli.CertPassword());
    }));

// Remove the "Server: Kestrel" response header.
builder.Services.Configure<KestrelServerOptions>(options =>
    options.AddServerHeader = false);

WebApplication app = builder.Build();

// No HTTPS redirection: a redirect would add a round trip to every measured request and would
// break the plaintext listeners.

// GET "/"
//
// Query parameters
//   payload    int   Maps to PayloadSize
//   intensity  int   Maps to WorkIntensity
//   kind       int   Maps to WorkloadKind   (0 = Cpu, 1 = IO)
//   mode       int   Maps to ExecutionMode  (0 = Blocking, 1 = Async)
//
// Response
//   200 text/plain  : ASCII hex payload of the requested size
//   400 Bad Request : when any parameter is out of range
//
// The delegate is not declared `async` because the blocking branch runs the workload
// inline on the Kestrel thread and returns an already completed task, so the blocking path is
// unambiguously blocking.
app.MapGet("/", (int payload, int intensity, int kind, int mode) =>
{
    // Validate raw integer inputs before casting to enum types. Returning 400 here keeps
    // invalid input handling explicit and avoids propagating ArgumentOutOfRangeException
    // through the middleware pipeline.
    if (!Enum.IsDefined(typeof(PayloadSize), payload) ||
        !Enum.IsDefined(typeof(WorkIntensity), intensity) ||
        !Enum.IsDefined(typeof(WorkloadKind), kind) ||
        !Enum.IsDefined(typeof(ExecutionMode), mode))
    {
        return Task.FromResult(Results.BadRequest(
            $"Invalid parameters: payload={payload}, intensity={intensity}, " +
            $"kind={kind}, mode={mode}. All values must be integers in range."));
    }

    PayloadSize size            = (PayloadSize)payload;
    WorkIntensity workIntensity = (WorkIntensity)intensity;
    WorkloadKind workKind       = (WorkloadKind)kind;
    ExecutionMode execMode      = (ExecutionMode)mode;

    if (execMode == ExecutionMode.Async)
        return ExecuteAsyncPath(size, workIntensity, workKind);

    // The array is shared and read only.
    byte[] blockingPayload = OperationsHandler.ExecuteBlocking(size, workIntensity, workKind);
    return Task.FromResult(Results.Bytes(blockingPayload, contentType: "text/plain"));
});

// With a single listener a mistyped selection would leave the port the client expects
// closed and logging is set at Error so Kestrel's own "Now listening on" line never appears.
Console.WriteLine(
    $"HTTP server: protocol={protocolName} tls={(useTls ? "on" : "off")} port={port} kestrel={protocols}");

app.Run();
return 0;

static async Task<IResult> ExecuteAsyncPath(PayloadSize size, WorkIntensity intensity, WorkloadKind kind)
{
    byte[] payload = await OperationsHandler.ExecuteAsync(size, intensity, kind);
    return Results.Bytes(payload, contentType: "text/plain");
}