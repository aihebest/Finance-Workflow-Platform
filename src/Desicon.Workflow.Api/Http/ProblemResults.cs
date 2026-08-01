using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Infrastructure.Workflow;

namespace Desicon.Workflow.Api.Http;

/// <summary>
/// Maps the engine's own vocabulary of "why didn't this go through"
/// (TransitionOutcome, via RequestActionResult) onto RFC 7807 problem+json.
/// One place for this mapping so every endpoint that calls
/// RequestActionService reports failures the same way.
/// </summary>
public static class ProblemResults
{
    public static IResult ToApiResult(this RequestActionResult result, string requestPath)
    {
        if (result.Succeeded)
        {
            return Results.Ok(new
            {
                result.FromState,
                result.ToState,
                result.Message,
                result.AuditEventId,
                result.SlaDueAt,
                result.WasIdempotentReplay
            });
        }

        var (status, title) = result.Outcome switch
        {
            TransitionOutcome.NoSuchTransition => (StatusCodes.Status404NotFound, "No such transition"),
            TransitionOutcome.NotAuthorised => (StatusCodes.Status403Forbidden, "Not authorised"),
            TransitionOutcome.GuardFailed => (StatusCodes.Status409Conflict, "Guard condition not met"),
            TransitionOutcome.PolicyViolation => (StatusCodes.Status403Forbidden, "Policy violation"),
            TransitionOutcome.CommentRequired => (StatusCodes.Status400BadRequest, "Comment required"),
            TransitionOutcome.MissingCapturedField => (StatusCodes.Status400BadRequest, "Missing field"),
            _ => (StatusCodes.Status400BadRequest, "Action could not be completed")
        };

        return Results.Problem(
            statusCode: status,
            title: title,
            detail: result.Message,
            instance: requestPath,
            type: $"https://desicon.internal/problems/{result.Outcome}");
    }

    public static IResult NotFound(string entity, object id, string requestPath) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: $"{entity} not found",
            detail: $"{entity} '{id}' does not exist.",
            instance: requestPath,
            type: "https://desicon.internal/problems/not-found");

    public static IResult Forbidden(string detail, string requestPath) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: detail,
            instance: requestPath,
            type: "https://desicon.internal/problems/forbidden");

    public static IResult PreconditionFailed(string requestPath) =>
        Results.Problem(
            statusCode: StatusCodes.Status412PreconditionFailed,
            title: "Precondition failed",
            detail: "The If-Match header does not match the current version of this resource. Reload and retry.",
            instance: requestPath,
            type: "https://desicon.internal/problems/precondition-failed");

    public static IResult PreconditionRequired(string requestPath) =>
        Results.Problem(
            statusCode: StatusCodes.Status428PreconditionRequired,
            title: "If-Match header required",
            detail: "An If-Match header carrying the resource's current ETag is required for this update.",
            instance: requestPath,
            type: "https://desicon.internal/problems/precondition-required");

    public static IResult BadRequest(string detail, string requestPath) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad request",
            detail: detail,
            instance: requestPath,
            type: "https://desicon.internal/problems/bad-request");
}
