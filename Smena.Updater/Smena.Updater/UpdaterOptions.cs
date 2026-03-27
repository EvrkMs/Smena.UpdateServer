namespace Smena.Updater;

internal enum UpdaterMode
{
    Update,
    Reconfigure,
    Uninstall
}

internal sealed class UpdaterOptions
{
    public UpdaterMode Mode { get; init; } = UpdaterMode.Update;
    public string ServerUrl { get; init; } = string.Empty;
    public string AppDirectory { get; init; } = string.Empty;
    public string? EntryExeOverride { get; init; }
    public string? ApiKeyOverride { get; init; }
    public string? GrpcAddressOverride { get; init; }
    public bool NoLaunch { get; init; }
    public bool AssumeYes { get; init; }
    /// <summary>
    /// When set, this is a self-update continuation: the updater was launched from a temp
    /// directory and should copy itself over the original at this path before proceeding.
    /// </summary>
    public string? SelfUpdateFromPath { get; init; }

    public static string Usage =>
        """
        Usage:
          Smena.Updater.exe [update] --server-url <url> [--app-dir <path>] [--entry-exe Smena.Client.exe] [--api-key <key>] [--grpc-address <url>] [--no-launch]
          Smena.Updater.exe reconfig [--api-key <key>] [--grpc-address <url>]
          Smena.Updater.exe uninstall --app-dir <path> [--entry-exe Smena.Client.exe] [--yes]

        Notes:
          - Default mode is 'update'
          - You can also use exe aliases: Smena.Reconfig.exe / Smena.Uninstall.exe
        """;

    public static bool TryParse(
        string[] args,
        string executableName,
        out UpdaterOptions options,
        out string errorMessage)
    {
        var mode = ResolveMode(args, executableName, out var argsWithoutMode);
        var argsMap = ParseArgs(argsWithoutMode);
        var appDirectory = argsMap.GetValueOrDefault("app-dir")?.Trim() ?? string.Empty;
        var entryExeOverride = argsMap.GetValueOrDefault("entry-exe");
        var apiKeyOverride = argsMap.GetValueOrDefault("api-key")
            ?? Environment.GetEnvironmentVariable("SMENA_UPDATER_API_KEY");
        var assumeYes = argsMap.ContainsKey("yes");
        var noLaunch = argsMap.ContainsKey("no-launch");
        var selfUpdateFromPath = argsMap.GetValueOrDefault("self-update-from")?.Trim();

        var grpcAddressArg = argsMap.GetValueOrDefault("grpc-address");
        string? grpcAddressOverride = null;
        if (!string.IsNullOrWhiteSpace(grpcAddressArg))
        {
            if (!TryNormalizeGrpcAddress(grpcAddressArg, out var normalizedGrpcAddress, out var grpcError))
            {
                options = new UpdaterOptions();
                errorMessage = grpcError;
                return false;
            }

            grpcAddressOverride = normalizedGrpcAddress;
        }

        if (mode == UpdaterMode.Update)
        {
            var serverUrl = argsMap.GetValueOrDefault("server-url");
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                options = new UpdaterOptions();
                errorMessage = "Update mode requires: --server-url.";
                return false;
            }

            if (!TryNormalizeHttpsServerUrl(serverUrl, out var normalizedServerUrl, out errorMessage))
            {
                options = new UpdaterOptions();
                return false;
            }

            options = new UpdaterOptions
            {
                Mode = mode,
                ServerUrl = normalizedServerUrl,
                AppDirectory = appDirectory,
                EntryExeOverride = entryExeOverride,
                ApiKeyOverride = apiKeyOverride,
                GrpcAddressOverride = grpcAddressOverride,
                NoLaunch = noLaunch,
                AssumeYes = assumeYes,
                SelfUpdateFromPath = selfUpdateFromPath
            };
            errorMessage = string.Empty;
            return true;
        }

        if (mode == UpdaterMode.Reconfigure)
        {
            options = new UpdaterOptions
            {
                Mode = mode,
                ServerUrl = string.Empty,
                AppDirectory = appDirectory,
                EntryExeOverride = entryExeOverride,
                ApiKeyOverride = apiKeyOverride,
                GrpcAddressOverride = grpcAddressOverride,
                NoLaunch = noLaunch,
                AssumeYes = assumeYes,
                SelfUpdateFromPath = selfUpdateFromPath
            };
            errorMessage = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            options = new UpdaterOptions();
            errorMessage = "Uninstall mode requires: --app-dir.";
            return false;
        }

        options = new UpdaterOptions
        {
            Mode = mode,
            ServerUrl = string.Empty,
            AppDirectory = appDirectory,
            EntryExeOverride = entryExeOverride,
            ApiKeyOverride = apiKeyOverride,
            GrpcAddressOverride = grpcAddressOverride,
            NoLaunch = true,
            AssumeYes = assumeYes,
            SelfUpdateFromPath = selfUpdateFromPath
        };

        errorMessage = string.Empty;
        return true;
    }

    private static UpdaterMode ResolveMode(string[] args, string executableName, out string[] argsWithoutMode)
    {
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            var modeFromArg = ParseMode(args[0]);
            if (modeFromArg != null)
            {
                argsWithoutMode = args[1..];
                return modeFromArg.Value;
            }
        }

        argsWithoutMode = args;

        var exe = executableName.ToLowerInvariant();
        if (exe.Contains("reconfig", StringComparison.Ordinal))
        {
            return UpdaterMode.Reconfigure;
        }

        if (exe.Contains("uninstall", StringComparison.Ordinal))
        {
            return UpdaterMode.Uninstall;
        }

        return UpdaterMode.Update;
    }

    private static UpdaterMode? ParseMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "update" => UpdaterMode.Update,
            "reconfig" => UpdaterMode.Reconfigure,
            "uninstall" => UpdaterMode.Uninstall,
            _ => null
        };
    }

    private static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (!raw.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = raw[2..];
            string? value = null;

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            map[key] = value;
        }

        return map;
    }

    private static bool TryNormalizeHttpsServerUrl(string? rawServerUrl, out string normalizedServerUrl, out string error)
    {
        var parsed = TryNormalizeUrl(rawServerUrl, out normalizedServerUrl, out error);
        if (!parsed)
        {
            return false;
        }

        if (!string.Equals(new Uri(normalizedServerUrl).Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"--server-url must use HTTPS. Received: {rawServerUrl?.Trim()}";
            return false;
        }

        return true;
    }

    public static bool TryNormalizeGrpcAddress(string? rawGrpcAddress, out string normalizedGrpcAddress, out string error)
    {
        var parsed = TryNormalizeUrl(rawGrpcAddress, out normalizedGrpcAddress, out error);
        if (!parsed)
        {
            return false;
        }

        var scheme = new Uri(normalizedGrpcAddress).Scheme;
        if (!string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            error = $"--grpc-address must use HTTP or HTTPS. Received: {rawGrpcAddress?.Trim()}";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeUrl(string? rawUrl, out string normalizedUrl, out string error)
    {
        normalizedUrl = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "Missing required URL value.";
            return false;
        }

        var candidate = rawUrl.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            error = $"Invalid URL: {candidate}";
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        normalizedUrl = builder.Uri.ToString().TrimEnd('/');
        return true;
    }
}
