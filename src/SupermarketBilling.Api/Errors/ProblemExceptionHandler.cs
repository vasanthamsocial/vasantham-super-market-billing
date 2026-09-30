using Microsoft.AspNetCore.Diagnostics;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.Api.Errors;

/// <summary>Turns expected failures into RFC 7807 problem responses with a stable machine-readable code.</summary>
internal sealed class ProblemExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var (status, code, detail) = exception switch
        {
            AppException app => (StatusFor(app.Kind), app.Code, app.Message),
            DomainException domain => (StatusCodes.Status400BadRequest, domain.Code, domain.Message),
            BadHttpRequestException bad => (bad.StatusCode, "bad_request", DescribeBadRequest(bad)),
            _ => (0, string.Empty, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        await Results.Problem(detail: detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }

    /// <summary>Names the field that could not be read (for example $.baseUnitId), never the submitted value.</summary>
    private static string DescribeBadRequest(BadHttpRequestException exception) =>
        exception.InnerException is System.Text.Json.JsonException { Path: { Length: > 0 } path }
            ? $"The value for '{path.TrimStart('$', '.')}' is missing or not in the expected format."
            : "The request is malformed.";

    private static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Locked => StatusCodes.Status423Locked,
        ErrorKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status400BadRequest,
    };
}
