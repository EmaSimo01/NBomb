using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// WEBSOCKET Server
//
// Minimal ASP.NET Core WebSocket server. A connection stays open and serves any number of
// request/response exchanges until the client closes it.
//
// The NBomber client sends a command string per operation:
//   "<payload>,<intensity>,<kind>,<mode>". Example: "1,2,0,1"
//     payload   -> PayloadSize
//     intensity -> WorkIntensity
//     kind      -> WorkloadKind   (0 = Cpu, 1 = IO)
//     mode      -> ExecutionMode  (0 = Blocking, 1 = Async)
//
// The command is sent again on every message rather than negotiated once to keep the server
// stateless.
//
// Usage:
//
//   NBombedWebsocketServer               : WS  on port 6002 (default)
//   NBombedWebsocketServer --tls off     : WS  on port 6002
//   NBombedWebsocketServer --tls on      : WSS on port 6003 (requires PFX certificate)
//   NBombedWebsocketServer --calibrate   : times each --intensity level and exits
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

int port = useTls ? 6003 : 6002;

// KESTREL
//
// The listener protocols are left at the Http1AndHttp2 default. A WebSocket upgrade rides on HTTP/1.1
// and a common host serving WSS also answers HTTP/2 for everything else on the same port.
builder.WebHost.ConfigureKestrel(options =>
    options.ListenAnyIP(port, listenOptions =>
    {
        if (useTls)
            listenOptions.UseHttps(ServerCli.CertPath(), ServerCli.CertPassword());
    }));

// Remove the "Server: Kestrel" response header.
builder.Services.Configure<KestrelServerOptions>(options =>
    options.AddServerHeader = false);

WebApplication app = builder.Build();

// Enable the WebSocket middleware. No keep alive ping is configured.
app.UseWebSockets();

// WebSocket endpoint: /ws
// Lifecycle per connection:
//   1. Upgrade HTTP to WebSocket.
//   2. Loop: receive one text frame (the command string), compute the payload,
//      send it back as a single binary frame.
//   3. Exit when the client initiates the closing handshake.
//
// The server never closes a healthy connection on its own. Closing after the first exchange
// would cap every session at one operation.
//
// Non WebSocket requests are rejected with 400 Bad Request.
app.Map("/ws", async context =>
{
    // Reject plain HTTP requests that reach this endpoint without upgrading.
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

    // Rent a small receive buffer from the shared ArrayPool to avoid allocating a new byte
    // array per connection. The command string is at most ~12 bytes ("4,4,1,1"), so 128 bytes
    // is sufficient; the do-while loop handles the case where the frame arrives in
    // multiple segments.
    byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(128);
    MemoryStream messageBuffer = new MemoryStream(128);

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            // Receive a message
            messageBuffer.SetLength(0);
            WebSocketReceiveResult receiveResult;
            do
            {
                receiveResult = await socket.ReceiveAsync(
                    new ArraySegment<byte>(receiveBuffer),
                    CancellationToken.None);

                // Handle client initiated close during receive. This is the normal way a
                // session ends once the client has finished its N operations.
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(
                        receiveResult.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        receiveResult.CloseStatusDescription,
                        CancellationToken.None);
                    return;
                }
                messageBuffer.Write(receiveBuffer, 0, receiveResult.Count);
            } while (!receiveResult.EndOfMessage);

            // Parse the command string
            // Expected format: "<payload>,<intensity>,<kind>,<mode>"
            string command = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
            string[] parameters = command.Split(',');

            if (parameters.Length != 4
                || !int.TryParse(parameters[0], out int rawPayload)
                || !int.TryParse(parameters[1], out int rawIntensity)
                || !int.TryParse(parameters[2], out int rawKind)
                || !int.TryParse(parameters[3], out int rawMode))
            {
                // Command not valid: close with a protocol error status so the
                // NBomber client records a failed step rather than timing out
                await socket.CloseAsync(
                    WebSocketCloseStatus.InvalidPayloadData,
                    "Malformed command. Expected format: \"<payload>,<intensity>,<kind>,<mode>\"",
                    CancellationToken.None);
                return;
            }

            // Validate parsed integers before casting to enum types
            if (!Enum.IsDefined(typeof(PayloadSize), rawPayload) ||
                !Enum.IsDefined(typeof(WorkIntensity), rawIntensity) ||
                !Enum.IsDefined(typeof(WorkloadKind), rawKind) ||
                !Enum.IsDefined(typeof(ExecutionMode), rawMode))
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.InvalidPayloadData,
                    $"Parameter out of range: payload={rawPayload}, intensity={rawIntensity}, " +
                    $"kind={rawKind}, mode={rawMode}.",
                    CancellationToken.None);
                return;
            }
            PayloadSize size            = (PayloadSize)rawPayload;
            WorkIntensity intensity     = (WorkIntensity)rawIntensity;
            WorkloadKind kind           = (WorkloadKind)rawKind;
            ExecutionMode execMode      = (ExecutionMode)rawMode;

            // Compute the payload. The array is shared and SendAsync only
            // reads it, so using the same instance in every connection is safe.
            byte[] payload = execMode == ExecutionMode.Async
                ? await OperationsHandler.ExecuteAsync(size, intensity, kind)
                : OperationsHandler.ExecuteBlocking(size, intensity, kind);

            // Send the payload then loop back to wait the next command.
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    CancellationToken.None);
            }
        }
    }
    catch (WebSocketException)
    {
        // The client closed the connection abruptly. No action needed.
    }
    catch (Exception ex)
    {
        // Unexpected server side error: log at Error level
        var logger = context.RequestServices
            .GetRequiredService<ILogger<WebSocket>>();
        logger.LogError(ex, "Unhandled exception in WebSocket handler.");
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(receiveBuffer);
        await messageBuffer.DisposeAsync();
    }
});

// With a single listener a mistyped selection would leave the port the client expects closed
// and logging is set at Error so Kestrel's own "Now listening on" line never appears.
Console.WriteLine($"WebSocket server: protocol=websocket tls={(useTls ? "on" : "off")} port={port}");

app.Run();
return 0;