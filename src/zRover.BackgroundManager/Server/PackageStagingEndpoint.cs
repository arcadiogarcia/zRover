using System.Text.Json;
using zRover.BackgroundManager.Packages;

namespace zRover.BackgroundManager.Server;

/// <summary>
/// Registers the <c>POST /packages/stage/{uploadToken}</c> endpoint on any
/// <see cref="WebApplication"/>, shared between the primary server (<c>Program.cs</c>)
/// and the external listener (<see cref="ExternalAccessManager"/>).
///
/// <b>Authentication note</b>: this endpoint is intentionally exempt from the
/// bearer-token middleware on the external listener.  Each upload URL contains a
/// 256-bit random single-use token as the last path segment — that token IS the
/// credential for exactly one upload.  All other paths on the external listener
/// still require the bearer token.
/// </summary>
public static class PackageStagingEndpoint
{
    /// <summary>
    /// Maps <c>POST /packages/stage/{uploadToken}</c> on <paramref name="app"/>
    /// using the given <see cref="PackageStagingManager"/>.
    /// </summary>
    public static void MapStagingEndpoints(WebApplication app, PackageStagingManager staging)
    {
        app.MapPost("/packages/stage/{uploadToken}", async (
            string uploadToken,
            HttpRequest request,
            CancellationToken ct) =>
        {
            // Reject excessively large uploads early (before reading the body)
            if (request.ContentLength.HasValue &&
                request.ContentLength.Value > PackageStagingManager.MaxFileSizeBytes)
            {
                return Results.Json(
                    new { error = "FILE_TOO_LARGE", maxBytes = PackageStagingManager.MaxFileSizeBytes },
                    statusCode: 413);
            }

            var result = await staging.AcceptUploadAsync(
                uploadToken,
                request.Body,
                request.ContentLength,
                ct);

            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    "TOKEN_NOT_FOUND"      => 404,
                    "TOKEN_EXPIRED"        => 404,
                    "ALREADY_UPLOADED"     => 409,
                    "FILE_TOO_LARGE"       => 413,
                    "WRONG_SIZE"           => 400,
                    "SHA256_MISMATCH"      => 422,
                    "FORWARD_FAILED"       => 502,
                    "DOWNSTREAM_UNREACHABLE" => 503,
                    _                      => 500,
                };

                var body = result.HopAlias is not null
                    ? new { error = result.Error!, message = result.ErrorMessage!, hop = result.HopAlias }
                    : (object)new { error = result.Error!, message = result.ErrorMessage! };

                return Results.Json(body, statusCode: statusCode);
            }

            return Results.Json(new
            {
                stagingId = result.StagingId!,
                sizeBytes = result.SizeBytes!.Value,
            });
        });
    }
}
