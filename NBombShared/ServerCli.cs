/// <summary>
/// Command line configuration shared by the three servers.
///
/// <para>
/// Parsing is done here rather than being repeated in each <c>Program.cs</c>. Each server passes only the options it
/// has: the gRPC and WebSocket servers serve one protocol by construction and take only
/// <c>--tls</c>, while the HTTP server takes also <c>--protocol http1|http2</c>
/// because it is the project that serves two protocols.
/// </para>
/// </summary>
public static class ServerCli
{
    /// <summary>Environment variable holding the path of the PFX used by TLS listeners.</summary>
    public const string CertPathVariable = "NBOMB_CERT_PATH";

    /// <summary>Environment variable holding the password of that PFX.</summary>
    public const string CertPasswordVariable = "NBOMB_CERT_PASSWORD";

    /// <summary>Path the servers fall back to when <see cref="CertPathVariable"/> is unset.</summary>
    public const string DefaultCertPath = @"C:\CA\server.pfx";

    /// <summary>Password the servers fall back to when <see cref="CertPasswordVariable"/> is unset.</summary>
    public const string DefaultCertPassword = "nbomber";

    /// <summary>
    /// True when the user asked for a workload calibration test.
    /// </summary>
    public static bool WantsCalibration(string[] args) =>
        args.Any(argument => string.Equals(argument, "--calibrate", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads <c>--tls on|off</c>. Not set = off.
    /// </summary>
    /// <param name="args">Raw command line.</param>
    /// <param name="tls">Receives the parsed choice.</param>
    /// <param name="error">Receives a message when the value is missing or unrecognized.</param>
    /// <returns><c>true</c> when the command line is correct.</returns>
    public static bool TryReadTls(string[] args, out bool tls, out string? error)
    {
        tls = false;
        error = null;

        int index = IndexOf(args, "--tls");
        if (index < 0)
            return true;
        if (index + 1 >= args.Length)
        {
            error = "Missing value for --tls. Expected: on | off.";
            return false;
        }

        string value = args[index + 1];
        if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            tls = true;
            return true;
        }
        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
            return true;

        error = $"Unknown value '{value}' for --tls. Expected: on | off.";
        return false;
    }

    /// <summary>
    /// Reads <c>--protocol</c>.
    /// </summary>
    /// <param name="args">Raw command line.</param>
    /// <param name="allowed">Accepted lowercase values.</param>
    /// <param name="fallback">Value used when the option is absent.</param>
    /// <param name="protocol">Receives the parsed lowercased value.</param>
    /// <param name="error">Receives a message when the value is missing or unrecognized.</param>
    /// <returns><c>true</c> when the command line is correct.</returns>
    public static bool TryReadProtocol(
        string[] args, string[] allowed, string fallback, out string protocol, out string? error)
    {
        protocol = fallback;
        error = null;

        int index = IndexOf(args, "--protocol");
        if (index < 0)
            return true;
        if (index + 1 >= args.Length)
        {
            error = $"Missing value for --protocol. Expected: {string.Join(" | ", allowed)}.";
            return false;
        }

        string value = args[index + 1].ToLowerInvariant();
        if (!allowed.Contains(value))
        {
            error = $"Unknown value '{value}' for --protocol. Expected: {string.Join(" | ", allowed)}.";
            return false;
        }

        protocol = value;
        return true;
    }

    /// <summary>
    /// Rejects any option this executable does not understand.
    ///
    /// <para>
    /// Without this a wrong option would be ignored and the server would start in
    /// configuration not requested. This behavior is difficult to notice because
    /// the test completes but the data describe not what is expected.
    /// </para>
    /// </summary>
    /// <param name="args">Raw command line.</param>
    /// <param name="known">Option names this executable accepts, including the leading dashes.</param>
    /// <param name="error">Receives a message naming the first unknown token.</param>
    /// <returns><c>true</c> when every token is accepted.</returns>
    public static bool TryValidateKnown(string[] args, string[] known, out string? error)
    {
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];

            if (!argument.StartsWith('-'))
                continue;
            if (!known.Contains(argument, StringComparer.OrdinalIgnoreCase))
            {
                error = $"Unknown option '{argument}'. Accepted: {string.Join(" ", known)}.";
                return false;
            }

            // Skip the value of a valued option so it is not mistaken for a token.
            if (!string.Equals(argument, "--calibrate", StringComparison.OrdinalIgnoreCase))
                i++;
        }
        return true;
    }

    /// <summary>Path of the PFX to load</summary>
    public static string CertPath() =>
        Environment.GetEnvironmentVariable(CertPathVariable) ?? DefaultCertPath;

    /// <summary>Password of the previous PFX</summary>
    public static string CertPassword() =>
        Environment.GetEnvironmentVariable(CertPasswordVariable) ?? DefaultCertPassword;

    private static int IndexOf(string[] args, string name) =>
        Array.FindIndex(args, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
}