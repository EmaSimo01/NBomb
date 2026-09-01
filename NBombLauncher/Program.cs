using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using NBomber.Contracts.Stats;

namespace NBombLauncher;

/// <summary>
/// An on/off switch to centralize the spelling at every level of the system.
/// The cost of an extra enum is nothing and the system expressivity is better.
/// </summary>
public enum Toggle
{
    /// <summary>Disabled.</summary>
    Off = 0,

    /// <summary>Enabled.</summary>
    On = 1
}

// ENTRY POINT

/// <summary>
/// CLI entry point for the NBomber launcher.
/// Parses and validates command line arguments, prints a test summary and then
/// delegates execution to <see cref="NBomberLauncher"/>.
///
/// <para>
/// The workload enums (<see cref="PayloadSize"/>, <see cref="WorkIntensity"/>,
/// <see cref="WorkloadKind"/>, <see cref="ExecutionMode"/>) come from the shared project,
/// so a value selected here cannot be different from the value the server interprets.
/// </para>
///
/// <para>
/// <b>Vocabulary.</b> Every parameter is named the same as it is named in <c>run-client.sh</c>,
/// in <c>run-server.ps1</c> and in the server executables. Moreover, every value is documented in its
/// lowercase form. Parsing is case-insensitive but only the lowercase spelling is documented.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>Application entry point.</summary>
    /// <returns>
    /// <c>0</c> on success; <c>1</c> if argument validation fails or an
    /// unhandled exception is thrown during the test.
    /// </returns>
    private static async Task<int> Main(string[] args)
    {
        // CLI option definitions

        Option<string> host = new Option<string>("--host")
        {
            Description = "Host name or address of the server under test.",
            Required = true,
            Arity = ArgumentArity.ExactlyOne
        };

        Option<Toggle> tls = new Option<Toggle>("--tls")
        {
            Description = "Transport security: on | off. Selects both the scheme and the port.",
            DefaultValueFactory = _ => Toggle.Off,
            Arity = ArgumentArity.ExactlyOne
        };

        Option<Protocol> protocol = new Option<Protocol>("--protocol")
        {
            Description = "Protocol to exercise: http1 | http2 | websocket | grpc.",
            DefaultValueFactory = _ => Protocol.Http1,
            Arity = ArgumentArity.ExactlyOne
        };

        // Escape hatch for a deployment that does not use the canonical ports. If it is left empty the
        // endpoint is resolved from --host, --protocol and --tls.
        Option<string> urlOverride = new Option<string>("--url")
        {
            Description = "Full endpoint URL, overriding the canonical host/port/path resolution.",
            DefaultValueFactory = _ => string.Empty,
            Arity = ArgumentArity.ZeroOrOne
        };
        urlOverride.Validators.Add(result =>
        {
            string? value = result.GetValue(urlOverride);
            if (!string.IsNullOrWhiteSpace(value) && !Uri.TryCreate(value, UriKind.Absolute, out _))
                result.AddError($"'{value}' is not a valid absolute URL.");
        });

        Option<PayloadSize> payloadSize = new Option<PayloadSize>("--payload", "-p")
        {
            Description = "Response payload size: null | small | medium | large | extreme.",
            DefaultValueFactory = _ => PayloadSize.Small,
            Arity = ArgumentArity.ExactlyOne
        };

        Option<WorkIntensity> intensity = new Option<WorkIntensity>("--intensity", "-i")
        {
            Description = "How much work the server performs per operation: "
                        + "null | low | medium | high | extreme. Run a server with --calibrate "
                        + "to see what each level costs in milliseconds on that machine.",
            DefaultValueFactory = _ => WorkIntensity.Null,
            Arity = ArgumentArity.ExactlyOne
        };

        Option<WorkloadKind> workloadKind = new Option<WorkloadKind>("--kind", "-k")
        {
            Description = "Kind of work the server performs: cpu (SHA-256 chain) | IO (simulated latency).",
            DefaultValueFactory = _ => WorkloadKind.Cpu,
            Arity = ArgumentArity.ExactlyOne
        };

        Option<ExecutionMode> execution = new Option<ExecutionMode>("--execution", "-x")
        {
            Description = "How the server handler executes the work: blocking | async.",
            DefaultValueFactory = _ => ExecutionMode.Blocking,
            Arity = ArgumentArity.ExactlyOne
        };

        Option<int> opsPerSession = PositiveInt(
            "--ops-per-session", "-n",
            "Operations performed per session on one connection. Note this multiplies the offered "
          + "load: the server sees --rps x this many operations per second, while the number of "
          + "connections per second stays at --rps.",
            defaultValue: 1, minimum: 1);

        Option<LoadProfile> profile = new Option<LoadProfile>("--profile")
        {
            Description = "Load shape: load (constant rate) | stress (exploratory staircase).",
            DefaultValueFactory = _ => LoadProfile.Load,
            Arity = ArgumentArity.ExactlyOne
        };

        Option<int> rps = PositiveInt(
            "--rps", "-r",
            "Arrival rate in sessions per second for the load profile. Multiply by "
          + "--ops-per-session to get the operations per second actually offered.",
            defaultValue: 10, minimum: 1);

        Option<int> duration = PositiveInt(
            "--duration", "-d",
            "Duration of the load profile simulation in seconds.",
            defaultValue: 60, minimum: 1);

        Option<int> maxRps = PositiveInt(
            "--max-rps", null,
            "Arrival rate reached by the final plateau of the stress profile.",
            defaultValue: 1000, minimum: 1);

        Option<int> steps = PositiveInt(
            "--steps", null,
            "Number of plateaus in the stress profile staircase.",
            defaultValue: 8, minimum: 2);

        Option<int> stepDuration = PositiveInt(
            "--step-duration", null,
            "Duration of each stress profile plateau in seconds. Keep >= 20s so the thread pool "
          + "settles and at least one gen2 collection occurs within a plateau.",
            defaultValue: 30, minimum: 1);

        // Defaults to on because every test starts against a freshly restarted server, so without a
        // warmup the tiered-JIT transient lands inside the measured window. A realistic server is warm.
        Option<Toggle> warmup = new Option<Toggle>("--warmup", "-w")
        {
            Description = "Run a 5 second warmup before the measured phase: on | off.",
            DefaultValueFactory = _ => Toggle.On,
            Arity = ArgumentArity.ExactlyOne
        };

        Option<int> timeout = PositiveInt(
            "--timeout", null,
            "Single operation time limit in seconds, same value applied to all four protocols.",
            defaultValue: 30, minimum: 1);

        Option<int> repeat = PositiveInt(
            "--repeat", null,
            "How many times to run this identical test configuration, each into its own report folder.",
            defaultValue: 1, minimum: 1);

        Option<int> startDelay = PositiveInt(
            "--start-delay", null,
            "Seconds to wait after the server answers the readiness probe, so the server's "
          + "counter collector is already recording when the load starts.",
            defaultValue: 5, minimum: 0);

        Option<string> affinity = new Option<string>("--affinity")
        {
            Description = "Processor affinity mask for this process, hex (0xFFFF0000) or decimal. "
                        + "Used on the loopback benchmark to give client and server independent cores. "
                        + "Omit to use the default affinity.",
            DefaultValueFactory = _ => string.Empty,
            Arity = ArgumentArity.ZeroOrOne
        };
        affinity.Validators.Add(result =>
        {
            string? value = result.GetValue(affinity);
            if (!string.IsNullOrWhiteSpace(value) && TryParseMask(value) is null)
                result.AddError($"'{value}' is not a valid affinity mask. Expected hex (0xFF) or decimal.");
        });

        Option<string> label = new Option<string>("--label")
        {
            Description = "Test label, shared with the server's counter files so the two halves "
                        + "of a test can be paired. Calculated from the parameters when omitted.",
            DefaultValueFactory = _ => string.Empty,
            Arity = ArgumentArity.ZeroOrOne
        };

        Option<string> outputDirectory = new Option<string>("--output-dir", "-o")
        {
            Description = "Existing directory where timestamped report subfolders will be created.",
            DefaultValueFactory = _ => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Reports"),
            Arity = ArgumentArity.ExactlyOne
        };
        outputDirectory.Validators.Add(result =>
        {
            string? dir = result.GetValue(outputDirectory);
            if (!Directory.Exists(dir))
                result.AddError($"Output directory '{dir}' does not exist.");
        });

        // By default html and csv. html carries the interval timeline and csv is convenient to make multiple tests
        // comparable side by side in a spreadsheet. Neither replaces the other.
        Option<ReportFormat[]> reportFormats = new Option<ReportFormat[]>("--report-format", "-e")
        {
            Description = "Report formats, one or more: html | csv | md | txt.",
            DefaultValueFactory = _ => [ReportFormat.Html, ReportFormat.Csv],
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
        };

        // ROOT COMMAND
        RootCommand rootCommand = new RootCommand(
            "NBomber client for HTTP/1.1, HTTP/2, WebSocket and gRPC servers")
        {
            host, tls, protocol, urlOverride, payloadSize, intensity, workloadKind, execution,
            opsPerSession, profile, rps, duration, maxRps, steps, stepDuration, warmup,
            timeout, repeat, startDelay, affinity, label, outputDirectory, reportFormats
        };

        // Parsing and validation
        ParseResult parseResult = rootCommand.Parse(args);

        // CLI helpers
        if (parseResult.Tokens.Any(IsHelpToken))
        {
            await parseResult.InvokeAsync();
            return 0;
        }
        if (parseResult.Errors.Count != 0)
        {
            foreach (ParseError error in parseResult.Errors)
                Console.Error.WriteLine(error.Message);
            return 1;
        }

        // Extract parsed values
        LaunchConfig config = new LaunchConfig(
            Host:                parseResult.GetValue(host) ?? string.Empty,
            Tls:                 parseResult.GetValue(tls) == Toggle.On,
            UrlOverride:         parseResult.GetValue(urlOverride) ?? string.Empty,
            Protocol:            parseResult.GetValue(protocol),
            Payload:             parseResult.GetValue(payloadSize),
            Intensity:           parseResult.GetValue(intensity),
            Kind:                parseResult.GetValue(workloadKind),
            Execution:           parseResult.GetValue(execution),
            OpsPerSession:       parseResult.GetValue(opsPerSession),
            Profile:             parseResult.GetValue(profile),
            Rps:                 parseResult.GetValue(rps),
            DurationSeconds:     parseResult.GetValue(duration),
            MaxRps:              parseResult.GetValue(maxRps),
            Steps:               parseResult.GetValue(steps),
            StepDurationSeconds: parseResult.GetValue(stepDuration),
            Warmup:              parseResult.GetValue(warmup) == Toggle.On,
            TimeoutSeconds:      parseResult.GetValue(timeout),
            Repeat:              parseResult.GetValue(repeat),
            StartDelaySeconds:   parseResult.GetValue(startDelay),
            AffinityMask:        TryParseMask(parseResult.GetValue(affinity)) ?? 0,
            Label:               parseResult.GetValue(label) ?? string.Empty,
            ReportDirectory:     parseResult.GetValue(outputDirectory) ?? string.Empty,
            ReportFormats:       parseResult.GetValue(reportFormats) ?? [ReportFormat.Html, ReportFormat.Csv]);

        // Applied before anything measurable happens, this way every thread this process
        // later creates inherits the restricted core set.
        ApplyAffinity(config.AffinityMask);

        // Print test summary
        Console.WriteLine("NBomber launched with the following parameters:");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(config.ToSummary());
        Console.ResetColor();

        // TEST
        try
        {
            await new NBomberLauncher(config).RunAsync();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Fatal error during test run: {ex}");
            Console.ResetColor();
            return 1;
        }
        return 0;
    }

    // HELPERS

    /// <summary>
    /// Restricts this process to the given set of logical processors.
    ///
    /// <para>
    /// Only usefull on the loopback benchmark where client and server share the machine and must be
    /// given independent cores so that neither steals time from the other. On multiple machine benchmarks
    /// the mask is not passed keeping the system to influence the machine behavior.
    /// </para>
    /// </summary>
    /// <param name="mask">Affinity mask, or 0 to leave the process unaltered.</param>
    private static void ApplyAffinity(long mask)
    {
        if (mask == 0)
            return;

        // ProcessorAffinity exists only on Windows and Linux. A rejected affinity must not abort a test
        // but it must be visible, because a test that shares cores is not the test that was asked for.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("[affinity] not supported on this platform, ignored.");
            return;
        }

        try
        {
            using Process self = Process.GetCurrentProcess();
            self.ProcessorAffinity = (IntPtr)mask;
            Console.WriteLine($"[affinity] client pinned to 0x{mask:X}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[affinity] could not apply 0x{mask:X}: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses an affinity mask written either as hex with a <c>0x</c> prefix or as a decimal.
    /// </summary>
    /// <param name="value">Raw text or empty</param>
    /// <returns>The parsed mask or <c>null</c> when the text is empty or malformed.</returns>
    private static long? TryParseMask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string text = value.Trim();
        bool isHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        bool parsed = isHex
            ? long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long mask)
            : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask);

        // A zero mask would give the process no processor so it is rejected.
        return parsed && mask > 0 ? mask : null;
    }

    /// <summary>
    /// Builds an <see cref="int"/> option that rejects values below <paramref name="minimum"/>.
    /// </summary>
    /// <param name="name">Long option name including the leading dashes.</param>
    /// <param name="alias">Short alias, or <c>null</c>, when the option has none.</param>
    /// <param name="description">Help text.</param>
    /// <param name="defaultValue">Value used when the option is absent.</param>
    /// <param name="minimum">Lowest accepted value.</param>
    private static Option<int> PositiveInt(
        string name, string? alias, string description, int defaultValue, int minimum = 1)
    {
        Option<int> option = alias is null
            ? new Option<int>(name)
            : new Option<int>(name, alias);

        option.Description = description;
        option.DefaultValueFactory = _ => defaultValue;
        option.Arity = ArgumentArity.ExactlyOne;

        option.Validators.Add(result =>
        {
            if (result.GetValue(option) < minimum)
                result.AddError($"{name} must be greater than or equal to {minimum}.");
        });

        return option;
    }

    /// <summary>
    /// Returns <c>true</c> when a parsed Token represents a help flag.
    /// </summary>
    /// <param name="token">The token to inspect.</param>
    private static bool IsHelpToken(Token token)
    {
        if (token.Type != TokenType.Option)
            return false;
        return token.Value is "-h" or "--help" or "-?" or "/h" or "/?" or "--version";
    }
}