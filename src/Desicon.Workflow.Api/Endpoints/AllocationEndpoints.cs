using System.Text.Json;
using Desicon.Workflow.Api.Http;
using Desicon.Workflow.Api.Security;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Audit;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Api.Endpoints;

/// <summary>
/// Cost Control setting or correcting the coding on a request in its queue.
/// </summary>
/// <remarks>
/// Asked for by Cost Control on 24 August 2026, and confirmed by Finance:
/// "Cost Control should be able to return it to the requester with comments for
/// correction, and be able to correct the cost centre if wrongly inputted."
/// Both, not either.
///
/// WHY THIS DESK, RATHER THAN SENDING IT BACK EVERY TIME
/// -----------------------------------------------------
/// Cost Control holds the organisation's cost centres. The requester frequently
/// does not — they know what they spent money on, not which centre it belongs
/// to — so a code that arrives wrong or blank is ordinary, not careless.
/// Returning every such request makes the desk that knows the answer ask the
/// person who does not.
///
/// Return remains available and unchanged. It is the right response when the
/// *request* is wrong. This is for when only the coding is.
///
/// WHY IT IS NOT A TRANSITION, AND NOT A `captures` FIELD
/// ------------------------------------------------------
/// The obvious implementation was to add ProjectCode and CostCentreCode to
/// COST_CONTROL_VERIFY's `captures`. That does not work: WorkflowEngine treats
/// every captured field as mandatory and fails with MissingCapturedField when
/// one is absent. Project code and cost centre are alternatives — exactly one
/// ever applies — so requiring both would refuse every verification, and
/// requiring neither is what we already had.
///
/// It is also not a state change. Nothing moves; a fact about the request is
/// corrected while it sits in the same queue. Modelling that as a transition
/// would put a fake step in the trail of every request that needed recoding.
///
/// So: a separate endpoint, gated on the same role and state the transition
/// would have been, writing its own hash-chained audit event.
///
/// THE OLD VALUE IS RECORDED, NOT OVERWRITTEN
/// -------------------------------------------
/// The event carries what the coding was as well as what it became. A silent
/// correction is indistinguishable from the original entry a week later, and
/// this is a figure that decides which budget carries the spend. If a
/// department head ever asks why their centre was charged, the answer has to
/// exist.
/// </remarks>
public static class AllocationEndpoints
{
    /// <summary>The only state in which coding may be corrected, and the only
    /// role that may do it.</summary>
    private const string CorrectableState = "COST_CONTROL_VERIFY";

    private const string CostControlRole = "CostControlOfficer";

    public static void MapAllocationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPatch("/api/v1/requests/{id:guid}/allocation", CorrectAllocationAsync)
           .RequireAuthorization();
    }

    public sealed record CorrectAllocationDto(
        string? ProjectCode,
        string? CostCentreCode,
        string? Reason);

    private static async Task<IResult> CorrectAllocationAsync(
        Guid id,
        CorrectAllocationDto dto,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        ICurrentUserAccessor currentUser,
        IWorkflowClock clock,
        CancellationToken cancellationToken)
    {
        var project = string.IsNullOrWhiteSpace(dto.ProjectCode) ? null : dto.ProjectCode.Trim();
        var costCentre = string.IsNullOrWhiteSpace(dto.CostCentreCode) ? null : dto.CostCentreCode.Trim();

        if (project is null == costCentre is null)
        {
            return ProblemResults.BadRequest(
                "Supply exactly one of 'projectCode' or 'costCentreCode'. A request is either " +
                "project specific or it is not.",
                httpRequest.Path);
        }

        if (string.IsNullOrWhiteSpace(dto.Reason))
        {
            return ProblemResults.BadRequest(
                "'reason' is required. A coding change is recorded against the request and needs " +
                "to say why it was made.",
                httpRequest.Path);
        }

        var request = await db.Requests
            .Include(r => ((ExpenseRequest)r).Lines)
            .FirstOrDefaultAsync(r => r.RequestId == id, cancellationToken);

        if (request is null)
        {
            return ProblemResults.NotFound("Request", id, httpRequest.Path);
        }

        if (!currentUser.GetRoles().Contains(CostControlRole))
        {
            return ProblemResults.Forbidden(
                "Only Cost Control may set the coding on a request.", httpRequest.Path);
        }

        // Gated on the state as well as the role. Cost Control holds the role
        // permanently; they hold *this request* only while it is in their
        // queue. Once it has moved on, the figures have been approved by
        // someone downstream and changing the coding silently underneath them
        // is a different act -- that one is a Return.
        if (!string.Equals(request.CurrentState, CorrectableState, StringComparison.Ordinal))
        {
            return ProblemResults.Forbidden(
                $"Coding can only be set while a request is at {CorrectableState}. " +
                $"This one is at {request.CurrentState}.",
                httpRequest.Path);
        }

        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        var now = clock.UtcNow;

        var before = Describe(request);
        ApplyAllocation(request, project, costCentre);
        var after = Describe(request);

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return ProblemResults.BadRequest(
                "That is the coding the request already carries.", httpRequest.Path);
        }

        var previousHash = await db.AuditEvents
            .Where(e => e.RequestId == id)
            .OrderByDescending(e => e.AuditEventId)
            .Select(e => e.EventHash)
            .FirstOrDefaultAsync(cancellationToken);

        var auditEvent = new AuditEvent
        {
            RequestId = id,
            EventType = "ALLOCATION_SET",

            // From and To are the same state deliberately. Nothing moved, and a
            // reader scanning the trail for the route this request took should
            // see it stayed put while a fact about it changed.
            FromState = request.CurrentState,
            ToState = request.CurrentState,
            ActorId = employee.Id,
            ActorRole = CostControlRole,
            Reason = dto.Reason.Trim(),
            PayloadJson = JsonSerializer.Serialize(new { Before = before, After = after }),
            OccurredAtUtc = now
        };
        auditEvent.Seal(previousHash);

        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new
        {
            request.RequestId,
            request.RequestNumber,
            Allocation = after
        });
    }

    /// <summary>
    /// Where the coding lives differs by module, and the difference is real
    /// rather than incidental: a cash advance is coded once, and an expense
    /// claim carries a code per line because one claim can legitimately span
    /// two centres.
    /// </summary>
    /// <remarks>
    /// Setting every line to the same code is right for the case this exists
    /// for — a claim coded to the wrong centre, or to none — and wrong for a
    /// genuinely split claim. Cost Control's remedy there is Return, which puts
    /// the split back with the person who knows how it should divide. Recorded
    /// here rather than silently assumed, because a bulk overwrite of a
    /// deliberate split would be a quiet loss of information.
    /// </remarks>
    private static void ApplyAllocation(Request request, string? project, string? costCentre)
    {
        switch (request)
        {
            case CashAdvanceRequest advance:
                advance.AllocationType = project is null ? AllocationType.CostCentre : AllocationType.Project;
                advance.ProjectCode = project;
                advance.CostCentreCode = costCentre;
                break;

            case ExpenseRequest expense:
                foreach (var line in expense.Lines)
                {
                    line.ProjectCode = project;
                    line.CostCentreCode = costCentre;
                }
                break;
        }
    }

    private static string Describe(Request request) => request switch
    {
        CashAdvanceRequest a => a.ProjectCode is { } p ? $"Project {p}" : $"Cost centre {a.CostCentreCode}",

        ExpenseRequest e when e.Lines.Count > 0 =>
            string.Join(", ", e.Lines
                .Select(l => l.ProjectCode is { } p ? $"Project {p}" : $"Cost centre {l.CostCentreCode}")
                .Distinct(StringComparer.Ordinal)),

        _ => "(none)"
    };
}
