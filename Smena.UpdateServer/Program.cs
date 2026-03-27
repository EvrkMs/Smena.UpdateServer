using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<UpdateServerOptions>(
    builder.Configuration.GetSection(UpdateServerOptions.SectionName));
builder.Services.AddHealthChecks();

var app = builder.Build();
var logger = app.Logger;

var options = app.Services.GetRequiredService<IOptions<UpdateServerOptions>>().Value;
var jsonOptions = BuildJsonOptions();
var updatesRoot = ResolvePath(options.UpdatesPath, app.Environment.ContentRootPath);
var publishedClientRoot = ResolvePath(options.PublishedClientPath, app.Environment.ContentRootPath);
var publishedUpdaterRoot = ResolvePath(options.PublishedUpdaterPath, app.Environment.ContentRootPath);
var updaterPlanPath = Path.Combine(updatesRoot, options.UpdaterPlanFileName);

Directory.CreateDirectory(updatesRoot);
Directory.CreateDirectory(Path.Combine(updatesRoot, options.PackagesFolderName));
Directory.CreateDirectory(publishedClientRoot);
Directory.CreateDirectory(publishedUpdaterRoot);

try
{
    await BuildUpdaterPlanAsync(updatesRoot, options, jsonOptions, app.Lifetime.ApplicationStopping);
}
catch (Exception ex)
{
    logger.LogError(ex, "Failed to build updater plan.");
}

if (options.RebuildOnStartup)
{
    try
    {
        var rebuilt = await BuildClientUpdateAsync(
            publishedClientRoot,
            updatesRoot,
            options,
            jsonOptions,
            logger,
            app.Lifetime.ApplicationStopping);

        if (!rebuilt)
        {
            logger.LogWarning("Client package was not rebuilt because published client folder is empty: {Path}", publishedClientRoot);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to build client update package.");
    }

    try
    {
        var updaterRebuilt = await BuildUpdaterUpdateAsync(
            publishedUpdaterRoot,
            updatesRoot,
            options,
            jsonOptions,
            logger,
            app.Lifetime.ApplicationStopping);

        if (!updaterRebuilt)
        {
            logger.LogWarning("Updater package was not rebuilt because published updater folder is empty: {Path}", publishedUpdaterRoot);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to build updater update package.");
    }
}

app.MapHealthChecks("/healthz");

app.MapGet("/", () => Results.Json(new
{
    service = "Smena.UpdateServer",
    status = "ok",
    publishedClientPath = publishedClientRoot,
    updatesPath = updatesRoot,
    manifest = "/manifest.json",
    updaterPlan = "/updater.plan.json"
}));

app.MapGet("/manifest.json", async () =>
{
    var manifestPath = Path.Combine(updatesRoot, options.ManifestFileName);
    if (!File.Exists(manifestPath))
    {
        return Results.NotFound(new { error = "manifest_not_found" });
    }

    var content = await File.ReadAllTextAsync(manifestPath);
    try
    {
        using var _ = JsonDocument.Parse(content);
    }
    catch
    {
        return Results.Problem("Invalid manifest JSON.", statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Text(content, "application/json");
});

app.MapGet("/updater.plan.json", async () =>
{
    if (!File.Exists(updaterPlanPath))
    {
        return Results.NotFound(new { error = "updater_plan_not_found" });
    }

    var content = await File.ReadAllTextAsync(updaterPlanPath);
    try
    {
        using var _ = JsonDocument.Parse(content);
    }
    catch
    {
        return Results.Problem("Invalid updater plan JSON.", statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Text(content, "application/json");
});

app.MapGet("/updater-manifest.json", async () =>
{
    var updaterManifestPath = Path.Combine(updatesRoot, options.UpdaterManifestFileName);
    if (!File.Exists(updaterManifestPath))
    {
        return Results.NotFound(new { error = "updater_manifest_not_found" });
    }

    var content = await File.ReadAllTextAsync(updaterManifestPath);
    try
    {
        using var _ = JsonDocument.Parse(content);
    }
    catch
    {
        return Results.Problem("Invalid updater manifest JSON.", statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Text(content, "application/json");
});

// Serve update artifacts from /updates/client/*
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(updatesRoot),
    RequestPath = "/updates/client",
    ContentTypeProvider = BuildContentTypeProvider()
});

app.Run();

static string ResolvePath(string configuredPath, string contentRoot)
{
    if (Path.IsPathRooted(configuredPath))
    {
        return configuredPath;
    }

    return Path.GetFullPath(Path.Combine(contentRoot, configuredPath));
}

static FileExtensionContentTypeProvider BuildContentTypeProvider()
{
    var provider = new FileExtensionContentTypeProvider();
    provider.Mappings[".json"] = "application/json";
    provider.Mappings[".sha256"] = "text/plain";
    provider.Mappings[".zip"] = "application/zip";
    return provider;
}

static async Task<bool> BuildClientUpdateAsync(
    string publishedClientRoot,
    string updatesRoot,
    UpdateServerOptions options,
    JsonSerializerOptions jsonOptions,
    ILogger logger,
    CancellationToken ct)
{
    var files = Directory.GetFiles(publishedClientRoot, "*", SearchOption.AllDirectories)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (files.Count == 0)
    {
        return false;
    }

    var version = await ComputeDirectoryHashAsync(publishedClientRoot, files, ct);
    var packagesRoot = Path.Combine(updatesRoot, options.PackagesFolderName);
    var packageFileName = $"{options.PackagePrefix}-{version}.zip";
    var packagePath = Path.Combine(packagesRoot, packageFileName);

    foreach (var existing in Directory.GetFiles(packagesRoot, $"{options.PackagePrefix}-*.zip", SearchOption.TopDirectoryOnly))
    {
        if (!string.Equals(existing, packagePath, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(existing);
        }
    }

    if (File.Exists(packagePath))
    {
        File.Delete(packagePath);
    }

    ZipFile.CreateFromDirectory(
        publishedClientRoot,
        packagePath,
        CompressionLevel.Optimal,
        includeBaseDirectory: false);

    var zipSha = await ComputeFileHashAsync(packagePath, ct);
    var manifest = new ClientUpdateManifest
    {
        Version = version,
        PackageUrl = $"/updates/client/{options.PackagesFolderName}/{packageFileName}",
        Sha256 = zipSha,
        EntryExe = options.EntryExe,
        ProcessName = options.ProcessName,
        UpdaterPlanUrl = $"/{options.UpdaterPlanFileName}",
        UpdaterManifestUrl = $"/{options.UpdaterManifestFileName}",
        PublishedAtUtc = DateTimeOffset.UtcNow
    };

    var manifestPath = Path.Combine(updatesRoot, options.ManifestFileName);
    await File.WriteAllTextAsync(
        manifestPath,
        JsonSerializer.Serialize(manifest, jsonOptions),
        ct);

    logger.LogInformation(
        "Client update package built. Version: {Version}; Package: {Package}",
        manifest.Version,
        packagePath);

    return true;
}

static async Task<bool> BuildUpdaterUpdateAsync(
    string publishedUpdaterRoot,
    string updatesRoot,
    UpdateServerOptions options,
    JsonSerializerOptions jsonOptions,
    ILogger logger,
    CancellationToken ct)
{
    var files = Directory.GetFiles(publishedUpdaterRoot, "*", SearchOption.AllDirectories)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (files.Count == 0)
    {
        return false;
    }

    var version = await ComputeDirectoryHashAsync(publishedUpdaterRoot, files, ct);
    var packagesRoot = Path.Combine(updatesRoot, options.PackagesFolderName);
    var packageFileName = $"{options.UpdaterPackagePrefix}-{version}.zip";
    var packagePath = Path.Combine(packagesRoot, packageFileName);

    foreach (var existing in Directory.GetFiles(packagesRoot, $"{options.UpdaterPackagePrefix}-*.zip", SearchOption.TopDirectoryOnly))
    {
        if (!string.Equals(existing, packagePath, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(existing);
        }
    }

    if (File.Exists(packagePath))
    {
        File.Delete(packagePath);
    }

    ZipFile.CreateFromDirectory(
        publishedUpdaterRoot,
        packagePath,
        CompressionLevel.Optimal,
        includeBaseDirectory: false);

    var zipSha = await ComputeFileHashAsync(packagePath, ct);
    var updaterManifest = new UpdaterUpdateManifest
    {
        Version = version,
        PackageUrl = $"/updates/client/{options.PackagesFolderName}/{packageFileName}",
        Sha256 = zipSha,
        EntryExe = options.UpdaterEntryExe,
        PublishedAtUtc = DateTimeOffset.UtcNow
    };

    var updaterManifestPath = Path.Combine(updatesRoot, options.UpdaterManifestFileName);
    await File.WriteAllTextAsync(
        updaterManifestPath,
        JsonSerializer.Serialize(updaterManifest, jsonOptions),
        ct);

    logger.LogInformation(
        "Updater package built. Version: {Version}; Package: {Package}",
        updaterManifest.Version,
        packagePath);

    return true;
}

static async Task BuildUpdaterPlanAsync(
    string updatesRoot,
    UpdateServerOptions options,
    JsonSerializerOptions jsonOptions,
    CancellationToken ct)
{
    var plan = new UpdaterPlan
    {
        SchemaVersion = 1,
        GeneratedAtUtc = DateTimeOffset.UtcNow,
        App = new UpdaterAppDefinition
        {
            EntryExe = options.EntryExe,
            ProcessName = options.ProcessName,
            AppDirPolicy = options.AppDirPolicy,
            AppDirRelativePath = options.AppDirRelativePath,
            CreateAppDirIfMissing = options.CreateAppDirIfMissing
        },
        Env = GetEffectiveEnvDefinitions(options)
    };

    var updaterPlanPath = Path.Combine(updatesRoot, options.UpdaterPlanFileName);
    await File.WriteAllTextAsync(
        updaterPlanPath,
        JsonSerializer.Serialize(plan, jsonOptions),
        ct);
}

static List<UpdaterEnvVariableDefinition> GetEffectiveEnvDefinitions(UpdateServerOptions options)
{
    if (options.UpdaterEnv is { Count: > 0 })
    {
        return options.UpdaterEnv
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new UpdaterEnvVariableDefinition
            {
                Name = x.Name.Trim(),
                Prompt = x.Prompt?.Trim() ?? string.Empty,
                Required = x.Required,
                Secret = x.Secret,
                ValidationPattern = x.ValidationPattern?.Trim(),
                DefaultValue = x.DefaultValue
            })
            .ToList();
    }

    return
    [
        new UpdaterEnvVariableDefinition
        {
            Name = "AVA_SMENA_API_KEY",
            Prompt = "Введите API ключ",
            Required = true,
            Secret = true
        },
        new UpdaterEnvVariableDefinition
        {
            Name = "AVA_SMENA_GRPC_ADDRESS",
            Prompt = "Введите адрес gRPC сервера (https://host:port)",
            Required = true,
            Secret = false,
            ValidationPattern = "^https?://.+"
        }
    ];
}

static async Task<string> ComputeDirectoryHashAsync(
    string root,
    IReadOnlyList<string> files,
    CancellationToken ct)
{
    var buffer = new StringBuilder();
    foreach (var file in files)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        var fileHash = await ComputeFileHashAsync(file, ct);
        buffer.Append(relative).Append('|').Append(fileHash).Append('\n');
    }

    var bytes = Encoding.UTF8.GetBytes(buffer.ToString());
    var hash = SHA256.HashData(bytes);
    return WebEncoders.Base64UrlEncode(hash);
}

static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
{
    await using var stream = File.OpenRead(filePath);
    var hash = await SHA256.HashDataAsync(stream, ct);
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static JsonSerializerOptions BuildJsonOptions() => new(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};

internal sealed class UpdateServerOptions
{
    public const string SectionName = "UpdateServer";

    public string UpdatesPath { get; set; } = "updates/client";
    public string PublishedClientPath { get; set; } = "updates/published-client";
    public string PublishedUpdaterPath { get; set; } = "updates/published-updater";
    public string ManifestFileName { get; set; } = "manifest.json";
    public string UpdaterManifestFileName { get; set; } = "updater-manifest.json";
    public string UpdaterPlanFileName { get; set; } = "updater.plan.json";
    public string PackagesFolderName { get; set; } = "packages";
    public string PackagePrefix { get; set; } = "client";
    public string UpdaterPackagePrefix { get; set; } = "updater";
    public string EntryExe { get; set; } = "Smena.Client.exe";
    public string ProcessName { get; set; } = "Smena.Client";
    public string UpdaterEntryExe { get; set; } = "Smena.Updater.exe";
    public string AppDirPolicy { get; set; } = "relativeToUpdater";
    public string AppDirRelativePath { get; set; } = "client";
    public bool CreateAppDirIfMissing { get; set; } = true;
    public List<UpdaterEnvVariableDefinition> UpdaterEnv { get; set; } = [];
    public bool RebuildOnStartup { get; set; } = true;
}

internal sealed class ClientUpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("packageUrl")]
    public string PackageUrl { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("entryExe")]
    public string EntryExe { get; set; } = string.Empty;

    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = string.Empty;

    [JsonPropertyName("updaterPlanUrl")]
    public string UpdaterPlanUrl { get; set; } = string.Empty;

    [JsonPropertyName("updaterManifestUrl")]
    public string UpdaterManifestUrl { get; set; } = string.Empty;

    [JsonPropertyName("publishedAtUtc")]
    public DateTimeOffset PublishedAtUtc { get; set; }
}

internal sealed class UpdaterUpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("packageUrl")]
    public string PackageUrl { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("entryExe")]
    public string EntryExe { get; set; } = string.Empty;

    [JsonPropertyName("publishedAtUtc")]
    public DateTimeOffset PublishedAtUtc { get; set; }
}

internal sealed class UpdaterPlan
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; set; }

    [JsonPropertyName("app")]
    public UpdaterAppDefinition App { get; set; } = new();

    [JsonPropertyName("env")]
    public List<UpdaterEnvVariableDefinition> Env { get; set; } = [];
}

internal sealed class UpdaterAppDefinition
{
    [JsonPropertyName("entryExe")]
    public string EntryExe { get; set; } = "Smena.Client.exe";

    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "Smena.Client";

    [JsonPropertyName("appDirPolicy")]
    public string AppDirPolicy { get; set; } = "relativeToUpdater";

    [JsonPropertyName("appDirRelativePath")]
    public string AppDirRelativePath { get; set; } = "client";

    [JsonPropertyName("createAppDirIfMissing")]
    public bool CreateAppDirIfMissing { get; set; } = true;
}

internal sealed class UpdaterEnvVariableDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    [JsonPropertyName("secret")]
    public bool Secret { get; set; }

    [JsonPropertyName("validationPattern")]
    public string? ValidationPattern { get; set; }

    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; set; }
}
