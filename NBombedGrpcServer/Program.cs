using Microsoft.AspNetCore.Server.Kestrel.Core;

// GRPC Server
//
// Minimal ASP.NET Core gRPC server exposing the Operation service defined in
// the shared .proto file. HTTP/2 is required by the gRPC protocol.
//
// Usage:
//
//   NBombedGrpcServer               : gRPC over h2c on port 6004 (default)
//   NBombedGrpcServer --tls off     : gRPC over h2c on port 6004
//   NBombedGrpcServer --tls on      : gRPC over TLS on port 6005
//   NBombedGrpcServer --calibrate   : times each --intensity level and exits
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
string[] knownOptions = ["--tls", "--calibrate"];

if (!ServerCli.TryValidateKnown(args, knownOptions, out string? unknownError))
{
    Console.Error.WriteLine(unknownError);
    return 1;
}

if (!ServerCli.TryReadTls(args, out bool useTls, out string? tlsError))
{
    Console.Error.WriteLine(tlsError);
    return 1;
}

// Runtime counter sampling. It does not start unless NBOMB_SAMPLER_CSV is set. Covers the GC and
// thread-pool data that Performance Monitor cannot report for a .NET 10 process; PerfMon
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
// Clear all default configuration sources to minimize startup overhead.
builder.Configuration.Sources.Clear();

int port = useTls ? 6005 : 6004;

// KESTREL
//
// Http2 is selected on both listeners. UseHttps on its own does not imply HTTP/2: it enables ALPN
// with whatever protocols the listener is configured for, which defaults to Http1AndHttp2. If the default
// is set the TLS listener would use http/1.1 alongside h2 and gRPC with TLS
// would be measured against a differently configured listener than the HTTP/2 with TLS test
// on port 6007, which registers h2 alone.
builder.WebHost.ConfigureKestrel(options =>
    options.ListenAnyIP(port, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
        if (useTls)
            listenOptions.UseHttps(ServerCli.CertPath(), ServerCli.CertPassword());
    }));

// Remove the "Server: Kestrel" response header.
builder.Services.Configure<KestrelServerOptions>(options =>
    options.AddServerHeader = false);

// SERVICES
// Register gRPC services. No other middleware is needed for a minimal server.
builder.Services.AddGrpc();

WebApplication app = builder.Build();

// Map the PayloadService
app.MapGrpcService<PayloadService>();

// With a single listener a mistyped selection would leave the port the client expects closed
// and logging is set at Error so Kestrel's own "Now listening on" line never appears.
Console.WriteLine($"gRPC server: protocol=grpc tls={(useTls ? "on" : "off")} port={port} kestrel=Http2");

app.Run();
return 0;