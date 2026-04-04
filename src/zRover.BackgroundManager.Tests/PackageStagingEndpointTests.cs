using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using zRover.BackgroundManager.Packages;
using zRover.BackgroundManager.Server;

namespace zRover.BackgroundManager.Tests;

/// <summary>
/// Integration tests for the <c>POST /packages/stage/{token}</c> HTTP endpoint.
/// Uses <see cref="TestServer"/> — no real network ports opened.
/// </summary>
public sealed class PackageStagingEndpointTests : IDisposable
{
    private readonly PackageStagingManager _staging;
    private readonly TestServer _server;
    private readonly HttpClient _client;

    public PackageStagingEndpointTests()
    {
        _staging = new PackageStagingManager(NullLogger<PackageStagingManager>.Instance);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);

        var app = builder.Build();
        app.UseRouting();
        PackageStagingEndpoint.MapStagingEndpoints(app, _staging);

        app.StartAsync().GetAwaiter().GetResult();

        _server = app.GetTestServer();
        _client = _server.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        _staging.Dispose();
    }

    // ─── Success ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_ValidPayload_Returns200_WithStagingId()
    {
        var (data, sha256) = MakePayload("msix package bytes");
        var ticket = _staging.CreateLocalStage("App.msix", sha256, data.Length);

        var response = await PostUpload(ticket.UploadToken, data);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadJson(response);
        body.GetProperty("stagingId").GetString().Should().Be(ticket.StagingId);
        body.GetProperty("sizeBytes").GetInt64().Should().Be(data.Length);
    }

    [Fact]
    public async Task Upload_ValidPayload_StagingStatusIsReady()
    {
        var (data, sha256) = MakePayload("content");
        var ticket = _staging.CreateLocalStage("App.msix", sha256, data.Length);

        await PostUpload(ticket.UploadToken, data);

        _staging.ResolveLocal(ticket.StagingId)!.Status.Should().Be(StagingStatus.Ready);
    }

    // ─── Failure — unknown token ──────────────────────────────────────────────

    [Fact]
    public async Task Upload_UnknownToken_Returns404()
    {
        var unknownToken = new string('0', 64);
        var response = await PostUpload(unknownToken, [1, 2, 3]);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await ReadJson(response);
        body.GetProperty("error").GetString().Should().Be("TOKEN_NOT_FOUND");
    }

    // ─── Failure — hash mismatch ─────────────────────────────────────────────

    [Fact]
    public async Task Upload_WrongHash_Returns422()
    {
        var data = "some data"u8.ToArray();
        var wrongHash = new string('f', 64);
        var ticket = _staging.CreateLocalStage("pkg.msix", wrongHash, data.Length);

        var response = await PostUpload(ticket.UploadToken, data);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await ReadJson(response);
        body.GetProperty("error").GetString().Should().Be("SHA256_MISMATCH");
    }

    // ─── Failure — file too large ─────────────────────────────────────────────

    [Fact]
    public async Task Upload_FileTooLarge_ContentLength_Returns413()
    {
        var ticket = _staging.CreateLocalStage("big.msix", new string('0', 64), 100);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/packages/stage/{ticket.UploadToken}");
        request.Content = new ByteArrayContent([1])
        {
            Headers = { ContentLength = PackageStagingManager.MaxFileSizeBytes + 1 }
        };

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    // ─── Failure — double upload ──────────────────────────────────────────────

    [Fact]
    public async Task Upload_SecondAttemptWithSameToken_Returns404()
    {
        var (data, sha256) = MakePayload("bytes");
        var ticket = _staging.CreateLocalStage("pkg.msix", sha256, data.Length);

        await PostUpload(ticket.UploadToken, data); // first succeeds

        var second = await PostUpload(ticket.UploadToken, data); // token consumed
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostUpload(string token, byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = data.Length;
        return _client.PostAsync($"/packages/stage/{token}", content);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement;
    }

    private static (byte[] data, string sha256Hex) MakePayload(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(data);
        return (data, Convert.ToHexString(hash).ToLowerInvariant());
    }
}

// Minimal NullLoggerProvider for test host
file sealed class NullLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    public static readonly NullLoggerProvider Instance = new();
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string _) => NullLogger.Instance;
    public void Dispose() { }
}
