# Package Deployment

The zRover Retriever exposes a set of MCP tools that let AI agents and other MCP clients install, uninstall, query, and launch MSIX packages on Windows devices — including devices that are remote or connected through a federation chain. This document explains what those tools do, how file transfer and installation work end-to-end, and why certain steps (like signing) are performed automatically.

## Table of Contents

- [Security gate](#security-gate)
- [Tools overview](#tools-overview)
- [Deploying a package: end-to-end flow](#deploying-a-package-end-to-end-flow)
  - [1. Request a staging slot](#1-request-a-staging-slot)
  - [2. Upload the file](#2-upload-the-file)
  - [3. Install](#3-install)
- [Automatic signing](#automatic-signing)
  - [Why signing is required](#why-signing-is-required)
  - [How the dev cert works](#how-the-dev-cert-works)
  - [Publisher patching](#publisher-patching)
  - [The one-time trust prompt](#the-one-time-trust-prompt)
- [Multi-hop federation](#multi-hop-federation)
- [Read-only operations](#read-only-operations)
- [Error reference](#error-reference)

---

## Security gate

Package installation is **disabled by default**. Before any upload or install tool can succeed on a device, package management must be explicitly enabled on that device — either via the Retriever UI toggle or by navigating to `zrover://enable-package-install` on the device.

This gate applies to `request_package_upload`, `install_package`, and `uninstall_package`. Read-only tools (`list_installed_packages`, `get_package_info`, `launch_app`, `stop_app`) are always available.

---

## Tools overview

| Tool | Description |
|------|-------------|
| `list_devices` | Lists all devices available for package management — the local machine plus any federated remotes. Returns the architecture of each device so you can choose the correct MSIX. |
| `get_device_info` | Returns OS and hardware details for a device, including processor architecture. |
| `list_installed_packages` | Lists installed MSIX packages on a device, with optional name filtering and flags to include framework/system packages. |
| `get_package_info` | Returns detailed information about a specific installed package, including its apps, version, publisher, and running status. |
| `request_package_upload` | Reserves a staging slot and returns a single-use upload URL. Required before `install_package` when delivering a local file to the device. |
| `get_package_stage_status` | Returns the status of a pending or in-progress staged upload. |
| `discard_package_stage` | Cancels and cleans up a staged upload. |
| `install_package` | Installs or updates a package from a URL, a local file path, an `.appinstaller` manifest, or a staged upload. |
| `uninstall_package` | Removes an installed package. |
| `launch_app` | Launches a packaged app by its package family name and optional app entry ID. |
| `stop_app` | Terminates all running processes of a packaged app, gracefully or forcibly. |

---

## Deploying a package: end-to-end flow

When the source file lives on the machine running the MCP client (rather than being hosted at an HTTPS URL), you use the staging pipeline to transfer it to the target device:

### 1. Request a staging slot

Call `request_package_upload` with the filename, exact file size in bytes, and the SHA-256 hash of the file. The Retriever reserves a staging slot and returns:

- A **`stagingId`** — an opaque token you will pass to `install_package` later.
- An **`uploadUrl`** — the HTTP endpoint to POST the file bytes to.
- An **`expiresAt`** timestamp — the slot expires after 30 minutes if unused.

> **Match your `deviceId`.** The `stagingId` is scoped to the device it was created on. Always pass the same `deviceId` to both `request_package_upload` and `install_package`.

> **Choose the right architecture.** Use `list_devices` or `get_device_info` to check the target's `architecture` field before selecting which MSIX to upload. Uploading an x64 package to an arm64 device will fail at install time.

### 2. Upload the file

POST the raw file bytes to the `uploadUrl` with `Content-Type: application/octet-stream`. The server streams the bytes to a temporary file and verifies the SHA-256 on completion. If the hash does not match, the upload is rejected and the staging slot moves to a `Failed` state.

You can poll `get_package_stage_status` to track progress if needed.

### 3. Install

Once the staging status is `Ready`, call `install_package` with `packageUri` set to `staged://{stagingId}`. The Retriever will:

1. Resolve the staged file path.
2. Automatically sign the package (see [Automatic signing](#automatic-signing) below).
3. Pass it to the Windows deployment stack for installation.

`install_package` also accepts `https://` URLs, `file:///` local paths (note: always use a `file:///` URI, not a bare Windows path), and `ms-appinstaller://` URIs. For HTTPS and `ms-appinstaller://` sources, Windows handles the download directly and automatic signing is **not** applied — the package must already be signed with a certificate trusted on the target device.

---

## Automatic signing

### Why signing is required

Windows requires every MSIX package to carry a valid code signature before it will install it, even in development scenarios with Developer Mode enabled. A package built locally — for example by Visual Studio in Debug mode — is typically signed with the developer's personal certificate, which is only trusted on their own machine. When that package is transferred to a different device, its signature is either absent or signed by an untrusted certificate, and Windows will refuse to install it.

### How the dev cert works

The first time package installation is enabled on a device, the Retriever automatically creates a **machine-specific self-signed certificate** (`CN=zRover Dev Signing`). The certificate and its private key are stored durably in the Windows certificate store so the same certificate is loaded automatically on subsequent starts. This certificate is then used to re-sign every package installed through the Retriever on that device.

Because the same certificate is reused for all packages on a given machine, the trust prompt (described below) only ever needs to happen once per device.

### Publisher patching

An MSIX package encodes its expected publisher identity inside `AppxManifest.xml`. The signature must come from a certificate whose subject matches that publisher exactly — otherwise Windows rejects the package, regardless of whether the cert itself is trusted.

Since the Retriever uses a single fixed certificate (`CN=zRover Dev Signing`), it first checks whether the package's declared publisher matches. If it does not, the Retriever **repacks the package** — patching the publisher field in the manifest and regenerating the content hash metadata — before signing. This repacking step is automatic and transparent; the package content (binaries, assets, resources) is unchanged.

### The one-time trust prompt

For Windows to accept a self-signed certificate, that certificate must be installed in the machine's **Trusted People** and **Trusted Root Certification Authorities** certificate stores. The Retriever handles this automatically the first time you enable package installation:

1. It exports the dev certificate to a temporary file.
2. It launches an **elevated (UAC) PowerShell prompt** that imports the certificate into both stores.
3. Once the prompt is accepted, subsequent installs on the same machine require no further elevation.

If you cancel the UAC prompt, signing will still be attempted but installation may fail with a trust error until the cert is added.

---

## Multi-hop federation

When a `deviceId` refers to a remote machine in a federation, the staging pipeline is automatically chained across hops:

1. `request_package_upload` on the local Retriever reaches out to the downstream Retriever and creates a **forwarding staging entry**. The local machine acts as a relay.
2. You POST the file to the local upload URL. The local Retriever verifies the SHA-256, then forwards the file to the next hop over its own staging HTTP endpoint, verifying integrity again at each step.
3. `install_package` passes the local `stagingId`; the Retriever rewrites it to the downstream `stagingId` and routes the call to the correct remote machine, which performs signing and installation locally.

Integrity is checked at every hop — the SHA-256 you provide at the start must match the bytes received at every stage of the chain.

---

## Read-only operations

The following tools do not require package installation to be enabled and do not modify the device state:

- `list_installed_packages` — enumerate installed packages, optionally filtered by name.
- `get_package_info` — get details about a specific package, including whether any of its apps are currently running.
- `launch_app` — launch an installed app by package family name.
- `stop_app` — terminate a running app.

---

## Error reference

| Error code | Meaning |
|------------|---------|
| `PACKAGE_INSTALL_DISABLED` | Package management has not been enabled on this device. Navigate to `zrover://enable-package-install` or use the Retriever UI toggle. |
| `STAGING_NOT_FOUND` | The `stagingId` is unknown or has expired (30-minute upload window, 24-hour file retention). |
| `STAGING_NOT_READY` | The staged file has not finished uploading or verification yet. |
| `SIGN_FAILED` | The Retriever could not sign the package, usually because the dev cert is not initialised. Enable package installation first, which triggers cert initialisation. |
| `CERT_NOT_TRUSTED` | The signing certificate is not trusted on this device. Accept the UAC trust prompt when enabling package installation. |
| `PACKAGE_NOT_FOUND` | No installed package matches the supplied family name or full name. |
| `PACKAGE_IN_USE` | The package cannot be replaced because one of its apps is running. Stop the app first with `stop_app`, or pass `forceAppShutdown: true` to `install_package`. |
| `PACKAGE_ALREADY_REGISTERED` | The exact same version is already installed. Uninstall first or supply a newer version. |
| `HIGHER_VERSION_INSTALLED` | A newer version of the package is already on the device. |
| `DEPENDENCY_NOT_FOUND` | A required dependency (runtime, framework, etc.) is not installed on the device. |
| `ACCESS_DENIED` | The operation requires elevated permissions not available to the Retriever. |
| `FILE_TOO_LARGE` | The `sizeBytes` value exceeds the 4 GiB maximum. |
