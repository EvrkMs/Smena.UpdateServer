using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Smena.Updater;

internal interface IUpdaterInteraction
{
    Task<string?> PromptApiKeyAsync();
    Task<string?> PromptGrpcAddressAsync();
    Task<string?> PromptEnvironmentVariableAsync(string name, string prompt, bool secret, string? initialValue);
    Task<bool> ConfirmCloseClientAsync(string processName, int processCount);
    Task<bool> ConfirmForceCloseClientAsync(string processName, int processCount);
    Task<bool> ConfirmUninstallAsync(string appDirFullPath);
}

internal enum WorkflowStage
{
    ValidateInput,
    EnsureApiKey,
    EnsureGrpcAddress,
    ConnectServer,
    FetchManifest,
    FetchUpdaterPlan,
    CompareVersions,
    EnsureClientStopped,
    DownloadPackage,
    VerifyPackage,
    ExtractPackage,
    ApplyUpdate,
    SaveState,
    LaunchClient,
    ConfirmUninstall,
    RemoveClientFiles,
    ClearConfiguration
}

internal enum WorkflowStageStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Skipped
}

internal sealed record WorkflowProgressUpdate(
    WorkflowStage Stage,
    WorkflowStageStatus Status,
    string Message);

internal sealed record WorkflowResult(
    int ExitCode,
    bool Updated,
    string Message);

internal static class WorkflowStages
{
    public static IReadOnlyList<WorkflowStage> Ordered { get; } =
    [
        WorkflowStage.ValidateInput,
        WorkflowStage.EnsureApiKey,
        WorkflowStage.EnsureGrpcAddress,
        WorkflowStage.ConnectServer,
        WorkflowStage.FetchManifest,
        WorkflowStage.FetchUpdaterPlan,
        WorkflowStage.CompareVersions,
        WorkflowStage.EnsureClientStopped,
        WorkflowStage.DownloadPackage,
        WorkflowStage.VerifyPackage,
        WorkflowStage.ExtractPackage,
        WorkflowStage.ApplyUpdate,
        WorkflowStage.SaveState,
        WorkflowStage.LaunchClient,
        WorkflowStage.ConfirmUninstall,
        WorkflowStage.RemoveClientFiles,
        WorkflowStage.ClearConfiguration
    ];

    public static string GetTitle(WorkflowStage stage) => stage switch
    {
        WorkflowStage.ValidateInput => "Input validation",
        WorkflowStage.EnsureApiKey => "API key check",
        WorkflowStage.EnsureGrpcAddress => "gRPC address check",
        WorkflowStage.ConnectServer => "Server connection",
        WorkflowStage.FetchManifest => "Manifest download",
        WorkflowStage.FetchUpdaterPlan => "Updater plan download",
        WorkflowStage.CompareVersions => "Version compare",
        WorkflowStage.EnsureClientStopped => "Client shutdown",
        WorkflowStage.DownloadPackage => "Package download",
        WorkflowStage.VerifyPackage => "Package verification",
        WorkflowStage.ExtractPackage => "Package extract",
        WorkflowStage.ApplyUpdate => "Apply update",
        WorkflowStage.SaveState => "Save local state",
        WorkflowStage.LaunchClient => "Launch client",
        WorkflowStage.ConfirmUninstall => "Confirm uninstall",
        WorkflowStage.RemoveClientFiles => "Remove client files",
        WorkflowStage.ClearConfiguration => "Clear configuration",
        _ => stage.ToString()
    };
}

internal sealed class UpdaterWorkflow
{
    private readonly UpdaterOptions options;
    private readonly IUpdaterInteraction interaction;
    private readonly IProgress<WorkflowProgressUpdate> progress;
    private readonly JsonSerializerOptions jsonOptions;

    public UpdaterWorkflow(
        UpdaterOptions options,
        IUpdaterInteraction interaction,
        IProgress<WorkflowProgressUpdate> progress)
    {
        this.options = options;
        this.interaction = interaction;
        this.progress = progress;
        jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
    }

    public async Task<WorkflowResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        return options.Mode switch
        {
            UpdaterMode.Reconfigure => await ExecuteReconfigureAsync(cancellationToken),
            UpdaterMode.Uninstall => await ExecuteUninstallAsync(cancellationToken),
            _ => await ExecuteUpdateAsync(cancellationToken)
        };
    }

    private async Task<WorkflowResult> ExecuteUpdateAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        Report(WorkflowStage.ConnectServer, WorkflowStageStatus.Running, "Connecting to update server.");
        var healthUrl = BuildHealthUrl(options.ServerUrl);
        try
        {
            using var response = await http.GetAsync(
                healthUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            return Fail(WorkflowStage.ConnectServer, 4, $"Connection failed: {ex.Message}");
        }

        Report(WorkflowStage.ConnectServer, WorkflowStageStatus.Success, "Server connection successful.");

        Report(WorkflowStage.FetchManifest, WorkflowStageStatus.Running, "Downloading manifest.json.");
        var manifestUrl = BuildManifestUrl(options.ServerUrl);

        RemoteManifest? remoteManifest;
        try
        {
            remoteManifest = await http.GetFromJsonAsync<RemoteManifest>(
                manifestUrl,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            return Fail(WorkflowStage.FetchManifest, 4, $"Manifest download failed: {ex.Message}");
        }

        if (remoteManifest == null ||
            string.IsNullOrWhiteSpace(remoteManifest.Version) ||
            string.IsNullOrWhiteSpace(remoteManifest.PackageUrl) ||
            string.IsNullOrWhiteSpace(remoteManifest.Sha256))
        {
            return Fail(WorkflowStage.FetchManifest, 5, "Manifest is invalid.");
        }

        Report(WorkflowStage.FetchManifest, WorkflowStageStatus.Success, $"Server version: {remoteManifest.Version}");

        Report(WorkflowStage.FetchUpdaterPlan, WorkflowStageStatus.Running, "Downloading updater.plan.json.");
        UpdaterPlan? updaterPlan = null;
        if (!string.IsNullOrWhiteSpace(remoteManifest.UpdaterPlanUrl))
        {
            try
            {
                var planUri = BuildPlanUri(options.ServerUrl, remoteManifest.UpdaterPlanUrl);
                updaterPlan = await http.GetFromJsonAsync<UpdaterPlan>(planUri, cancellationToken: cancellationToken);
                Report(WorkflowStage.FetchUpdaterPlan, WorkflowStageStatus.Success, "Updater plan loaded.");
            }
            catch (Exception ex)
            {
                Report(WorkflowStage.FetchUpdaterPlan, WorkflowStageStatus.Skipped, $"Updater plan unavailable: {ex.Message}");
            }
        }
        else
        {
            SkipStage(WorkflowStage.FetchUpdaterPlan, "Manifest does not include updaterPlanUrl.");
        }

        var appDirFullPath = ResolveAppDirectory(options.AppDirectory, updaterPlan?.App);
        var createAppDirIfMissing = updaterPlan?.App?.CreateAppDirIfMissing ?? false;
        Report(WorkflowStage.ValidateInput, WorkflowStageStatus.Running, "Validating startup options.");
        if (!Directory.Exists(appDirFullPath))
        {
            if (createAppDirIfMissing)
            {
                Directory.CreateDirectory(appDirFullPath);
            }
            else
            {
                return Fail(WorkflowStage.ValidateInput, 3, $"Client folder not found: {appDirFullPath}");
            }
        }

        Report(WorkflowStage.ValidateInput, WorkflowStageStatus.Success, $"Client folder: {appDirFullPath}");

        var entryExe = ResolveEntryExe(options, remoteManifest, updaterPlan);
        var processName = ResolveProcessName(remoteManifest, updaterPlan, entryExe);

        string? apiKey;
        string? grpcAddress;
        if (updaterPlan is { Env.Count: > 0 })
        {
            Report(WorkflowStage.EnsureApiKey, WorkflowStageStatus.Running, "Applying updater plan environment.");
            Report(WorkflowStage.EnsureGrpcAddress, WorkflowStageStatus.Running, "Applying updater plan environment.");
            Dictionary<string, string> resolvedEnv;
            try
            {
                resolvedEnv = await EnsurePlanEnvironmentAsync(updaterPlan.Env, cancellationToken);
            }
            catch (Exception ex)
            {
                return Fail(WorkflowStage.EnsureApiKey, 10, $"Failed to apply updater plan environment: {ex.Message}");
            }
            if (!resolvedEnv.TryGetValue("AVA_SMENA_API_KEY", out apiKey) || string.IsNullOrWhiteSpace(apiKey))
            {
                return Fail(WorkflowStage.EnsureApiKey, 10, "API key not provided. Update canceled.");
            }

            if (!resolvedEnv.TryGetValue("AVA_SMENA_GRPC_ADDRESS", out grpcAddress) || string.IsNullOrWhiteSpace(grpcAddress))
            {
                return Fail(WorkflowStage.EnsureGrpcAddress, 11, "gRPC address not provided. Update canceled.");
            }
        }
        else
        {
            Report(WorkflowStage.EnsureApiKey, WorkflowStageStatus.Running, "Checking AVA_SMENA_API_KEY.");
            apiKey = await EnsureApiKeyAsync(cancellationToken);
            if (apiKey == null)
            {
                return Fail(WorkflowStage.EnsureApiKey, 10, "API key not provided. Update canceled.");
            }

            Report(WorkflowStage.EnsureApiKey, WorkflowStageStatus.Success, "API key is ready.");

            Report(WorkflowStage.EnsureGrpcAddress, WorkflowStageStatus.Running, "Checking client gRPC address.");
            grpcAddress = await EnsureGrpcAddressAsync(cancellationToken);
            if (grpcAddress == null)
            {
                return Fail(WorkflowStage.EnsureGrpcAddress, 11, "gRPC address not provided. Update canceled.");
            }

            Report(WorkflowStage.EnsureGrpcAddress, WorkflowStageStatus.Success, $"gRPC address is ready: {grpcAddress}");
        }

        var localStatePath = Path.Combine(appDirFullPath, "update.local.json");
        var localVersion = await TryReadLocalVersionAsync(localStatePath, cancellationToken);

        Report(WorkflowStage.CompareVersions, WorkflowStageStatus.Running, "Comparing local and server versions.");
        if (string.Equals(localVersion, remoteManifest.Version, StringComparison.Ordinal))
        {
            Report(WorkflowStage.CompareVersions, WorkflowStageStatus.Success, $"Already up to date: {localVersion}");

            SkipStage(WorkflowStage.EnsureClientStopped, "No update needed.");
            SkipStage(WorkflowStage.DownloadPackage, "No update needed.");
            SkipStage(WorkflowStage.VerifyPackage, "No update needed.");
            SkipStage(WorkflowStage.ExtractPackage, "No update needed.");
            SkipStage(WorkflowStage.ApplyUpdate, "No update needed.");
            SkipStage(WorkflowStage.SaveState, "No update needed.");

        return await FinishAsync(
            updated: false,
            appDirFullPath,
            entryExe,
            apiKey,
            grpcAddress,
            remoteManifest.Version);
        }

        Report(
            WorkflowStage.CompareVersions,
            WorkflowStageStatus.Success,
            $"Update available: {localVersion ?? "none"} -> {remoteManifest.Version}");

        var closeResult = await EnsureClientStoppedAsync(processName, appDirFullPath, cancellationToken);
        if (!closeResult.Success)
        {
            return Fail(WorkflowStage.EnsureClientStopped, 7, closeResult.Message);
        }

        var packageUri = BuildPackageUri(options.ServerUrl, remoteManifest.PackageUrl);
        var tempRoot = Path.Combine(Path.GetTempPath(), "SmenaUpdater", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(tempRoot, "package.zip");
        var extractPath = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(tempRoot);

        var activeStage = WorkflowStage.DownloadPackage;
        try
        {
            Report(WorkflowStage.DownloadPackage, WorkflowStageStatus.Running, "Downloading update package.");
            await DownloadFileAsync(http, packageUri, packagePath, cancellationToken);
            Report(WorkflowStage.DownloadPackage, WorkflowStageStatus.Success, "Package downloaded.");

            activeStage = WorkflowStage.VerifyPackage;
            Report(WorkflowStage.VerifyPackage, WorkflowStageStatus.Running, "Validating package checksum.");
            var packageHash = await ComputeSha256Async(packagePath, cancellationToken);
            if (!string.Equals(packageHash, remoteManifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(WorkflowStage.VerifyPackage, 6, "Package checksum mismatch.");
            }

            Report(WorkflowStage.VerifyPackage, WorkflowStageStatus.Success, "Checksum is valid.");

            activeStage = WorkflowStage.ExtractPackage;
            Report(WorkflowStage.ExtractPackage, WorkflowStageStatus.Running, "Extracting package.");
            await Task.Run(
                () => ZipFile.ExtractToDirectory(packagePath, extractPath, overwriteFiles: true),
                cancellationToken);
            Report(WorkflowStage.ExtractPackage, WorkflowStageStatus.Success, "Package extracted.");

            activeStage = WorkflowStage.ApplyUpdate;
            Report(WorkflowStage.ApplyUpdate, WorkflowStageStatus.Running, "Copying updated files.");
            await Task.Run(() => CopyDirectory(extractPath, appDirFullPath), cancellationToken);
            Report(WorkflowStage.ApplyUpdate, WorkflowStageStatus.Success, "Updated files copied.");

            activeStage = WorkflowStage.SaveState;
            Report(WorkflowStage.SaveState, WorkflowStageStatus.Running, "Writing local update state.");
            var newState = new LocalUpdateState
            {
                Version = remoteManifest.Version,
                AppliedAtUtc = DateTimeOffset.UtcNow
            };

            await File.WriteAllTextAsync(
                localStatePath,
                JsonSerializer.Serialize(newState, jsonOptions),
                cancellationToken);
            Report(WorkflowStage.SaveState, WorkflowStageStatus.Success, $"Saved version: {remoteManifest.Version}");
        }
        catch (Exception ex)
        {
            return Fail(activeStage, 8, $"Update install failed: {ex.Message}");
        }
        finally
        {
            TryDeleteTemp(tempRoot);
        }

        return await FinishAsync(
            updated: true,
            appDirFullPath,
            entryExe,
            apiKey!,
            grpcAddress!,
            remoteManifest.Version);
    }

    private async Task<WorkflowResult> ExecuteReconfigureAsync(CancellationToken cancellationToken)
    {
        Report(WorkflowStage.EnsureApiKey, WorkflowStageStatus.Running, "Checking AVA_SMENA_API_KEY.");
        var apiKey = await EnsureApiKeyAsync(cancellationToken);
        if (apiKey == null)
        {
            return Fail(WorkflowStage.EnsureApiKey, 10, "API key not provided. Reconfigure canceled.");
        }

        Report(WorkflowStage.EnsureApiKey, WorkflowStageStatus.Success, "API key is ready.");

        Report(WorkflowStage.EnsureGrpcAddress, WorkflowStageStatus.Running, "Checking client gRPC address.");
        var grpcAddress = await EnsureGrpcAddressAsync(cancellationToken);
        if (grpcAddress == null)
        {
            return Fail(WorkflowStage.EnsureGrpcAddress, 11, "gRPC address not provided. Reconfigure canceled.");
        }

        Report(WorkflowStage.EnsureGrpcAddress, WorkflowStageStatus.Success, $"gRPC address is ready: {grpcAddress}");

        SkipStage(WorkflowStage.ConnectServer, "Server check is not required in reconfigure mode.");
        SkipStage(WorkflowStage.FetchManifest, "Manifest is not used in reconfigure mode.");
        SkipStage(WorkflowStage.FetchUpdaterPlan, "Updater plan is not used in reconfigure mode.");
        SkipStage(WorkflowStage.CompareVersions, "Version compare is not used in reconfigure mode.");
        SkipStage(WorkflowStage.EnsureClientStopped, "Client stop is not required in reconfigure mode.");
        SkipStage(WorkflowStage.DownloadPackage, "Download is not used in reconfigure mode.");
        SkipStage(WorkflowStage.VerifyPackage, "Verification is not used in reconfigure mode.");
        SkipStage(WorkflowStage.ExtractPackage, "Extraction is not used in reconfigure mode.");
        SkipStage(WorkflowStage.ApplyUpdate, "Apply update is not used in reconfigure mode.");
        SkipStage(WorkflowStage.SaveState, "No local update state changes in reconfigure mode.");

        return new WorkflowResult(0, false, "Configuration updated.");
    }

    private async Task<WorkflowResult> ExecuteUninstallAsync(CancellationToken cancellationToken)
    {
        var appDirFullPath = Path.GetFullPath(options.AppDirectory);

        Report(WorkflowStage.ValidateInput, WorkflowStageStatus.Running, "Validating uninstall options.");
        if (!Directory.Exists(appDirFullPath))
        {
            return Fail(WorkflowStage.ValidateInput, 3, $"Client folder not found: {appDirFullPath}");
        }

        Report(WorkflowStage.ValidateInput, WorkflowStageStatus.Success, $"Client folder: {appDirFullPath}");

        Report(WorkflowStage.ConfirmUninstall, WorkflowStageStatus.Running, "Requesting uninstall confirmation.");
        var confirmed = options.AssumeYes || await interaction.ConfirmUninstallAsync(appDirFullPath);
        if (!confirmed)
        {
            return Fail(WorkflowStage.ConfirmUninstall, 12, "Uninstall canceled.");
        }

        Report(WorkflowStage.ConfirmUninstall, WorkflowStageStatus.Success, "Uninstall confirmed.");

        var entryExe = string.IsNullOrWhiteSpace(options.EntryExeOverride)
            ? "Smena.Client.exe"
            : options.EntryExeOverride;
        var processName = Path.GetFileNameWithoutExtension(entryExe);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return Fail(WorkflowStage.EnsureClientStopped, 7, "Could not determine client process name.");
        }

        var closeResult = await EnsureClientStoppedAsync(processName, appDirFullPath, cancellationToken);
        if (!closeResult.Success)
        {
            return Fail(WorkflowStage.EnsureClientStopped, 7, closeResult.Message);
        }

        Report(WorkflowStage.RemoveClientFiles, WorkflowStageStatus.Running, "Removing client files.");
        try
        {
            Directory.Delete(appDirFullPath, recursive: true);
        }
        catch (Exception ex)
        {
            return Fail(WorkflowStage.RemoveClientFiles, 13, $"Failed to remove client folder: {ex.Message}");
        }

        Report(WorkflowStage.RemoveClientFiles, WorkflowStageStatus.Success, "Client files removed.");

        Report(WorkflowStage.ClearConfiguration, WorkflowStageStatus.Running, "Clearing saved configuration.");
        ClearSavedConfiguration();
        Report(WorkflowStage.ClearConfiguration, WorkflowStageStatus.Success, "Saved configuration cleared.");

        SkipStage(WorkflowStage.ConnectServer, "Server check is not required in uninstall mode.");
        SkipStage(WorkflowStage.FetchManifest, "Manifest is not used in uninstall mode.");
        SkipStage(WorkflowStage.FetchUpdaterPlan, "Updater plan is not used in uninstall mode.");
        SkipStage(WorkflowStage.CompareVersions, "Version compare is not used in uninstall mode.");
        SkipStage(WorkflowStage.DownloadPackage, "Download is not used in uninstall mode.");
        SkipStage(WorkflowStage.VerifyPackage, "Verification is not used in uninstall mode.");
        SkipStage(WorkflowStage.ExtractPackage, "Extraction is not used in uninstall mode.");
        SkipStage(WorkflowStage.ApplyUpdate, "Apply update is not used in uninstall mode.");
        SkipStage(WorkflowStage.SaveState, "Local update state is removed with client directory.");
        SkipStage(WorkflowStage.LaunchClient, "Launch is not used in uninstall mode.");

        return new WorkflowResult(0, false, "Client uninstalled.");
    }

    private async Task<WorkflowResult> FinishAsync(
        bool updated,
        string appDirFullPath,
        string entryExe,
        string apiKey,
        string grpcAddress,
        string version)
    {
        if (options.NoLaunch)
        {
            SkipStage(WorkflowStage.LaunchClient, "Launch skipped by --no-launch.");
            var message = updated
                ? $"Updated to version: {version}"
                : $"Already up to date: {version}";
            return new WorkflowResult(0, updated, message);
        }

        Report(WorkflowStage.LaunchClient, WorkflowStageStatus.Running, "Starting client.");
        var launchCode = LaunchClient(appDirFullPath, entryExe, apiKey, grpcAddress);
        if (launchCode != 0)
        {
            return Fail(WorkflowStage.LaunchClient, launchCode, "Failed to launch client.");
        }

        Report(WorkflowStage.LaunchClient, WorkflowStageStatus.Success, "Client started.");
        var finalMessage = updated
            ? $"Updated to version: {version}"
            : $"Already up to date: {version}";
        return new WorkflowResult(0, updated, finalMessage);
    }

    private async Task<string?> EnsureApiKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(options.ApiKeyOverride))
        {
            SaveApiKey(options.ApiKeyOverride);
            return options.ApiKeyOverride;
        }

        var existing =
            Environment.GetEnvironmentVariable("AVA_SMENA_API_KEY", EnvironmentVariableTarget.Process) ??
            Environment.GetEnvironmentVariable("AVA_SMENA_API_KEY", EnvironmentVariableTarget.User) ??
            Environment.GetEnvironmentVariable("AVA_SMENA_API_KEY");

        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var entered = await interaction.PromptApiKeyAsync();
        if (string.IsNullOrWhiteSpace(entered))
        {
            return null;
        }

        SaveApiKey(entered);
        return entered;
    }

    private async Task<string?> EnsureGrpcAddressAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(options.GrpcAddressOverride))
        {
            SaveGrpcAddress(options.GrpcAddressOverride);
            return options.GrpcAddressOverride;
        }

        var existing =
            Environment.GetEnvironmentVariable("AVA_SMENA_GRPC_ADDRESS", EnvironmentVariableTarget.Process) ??
            Environment.GetEnvironmentVariable("AVA_SMENA_GRPC_ADDRESS", EnvironmentVariableTarget.User) ??
            Environment.GetEnvironmentVariable("AVA_SMENA_GRPC_ADDRESS") ??
            Environment.GetEnvironmentVariable("Grpc__Address");

        if (!string.IsNullOrWhiteSpace(existing) &&
            UpdaterOptions.TryNormalizeGrpcAddress(existing, out var normalizedExisting, out _))
        {
            return normalizedExisting;
        }

        var entered = await interaction.PromptGrpcAddressAsync();
        if (string.IsNullOrWhiteSpace(entered))
        {
            return null;
        }

        if (!UpdaterOptions.TryNormalizeGrpcAddress(entered, out var normalizedEntered, out _))
        {
            return null;
        }

        SaveGrpcAddress(normalizedEntered);
        return normalizedEntered;
    }

    private async Task<Dictionary<string, string>> EnsurePlanEnvironmentAsync(
        IReadOnlyCollection<UpdaterPlanEnvVariable> envVariables,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var variable in envVariables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                continue;
            }

            var name = variable.Name.Trim();
            var value =
                Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process) ??
                Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ??
                Environment.GetEnvironmentVariable(name) ??
                variable.DefaultValue;

            if (string.IsNullOrWhiteSpace(value) && variable.Required)
            {
                var promptText = string.IsNullOrWhiteSpace(variable.Prompt)
                    ? $"Введите значение {name}"
                    : variable.Prompt;
                value = await interaction.PromptEnvironmentVariableAsync(
                    name,
                    promptText,
                    variable.Secret,
                    variable.DefaultValue);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                if (variable.Required)
                {
                    throw new InvalidOperationException($"Environment variable is required but empty: {name}");
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(variable.ValidationPattern) &&
                !System.Text.RegularExpressions.Regex.IsMatch(value, variable.ValidationPattern))
            {
                throw new InvalidOperationException($"Environment variable '{name}' does not match validation pattern.");
            }

            SaveEnvironmentVariable(name, value);
            resolved[name] = value;
        }

        Report(WorkflowStage.EnsureApiKey, WorkflowStageStatus.Success, "Environment from updater plan is ready.");
        Report(WorkflowStage.EnsureGrpcAddress, WorkflowStageStatus.Success, "Environment from updater plan is ready.");

        return resolved;
    }

    private async Task<(bool Success, string Message)> EnsureClientStoppedAsync(
        string processName,
        string appDirFullPath,
        CancellationToken cancellationToken)
    {
        Report(WorkflowStage.EnsureClientStopped, WorkflowStageStatus.Running, "Checking running client process.");

        var running = GetTargetProcesses(processName, appDirFullPath);
        if (running.Count == 0)
        {
            Report(WorkflowStage.EnsureClientStopped, WorkflowStageStatus.Success, "Client is not running.");
            return (true, string.Empty);
        }

        var approved = await interaction.ConfirmCloseClientAsync(processName, running.Count);
        if (!approved)
        {
            return (false, "Update canceled: client close was not approved.");
        }

        RequestGracefulClose(running);
        var gracefulClosed = await WaitForProcessExitAsync(
            processName,
            appDirFullPath,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (gracefulClosed)
        {
            Report(WorkflowStage.EnsureClientStopped, WorkflowStageStatus.Success, "Client closed.");
            return (true, string.Empty);
        }

        running = GetTargetProcesses(processName, appDirFullPath);
        var forceApproved = await interaction.ConfirmForceCloseClientAsync(processName, running.Count);
        if (!forceApproved)
        {
            return (false, "Update canceled: client is still running.");
        }

        ForceClose(running);
        var forceClosed = await WaitForProcessExitAsync(
            processName,
            appDirFullPath,
            TimeSpan.FromSeconds(10),
            cancellationToken);
        if (!forceClosed)
        {
            return (false, "Could not close client process.");
        }

        Report(WorkflowStage.EnsureClientStopped, WorkflowStageStatus.Success, "Client force-closed.");
        return (true, string.Empty);
    }

    private static void RequestGracefulClose(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                }
            }
            catch
            {
                // Ignore process access errors.
            }
        }
    }

    private static void ForceClose(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore process access errors.
            }
        }
    }

    private static List<Process> GetTargetProcesses(string processName, string appDirFullPath)
    {
        var normalizedAppDir = NormalizeDir(appDirFullPath);

        return Process.GetProcessesByName(processName)
            .Where(process => process.Id != Environment.ProcessId)
            .Where(process => IsTargetProcess(process, normalizedAppDir))
            .ToList();
    }

    private static bool IsTargetProcess(Process process, string normalizedAppDir)
    {
        try
        {
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return true;
            }

            var processDir = NormalizeDir(Path.GetDirectoryName(executablePath)!);
            return string.Equals(processDir, normalizedAppDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static string NormalizeDir(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static async Task<bool> WaitForProcessExitAsync(
        string processName,
        string appDirFullPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (GetTargetProcesses(processName, appDirFullPath).Count == 0)
            {
                return true;
            }

            await Task.Delay(400, cancellationToken);
        }

        return false;
    }

    private static async Task<string?> TryReadLocalVersionAsync(
        string localStatePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(localStatePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(localStatePath, cancellationToken);
            var state = JsonSerializer.Deserialize<LocalUpdateState>(json);
            return state?.Version;
        }
        catch
        {
            return null;
        }
    }

    private static async Task DownloadFileAsync(
        HttpClient client,
        Uri uri,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        foreach (var sourcePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, sourcePath);
            var destinationPath = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            try
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
            catch (IOException)
            {
                // Skip locked files (usually updater itself).
            }
        }
    }

    private static string BuildManifestUrl(string serverUrl)
    {
        return $"{serverUrl.TrimEnd('/')}/manifest.json";
    }

    private static string BuildHealthUrl(string serverUrl)
    {
        return $"{serverUrl.TrimEnd('/')}/healthz";
    }

    private static Uri BuildPackageUri(string serverUrl, string packageUrl)
    {
        if (Uri.TryCreate(packageUrl, UriKind.Absolute, out var absolute))
        {
            if (!string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Manifest packageUrl must use HTTPS: {packageUrl}");
            }

            return absolute;
        }

        return new Uri($"{serverUrl.TrimEnd('/')}/{packageUrl.TrimStart('/')}");
    }

    private static Uri BuildPlanUri(string serverUrl, string updaterPlanUrl)
    {
        if (Uri.TryCreate(updaterPlanUrl, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        return new Uri($"{serverUrl.TrimEnd('/')}/{updaterPlanUrl.TrimStart('/')}");
    }

    private static string ResolveAppDirectory(string cliAppDirectory, UpdaterPlanApp? appPlan)
    {
        if (!string.IsNullOrWhiteSpace(cliAppDirectory))
        {
            return Path.GetFullPath(cliAppDirectory);
        }

        var policy = appPlan?.AppDirPolicy?.Trim();
        if (string.Equals(policy, "relativeToUpdater", StringComparison.OrdinalIgnoreCase))
        {
            var updaterBaseDir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
                ?? AppContext.BaseDirectory;
            var relativePath = string.IsNullOrWhiteSpace(appPlan?.AppDirRelativePath)
                ? "client"
                : appPlan.AppDirRelativePath!.Trim();
            return Path.GetFullPath(Path.Combine(updaterBaseDir, relativePath));
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "client"));
    }

    private static string ResolveEntryExe(UpdaterOptions updaterOptions, RemoteManifest manifest, UpdaterPlan? plan)
    {
        if (!string.IsNullOrWhiteSpace(updaterOptions.EntryExeOverride))
        {
            return updaterOptions.EntryExeOverride;
        }

        if (!string.IsNullOrWhiteSpace(manifest.EntryExe))
        {
            return manifest.EntryExe;
        }

        if (!string.IsNullOrWhiteSpace(plan?.App?.EntryExe))
        {
            return plan.App.EntryExe;
        }

        return "Smena.Client.exe";
    }

    private static string ResolveProcessName(RemoteManifest manifest, UpdaterPlan? plan, string entryExe)
    {
        if (!string.IsNullOrWhiteSpace(manifest.ProcessName))
        {
            return manifest.ProcessName;
        }

        if (!string.IsNullOrWhiteSpace(plan?.App?.ProcessName))
        {
            return plan.App.ProcessName;
        }

        return Path.GetFileNameWithoutExtension(entryExe) ?? "Smena.Client";
    }

    private static int LaunchClient(string appDirFullPath, string entryExe, string apiKey, string grpcAddress)
    {
        var exePath = Path.Combine(appDirFullPath, entryExe);
        if (!File.Exists(exePath))
        {
            return 9;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = appDirFullPath,
                UseShellExecute = false
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                startInfo.Environment["AVA_SMENA_API_KEY"] = apiKey;
            }

            if (!string.IsNullOrWhiteSpace(grpcAddress))
            {
                startInfo.Environment["AVA_SMENA_GRPC_ADDRESS"] = grpcAddress;
                startInfo.Environment["Grpc__Address"] = grpcAddress;
            }

            Process.Start(startInfo);
            return 0;
        }
        catch
        {
            return 9;
        }
    }

    private static void SaveApiKey(string apiKey)
    {
        SaveEnvironmentVariable("AVA_SMENA_API_KEY", apiKey);
    }

    private static void SaveGrpcAddress(string grpcAddress)
    {
        SaveEnvironmentVariable("AVA_SMENA_GRPC_ADDRESS", grpcAddress);
        SaveEnvironmentVariable("Grpc__Address", grpcAddress);
    }

    private static void SaveEnvironmentVariable(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
    }

    private static void ClearSavedConfiguration()
    {
        Environment.SetEnvironmentVariable("AVA_SMENA_API_KEY", null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("AVA_SMENA_API_KEY", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("AVA_SMENA_GRPC_ADDRESS", null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("AVA_SMENA_GRPC_ADDRESS", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("Grpc__Address", null, EnvironmentVariableTarget.Process);
    }

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors.
        }
    }

    private void Report(WorkflowStage stage, WorkflowStageStatus status, string message)
    {
        progress.Report(new WorkflowProgressUpdate(stage, status, message));
    }

    private void SkipStage(WorkflowStage stage, string message)
    {
        Report(stage, WorkflowStageStatus.Skipped, message);
    }

    private WorkflowResult Fail(WorkflowStage stage, int exitCode, string message)
    {
        Report(stage, WorkflowStageStatus.Failed, message);
        return new WorkflowResult(exitCode, false, message);
    }
}

internal sealed class RemoteManifest
{
    public string Version { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string? EntryExe { get; set; }
    public string? ProcessName { get; set; }
    public string? UpdaterPlanUrl { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
}

internal sealed class UpdaterPlan
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset? GeneratedAtUtc { get; set; }
    public UpdaterPlanApp? App { get; set; }
    public List<UpdaterPlanEnvVariable> Env { get; set; } = [];
}

internal sealed class UpdaterPlanApp
{
    public string? EntryExe { get; set; }
    public string? ProcessName { get; set; }
    public string? AppDirPolicy { get; set; }
    public string? AppDirRelativePath { get; set; }
    public bool CreateAppDirIfMissing { get; set; }
}

internal sealed class UpdaterPlanEnvVariable
{
    public string Name { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
    public bool Secret { get; set; }
    public string? ValidationPattern { get; set; }
    public string? DefaultValue { get; set; }
}

internal sealed class LocalUpdateState
{
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset AppliedAtUtc { get; set; }
}
