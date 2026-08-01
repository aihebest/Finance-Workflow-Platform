using Desicon.Workflow.Api.Security;
using Microsoft.AspNetCore.Diagnostics;

namespace Desicon.Workflow.Api.Http;

/// <summary>
/// Last-resort translation to problem+json. Endpoints are expected to catch
/// their own expected failure modes (not found, guard failed, ...) and return
/// a typed IResult via ProblemResults -- this only runs for what nobody
/// anticipated, so the response body must never leak exception detail.
/// </summary>
public sealed partial class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on {Path}")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception, string path);

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            UnprovisionedUserException ex => (
                StatusCodes.Status403Forbidden,
                "No matching staff record",
                ex.Message),

            InvalidOperationException ex => (
                StatusCodes.Status404NotFound,
                "Not found",
                ex.Message),

            ArgumentException ex => (
                StatusCodes.Status400BadRequest,
                "Bad request",
                ex.Message),

            _ => (
                StatusCodes.Status500InternalServerError,
                "An unexpected error occurred",
                (string?)null)
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            LogUnhandledException(_logger, exception, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;

        await Results.Problem(
            statusCode: status,
            title: title,
            detail: detail,
            instance: httpContext.Request.Path,
            type: $"https://desicon.internal/problems/{status}"
        ).ExecuteAsync(httpContext);

        return true;
    }
}
