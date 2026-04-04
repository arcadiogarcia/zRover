using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using zRover.BackgroundManager.Packages;
using zRover.BackgroundManager.Sessions;
using zRover.Core;

namespace zRover.BackgroundManager.Server;

/// <summary>
/// Registers device-level MSIX package management tools in the master MCP adapter.
///
/// Unlike the session-level tools (which route to the <em>active session</em>), these
/// tools operate on the <em>device layer</em> of any specific machine in the federation
/// and use the <c>deviceId</c> parameter to select the target:
/// <list type="bullet">
///   <item><c>null</c> / <c>"local"</c> → this machine</item>
///   <item><c>"a1b2"</c> → direct remote manager one hop away</item>
///   <item><c>"a1b2:c3d4"</c> → remote manager two hops away, routed via <c>a1b2</c></item>
/// </list>
///
/// Package upload (staging) uses a dedicated HTTP endpoint rather than MCP binary
/// payloads.  The <c>request_package_upload</c> tool returns a pre-signed single-use
/// URL; the caller POSTs the raw MSIX bytes there.  For multi-hop chains, this manager
/// creates a forwarding staging entry so the upload travels hop-by-hop and is verified
/// at every step.
/// </summary>
public static class DevicePackageManagementTools
{
    // ── Schema literals ────────────────────────────────────────────────────────
    // These are inline JSON Schema strings kept close to the tool registration so
    // they are easy to update when the schema changes.

    private const string DeviceIdProp = """
        "deviceId": {
          "type": "string",
          "description": "Target device ID from list_devices. Omit or pass null for the local machine."
        }
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  Register
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Registers all device package management tools in <paramref name="registry"/>.
    /// </summary>
    public static void Register(
        IMcpToolRegistry registry,
        IDevicePackageManager localPackageManager,
        PackageStagingManager stagingManager,
        RemoteManagerRegistry remoteManagers,
        ExternalAccessManager externalAccess,
        ILogger logger)
    {
        RegisterListDevices(registry, remoteManagers);
        RegisterListInstalledPackages(registry, localPackageManager, remoteManagers, logger);
        RegisterInstallPackage(registry, localPackageManager, remoteManagers, logger);
        RegisterUninstallPackage(registry, localPackageManager, remoteManagers, logger);
        RegisterLaunchApp(registry, localPackageManager, remoteManagers, logger);
        RegisterStopApp(registry, localPackageManager, remoteManagers, logger);
        RegisterGetPackageInfo(registry, localPackageManager, remoteManagers, logger);
        RegisterRequestPackageUpload(registry, stagingManager, remoteManagers, externalAccess, logger);
        RegisterGetPackageStageStatus(registry, stagingManager, remoteManagers, logger);
        RegisterDiscardPackageStage(registry, stagingManager, remoteManagers, logger);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  list_devices
    // ══════════════════════════════════════════════════════════════════════════

    private static void RegisterListDevices(
        IMcpToolRegistry registry,
        RemoteManagerRegistry remoteManagers)
    {
        registry.RegisterTool(
            "list_devices",
            "Lists all devices available for remote MSIX package management. " +
            "Returns the local machine (deviceId: 'local') plus any managers connected via federation. " +
            "Use the returned deviceId values in install_package, list_installed_packages, etc.",
            """{"type":"object","properties":{}}""",
            _ =>
            {
                var devices = new List<object>
                {
                    new { deviceId = "local", displayName = "This device", alias = (string?)null,
                          isConnected = true, hops = 0 }
                };

                foreach (var mgr in remoteManagers.Managers)
                {
                    devices.Add(new
                    {
                        deviceId    = mgr.ManagerId,
                        displayName = $"{mgr.Alias} ({mgr.ManagerId})",
                        alias       = mgr.Alias,
                        isConnected = mgr.IsConnected,
                        hops        = 1,
                    });
                }

                return Task.FromResult(JsonSerializer.Serialize(new { devices }, JsonOpts));
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  list_installed_packages
    // ══════════════════════════════════════════════════════════════════════════

    private static void RegisterListInstalledPackages(
        IMcpToolRegistry registry,
        IDevicePackageManager local,
        RemoteManagerRegistry remoteManagers,
        ILogger logger)
    {
        registry.RegisterTool(
            "list_installed_packages",
            "Lists MSIX packages installed on a device. " +
            "Supports filtering by name and optionally includes framework/system packages.",
            $$"""
            {
              "type": "object",
              "properties": {
                {{DeviceIdProp}},
                "nameFilter": {
                  "type": "string",
                  "description": "Case-insensitive substring filter on display name or package family name."
                },
                "includeFrameworks": {
                  "type": "boolean",
                  "description": "Include framework and runtime packages. Default: false."
                },
                "includeSystemPackages": {
                  "type": "boolean",
                  "description": "Include system/provisioned packages. Default: false."
                }
              }
            }
            """,
            async argsJson =>
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                var deviceId          = GetString(root, "deviceId");
                var nameFilter        = GetString(root, "nameFilter");
                var includeFrameworks = GetBool(root, "includeFrameworks", false);
                var includeSys        = GetBool(root, "includeSystemPackages", false);

                if (IsRemote(deviceId, out var remoteId))
                {
                    return await remoteManagers.RouteDeviceToolAsync(
                        remoteId!, "list_installed_packages",
                        BuildArgs(root, excludeDeviceId: true), logger);
                }

                var packages = await local.ListInstalledPackagesAsync(
                    nameFilter, includeFrameworks, includeSys);

                return JsonSerializer.Serialize(new { packages }, JsonOpts);
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  install_package
    // ══════════════════════════════════════════════════════════════════════════

    private static void RegisterInstallPackage(
        IMcpToolRegistry registry,
        IDevicePackageManager local,
        RemoteManagerRegistry remoteManagers,
        ILogger logger)
    {
        registry.RegisterTool(
            "install_package",
            "Installs or updates an MSIX package on a device. " +
            "Supported packageUri formats: https:// URL, file:// local path, " +
            "ms-appinstaller:// URI for .appinstaller manifests, or " +
            "staged://{stagingId} from a prior request_package_upload call.",
            $$"""
            {
              "type": "object",
              "required": ["packageUri"],
              "properties": {
                {{DeviceIdProp}},
                "packageUri": {
                  "type": "string",
                  "description": "https://, file://, ms-appinstaller://, or staged://{stagingId}."
                },
                "dependencyUris": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional explicit dependency package URIs. Usually not needed — Windows resolves these automatically."
                },
                "forceAppShutdown": {
                  "type": "boolean",
                  "description": "Force-close running instances before installing. Default: false."
                },
                "allowUnsigned": {
                  "type": "boolean",
                  "description": "Allow packages not signed by a trusted certificate (requires Developer Mode). Default: false."
                },
                "installForAllUsers": {
                  "type": "boolean",
                  "description": "Install for all users. Requires packageManagement restricted capability. Default: false."
                },
                "deferRegistration": {
                  "type": "boolean",
                  "description": "Stage but defer registration to next app launch. Default: false."
                }
              }
            }
            """,
            async argsJson =>
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                var deviceId    = GetString(root, "deviceId");
                var packageUri  = GetString(root, "packageUri") ?? "";
                var depUris     = GetStringArray(root, "dependencyUris");

                // For staged:// URIs with a remote device, we route via the staging chain
                // (the downstream stagingId is embedded inside the forwarding entry on this manager)
                if (IsRemote(deviceId, out var remoteId))
                {
                    return await remoteManagers.RouteDeviceToolAsync(
                        remoteId!, "install_package",
                        BuildArgsWithDownstreamStaging(root, packageUri), logger);
                }

                var options = new InstallOptions
                {
                    DependencyUris    = depUris,
                    ForceAppShutdown  = GetBool(root, "forceAppShutdown", false),
                    AllowUnsigned     = GetBool(root, "allowUnsigned", false),
                    InstallForAllUsers = GetBool(root, "installForAllUsers", false),
                    DeferRegistration = GetBool(root, "deferRegistration", false),
                };

                var result = await local.InstallPackageAsync(packageUri, options);
                return JsonSerializer.Serialize(result, JsonOpts);
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  uninstall_package
    // ══════════════════════════════════════════════════════════════════════════

    private static void RegisterUninstallPackage(
        IMcpToolRegistry registry,
        IDevicePackageManager local,
        RemoteManagerRegistry remoteManagers,
        ILogger logger)
    {
        registry.RegisterTool(
            "uninstall_package",
            "Removes an installed MSIX package from a device by its package family name.",
            $$"""
            {
              "type": "object",
              "required": ["packageFamilyName"],
              "properties": {
                {{DeviceIdProp}},
                "packageFamilyName": {
                  "type": "string",
                  "description": "e.g. ContosoApp_8wekyb3d8bbwe"
                },
                "removeForAllUsers": {
                  "type": "boolean",
                  "description": "Remove for all users. Requires packageManagement capability. Default: false."
                },
                "preserveAppData": {
                  "type": "boolean",
                  "description": "Keep the app's local data after uninstall. Default: false."
                }
              }
            }
            """,
            async argsJson =>
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                var deviceId = GetString(root, "deviceId");

                if (IsRemote(deviceId, out var remoteId))
                    return await remoteManagers.RouteDeviceToolAsync(
                        remoteId!, "uninstall_package", BuildArgs(root, excludeDeviceId: true), logger);

                var result = await local.UninstallPackageAsync(
                    GetString(root, "packageFamilyName") ?? "",
                    GetBool(root, "removeForAllUsers", false),
                    GetBool(root, "preserveAppData", false));

                return JsonSerializer.Serialize(result, JsonOpts);
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  launch_app
    // ══════════════════════════════════════════════════════════════════════════

    private static void RegisterLaunchApp(
        IMcpToolRegistry registry,
        IDevicePackageManager local,
        RemoteManagerRegistry remoteManagers,
        ILogger logger)
    {
        registry.RegisterTool(
            "launch_app",
            "Launches a packaged app by its MSIX package family name. " +
            "Returns the process ID of the launched instance.",
            $$"""
            {
              "type": "object",
              "required": ["packageFamilyName"],
              "properties": {
                {{DeviceIdProp}},
                "packageFamilyName": { "type": "string" },
                "appId": {
                  "type": "string",
                  "description": "App entry ID within the package (e.g. 'App'). Omit to use the first/default entry."
                },
                "arguments": {
                  "type": "string",
                  "description": "Command-line arguments to pass to the app."
                }
              }
            }
            """,
            async argsJson =>
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                var deviceId = GetString(root, "deviceId");

                if (IsRemote(deviceId, out var remoteId))
                    return await remoteManagers.RouteDeviceToolAsync(
                        remoteId!, "launch_app", BuildArgs(root, excludeDeviceId: true), logger);

                var result = await local.LaunchAppAsync(
                    GetString(root, "packageFamilyName") ?? "",
                    GetString(root, "appId"),
                    GetString(root, "arguments"));

                return JsonSerializer.Serialize(result, JsonOpts);
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  stop_app
    // ══════════════════════════════════════════════════════════════════════════

    private static void RegisterStopApp(
        IMcpToolRegistry registry,
        IDevicePackageManager local,
        RemoteManagerRegistry remoteManagers,
        ILogger logger)
    {
        registry.RegisterTool(
            "stop_app",
            "Terminates all running processes of a packaged app. " +
            "By default sends a graceful close request and waits up to 3 seconds before killing.",
            $$"""
            {
              "type": "object",
              "required": ["packageFamilyName"],
              "properties": {
                {{DeviceIdProp}},
                "packageFamilyName": { "type": "string" },
                "force": {
                  "type": "boolean",
                  "description": "Immediately kill all processes without waiting. Default: false."
                }
              }
            }
            """,
            async argsJson =>
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                var deviceId = GetString(root, "deviceId");

                if (IsRemote(deviceId, out var remoteId))
                    return await remoteManagers.RouteDeviceToolAsync(
                        remoteId!, "stop_app", BuildArgs(root, excludeDeviceId: true), logger);

                var result = await local.StopAppAsync(
                    GetString(root, "packageFamilyName") ?? "",
                    GetBool(root, "force", false));

                return JsonSerializer.Serialize(result, JsonOpts);
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  get_package_info
    // ══════════════════════════════════════════════════════════════════════════

    private static void RegisterGetPackageInfo(
        IMcpToolRegistry registry,
        IDevicePackageManager local,
        RemoteManagerRegistry remoteManagers,
        ILogger logger)
    {
        registry.RegisterTool(
            "get_package_info",
            "Returns complete metadata for a single installed MSIX package, " +
            "including dependencies, declared capabilities, and health status flags.",
            $$"""
            {
              "type": "object",
              "required": ["packageFamilyName"],
              "properties": {
                {{DeviceIdProp}},
                "packageFamilyName": { "type": "string" }
              }
            }
            """,
            async argsJson =>
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                var deviceId = GetString(root, "deviceId");

                if (IsRemote(deviceId, out var remoteId))
                    return await remoteManagers.RouteDeviceToolAsync(
                        remoteId!, "get_package_info", BuildArgs(root, excludeDeviceId: true), logger);

                var info = await local.GetPackageInfoAsync(
                    GetString(root, "packageFamilyName") ?? "");

                if (info is null)
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "PACKAGE_NOT_FOUND",
                        errorMessage = "The specified package is not installed on this device."
                    }, JsonOpts);

                return JsonSerializer.Serialize(new { success = true, package = info }, JsonOpts);
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  request_package_upload
    // ══════════════════════════════════════════════════════════════════════════

    private static void RegisterRequestPackageUpload(
        IMcpToolRegistry registry,
        PackageStagingManager staging,
        RemoteManagerRegistry remoteManagers,
        ExternalAccessManager externalAccess,
        ILogger logger)
    {
        registry.RegisterTool(
            "request_package_upload",
            "Requests a pre-signed, single-use upload URL for staging an MSIX package on a device. " +
            "POST the raw package bytes to the returned uploadUrl with Content-Type: application/octet-stream. " +
            "The server verifies the SHA-256 at every hop. On success, pass the stagingId to install_package " +
            "as 'staged://{stagingId}'. The upload token expires in 30 minutes; staged files are auto-purged after 24 hours.",
            $$"""
            {
              "type": "object",
              "required": ["filename", "sha256", "sizeBytes"],
              "properties": {
                {{DeviceIdProp}},
                "filename": {
                  "type": "string",
                  "description": "Original filename, e.g. 'ContosoApp.msix' or 'ContosoApp.msixbundle'. Used for format detection."
                },
                "sha256": {
                  "type": "string",
                  "description": "SHA-256 of the file as lowercase hex. Verified on receipt at every hop."
                },
                "sizeBytes": {
                  "type": "integer",
                  "description": "Exact file size in bytes. Must match the upload exactly."
                }
              }
            }
            """,
            async argsJson =>
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root     = doc.RootElement;
                var deviceId = GetString(root, "deviceId");
                var filename  = GetString(root, "filename") ?? "package.msix";
                var sha256    = GetString(root, "sha256") ?? "";
                var sizeBytes = GetLong(root, "sizeBytes", 0);

                if (string.IsNullOrWhiteSpace(sha256))
                    return JsonSerializer.Serialize(new
                    {
                        success = false, error = "MISSING_SHA256",
                        errorMessage = "sha256 is required for upload integrity verification."
                    }, JsonOpts);

                if (sizeBytes <= 0)
                    return JsonSerializer.Serialize(new
                    {
                        success = false, error = "MISSING_SIZE",
                        errorMessage = "sizeBytes must be a positive integer."
                    }, JsonOpts);

                if (sizeBytes > PackageStagingManager.MaxFileSizeBytes)
                    return JsonSerializer.Serialize(new
                    {
                        success = false, error = "FILE_TOO_LARGE",
                        errorMessage = $"sizeBytes ({sizeBytes:N0}) exceeds the maximum of {PackageStagingManager.MaxFileSizeBytes:N0} bytes.",
                        maxBytes = PackageStagingManager.MaxFileSizeBytes,
                    }, JsonOpts);

                try
                {
                    StagingTicket ticket;

                    if (IsRemote(deviceId, out var remoteId))
                    {
                        // Ask the immediate downstream manager to create its own ticket.
                        // It returns upstream stagingId + upstream uploadPath for the next hop.
                        ticket = await CreateForwardingTicketAsync(
                            remoteId!, filename, sha256, sizeBytes,
                            staging, remoteManagers, logger);
                    }
                    else
                    {
                        ticket = staging.CreateLocalStage(filename, sha256, sizeBytes);
                    }

                    // Build upload URLs from what we know about the local servers
                    var localUploadUrl    = $"http://127.0.0.1:5200{ticket.UploadPath}";
                    var externalUploadUrl = externalAccess.IsEnabled && externalAccess.ExternalUrl != null
                        ? BuildExternalUploadUrl(externalAccess.ExternalUrl, ticket.UploadPath)
                        : null;

                    return JsonSerializer.Serialize(new
                    {
                        success         = true,
                        stagingId       = ticket.StagingId,
                        uploadPath      = ticket.UploadPath,
                        localUploadUrl,
                        uploadUrl       = externalUploadUrl ?? localUploadUrl,
                        expiresAt       = ticket.ExpiresAt,
                        maxSizeBytes    = PackageStagingManager.MaxFileSizeBytes,
                        hops            = ticket.Hops,
                    }, JsonOpts);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to create staging ticket for {Filename}", filename);
                    return JsonSerializer.Serialize(new
                    {
                        success = false, error = "TICKET_CREATION_FAILED",
                        errorMessage = ex.Message,
                    }, JsonOpts);
                }
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  get_package_stage_status
    // ══════════════════════════════════════════════════════════════════════════

    private static void RegisterGetPackageStageStatus(
        IMcpToolRegistry registry,
        PackageStagingManager staging,
        RemoteManagerRegistry remoteManagers,
        ILogger logger)
    {
        registry.RegisterTool(
            "get_package_stage_status",
            "Returns the current status of a staged MSIX package. " +
            "Useful for diagnosing failed uploads before retrying.",
            $$"""
            {
              "type": "object",
              "required": ["stagingId"],
              "properties": {
                {{DeviceIdProp}},
                "stagingId": { "type": "string" }
              }
            }
            """,
            argsJson =>
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                var deviceId  = GetString(root, "deviceId");
                var stagingId = GetString(root, "stagingId") ?? "";

                if (IsRemote(deviceId, out var remoteId))
                    return remoteManagers.RouteDeviceToolAsync(
                        remoteId!, "get_package_stage_status",
                        BuildArgs(root, excludeDeviceId: true), logger);

                var entry = staging.Resolve(stagingId);
                if (entry is null)
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "STAGING_NOT_FOUND",
                        errorMessage = $"No staging entry found for '{stagingId}'. It may have expired."
                    }, JsonOpts));

                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success       = true,
                    stagingId     = entry.StagingId,
                    status        = entry.Status.ToString().ToLowerInvariant(),
                    filename      = entry.Filename,
                    sizeBytes     = entry.ExpectedBytes,
                    expiresAt     = entry.ExpiresAt,
                    failureReason = entry.FailureReason,
                    isForwarding  = entry is ForwardingStagingEntry,
                }, JsonOpts));
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  discard_package_stage
    // ══════════════════════════════════════════════════════════════════════════

    private static void RegisterDiscardPackageStage(
        IMcpToolRegistry registry,
        PackageStagingManager staging,
        RemoteManagerRegistry remoteManagers,
        ILogger logger)
    {
        registry.RegisterTool(
            "discard_package_stage",
            "Deletes a staged package from disk immediately. " +
            "Staged files auto-expire after 24 hours regardless, but calling this " +
            "is good practice after a successful install or a failed attempt you do not plan to retry.",
            $$"""
            {
              "type": "object",
              "required": ["stagingId"],
              "properties": {
                {{DeviceIdProp}},
                "stagingId": { "type": "string" }
              }
            }
            """,
            argsJson =>
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                var deviceId  = GetString(root, "deviceId");
                var stagingId = GetString(root, "stagingId") ?? "";

                if (IsRemote(deviceId, out var remoteId))
                    return remoteManagers.RouteDeviceToolAsync(
                        remoteId!, "discard_package_stage",
                        BuildArgs(root, excludeDeviceId: true), logger);

                var discarded = staging.Discard(stagingId);
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success = discarded,
                    error = discarded ? (string?)null : "STAGING_NOT_FOUND",
                    errorMessage = discarded
                        ? (string?)null
                        : $"No staging entry found for '{stagingId}'. It may have already expired.",
                }, JsonOpts));
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Forwarding ticket creation (multi-hop upload chain)
    // ══════════════════════════════════════════════════════════════════════════

    private static async Task<StagingTicket> CreateForwardingTicketAsync(
        string remoteId,
        string filename,
        string sha256,
        long sizeBytes,
        PackageStagingManager staging,
        RemoteManagerRegistry remoteManagers,
        ILogger logger)
    {
        // Strip one hop: pass the residual deviceId to the downstream manager
        SplitDeviceId(remoteId, out var immediateManagerId, out var residualDeviceId);

        // Build args for the downstream request_package_upload call
        var downstreamArgs = new Dictionary<string, object?>
        {
            ["filename"]  = filename,
            ["sha256"]    = sha256,
            ["sizeBytes"] = sizeBytes,
        };
        if (!string.IsNullOrEmpty(residualDeviceId))
            downstreamArgs["deviceId"] = residualDeviceId;

        var downstreamArgsJson = JsonSerializer.Serialize(downstreamArgs);

        // Call request_package_upload on the immediate downstream manager
        var responseJson = await remoteManagers.RouteDeviceToolAsync(
            immediateManagerId, "request_package_upload", downstreamArgsJson, logger);

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
        {
            var errMsg = root.TryGetProperty("errorMessage", out var em) ? em.GetString() : responseJson;
            throw new InvalidOperationException(
                $"Downstream manager '{immediateManagerId}' rejected request_package_upload: {errMsg}");
        }

        var downstreamStagingId   = root.GetProperty("stagingId").GetString()!;
        var downstreamUploadPath  = root.GetProperty("uploadPath").GetString()!;
        var downstreamHops        = root.TryGetProperty("hops", out var h) ? h.GetInt32() : 0;

        // Construct the downstream upload URL by combining the manager's MCP base URL
        // with the upload path (strip /mcp suffix, append the upload path)
        var downstreamMcpUrl = remoteManagers.GetManagerMcpUrl(immediateManagerId)
            ?? throw new InvalidOperationException($"No MCP URL for manager '{immediateManagerId}'");
        var downstreamBaseUrl = downstreamMcpUrl.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase)
            ? downstreamMcpUrl[..^4]
            : downstreamMcpUrl.TrimEnd('/');
        var downstreamUploadUrl = downstreamBaseUrl + downstreamUploadPath;

        // Create a forwarding entry on this manager
        return staging.CreateForwardingStage(
            filename, sha256, sizeBytes,
            downstreamUploadUrl, downstreamStagingId,
            residualDeviceId,
            hopCount: downstreamHops + 1);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Routing for staged:// install on a remote device
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// For install_package with staged:// URI targeting a remote device, we must
    /// rewrite the URI to the downstream staging ID before routing so the remote
    /// manager doesn't receive a foreign stagingId it doesn't know about.
    /// </summary>
    private static string BuildArgsWithDownstreamStaging(
        JsonElement root, string packageUri)
    {
        var args = new Dictionary<string, object?>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals("deviceId", StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Name.Equals("packageUri", StringComparison.OrdinalIgnoreCase)) continue;
            args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()
                : prop.Value;
        }

        args["packageUri"] = packageUri;
        return JsonSerializer.Serialize(args);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  JSON helpers
    // ══════════════════════════════════════════════════════════════════════════

    private static string? GetString(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static bool GetBool(JsonElement root, string key, bool defaultValue)
    {
        if (root.TryGetProperty(key, out var el))
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }

    private static long GetLong(JsonElement root, string key, long defaultValue)
    {
        if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetInt64();
        return defaultValue;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    /// <summary>
    /// Returns true when the deviceId indicates a remote target.
    /// Outputs the identifier for the immediate manager hop.
    /// </summary>
    private static bool IsRemote(string? deviceId, out string? remoteId)
    {
        if (string.IsNullOrEmpty(deviceId) ||
            deviceId.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            remoteId = null;
            return false;
        }
        remoteId = deviceId;
        return true;
    }

    /// <summary>
    /// Splits <c>"a1b2:c3d4:e5f6"</c> into <c>("a1b2", "c3d4:e5f6")</c>.
    /// If there is no colon, residual is empty string.
    /// </summary>
    private static void SplitDeviceId(string deviceId, out string immediateId, out string residualId)
    {
        var colon = deviceId.IndexOf(':');
        if (colon < 0)
        {
            immediateId = deviceId;
            residualId  = "";
        }
        else
        {
            immediateId = deviceId[..colon];
            residualId  = deviceId[(colon + 1)..];
        }
    }

    /// <summary>
    /// Rebuilds the args JSON without the <c>deviceId</c> field so the downstream
    /// manager receives only the properties it needs to operate locally.
    /// </summary>
    private static string BuildArgs(JsonElement root, bool excludeDeviceId)
    {
        var args = new Dictionary<string, object?>();
        foreach (var prop in root.EnumerateObject())
        {
            if (excludeDeviceId &&
                prop.Name.Equals("deviceId", StringComparison.OrdinalIgnoreCase))
                continue;

            // Re-use the raw JSON value so no type information is lost
            args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()
                : (object)prop.Value;
        }
        return JsonSerializer.Serialize(args);
    }

    private static string BuildExternalUploadUrl(string externalMcpUrl, string uploadPath)
    {
        var baseUrl = externalMcpUrl.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase)
            ? externalMcpUrl[..^4]
            : externalMcpUrl.TrimEnd('/');
        return baseUrl + uploadPath;
    }
}
