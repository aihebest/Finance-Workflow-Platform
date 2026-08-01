using Desicon.Workflow.Api.Http;
using Desicon.Workflow.Api.Security;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Security;
using Desicon.Workflow.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Api.Endpoints;

/// <summary>
/// DEL-AC-FRM-003 (cash advance) specific capture endpoints. gl-lines and
/// authorise-posting mirror ExpenseEndpoints -- doc 05's module-endpoint list
/// omits them for this module, but cash-advance.workflow.json has the same
/// POSTING/AUTHORISATION states gated by the same GL guards, so without these
/// two endpoints an advance could never actually pass through them.
/// </summary>
public static class CashAdvanceEndpoints
{
    private static readonly string[] OpenRetirementStates = { "OUTSTANDING", "PARTIALLY_RETIRED" };

    public static void MapCashAdvanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/advances").RequireAuthorization();

        group.MapGet("/outstanding", GetOutstandingAsync);
        group.MapGet("/{id:guid}/retirement", GetRetirementAsync);
        group.MapPost("/{id:guid}/acknowledge", AcknowledgeAsync);
        group.MapPost("/{id:guid}/release", ReleaseCashAsync);
        group.MapPost("/{id:guid}/gl-lines", CaptureGlLinesAsync);
        group.MapPost("/{id:guid}/authorise-posting", AuthorisePostingAsync);
    }

    private static async Task<IResult> GetOutstandingAsync(
        int? departmentId,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        ReadAccessScope readAccess,
        ICurrentUserAccessor currentUser,
        IWorkflowClock clock,
        CancellationToken cancellationToken)
    {
        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        var roles = currentUser.GetRoles();

        var scoped = await readAccess.ScopeAsync(
            db.CashAdvanceRequests.Include(a => a.Lines), employee, roles, cancellationToken);

        scoped = scoped.Where(a => OpenRetirementStates.Contains(a.CurrentState));

        if (departmentId is { } dept)
        {
            scoped = scoped.Where(a => a.DepartmentId == dept);
        }

        var advances = await scoped.OrderBy(a => a.RetirementDueDate).ToListAsync(cancellationToken);

        var now = clock.UtcNow;

        return Results.Ok(advances.Select(a => new
        {
            a.RequestId,
            a.RequestNumber,
            a.RequesterId,
            a.DepartmentId,
            a.TotalAmountNgn,
            a.RetiredAmountNgn,
            a.RetirementBalanceNgn,
            a.RetirementDueDate,
            RetirementStatus = a.RetirementStatus.ToString(),
            AgeingDays = a.RetirementDueDate is { } due ? Math.Max(0, (now - due).Days) : (int?)null
        }));
    }

    private static async Task<IResult> GetRetirementAsync(
        Guid id,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        ReadAccessScope readAccess,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var advance = await db.CashAdvanceRequests.FirstOrDefaultAsync(a => a.RequestId == id, cancellationToken);
        if (advance is null)
        {
            return ProblemResults.NotFound("Cash advance request", id, httpRequest.Path);
        }

        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        var roles = currentUser.GetRoles();

        if (!await readAccess.CanReadAsync(advance, employee, roles, cancellationToken))
        {
            return ProblemResults.Forbidden("You do not have access to this request.", httpRequest.Path);
        }

        var linkedClaims = await db.AdvanceRetirementLinks
            .AsNoTracking()
            .Where(l => l.CashAdvanceRequestId == id)
            .Select(l => new { l.ExpenseRequestId, l.AmountAppliedNgn })
            .ToListAsync(cancellationToken);

        return Results.Ok(new
        {
            advance.RequestId,
            advance.RequestNumber,
            advance.TotalAmountNgn,
            advance.RetiredAmountNgn,
            advance.RetirementBalanceNgn,
            advance.CashReleasedAt,
            advance.RetirementDueDate,
            RetirementStatus = advance.RetirementStatus.ToString(),
            LinkedClaims = linkedClaims
        });
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid id,
        ActionRequestDto? dto,
        HttpRequest httpRequest,
        RequestActionService actionService,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);

        var result = await actionService.ExecuteAsync(
            id, actingUser, new TransitionRequest("ACKNOWLEDGE", dto?.Comment, IdempotencyKey: dto?.IdempotencyKey),
            cancellationToken);

        return result.ToApiResult(httpRequest.Path);
    }

    private static async Task<IResult> ReleaseCashAsync(
        Guid id,
        ReleaseCashDto dto,
        HttpRequest httpRequest,
        RequestActionService actionService,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);

        // CashReleasedAt is consumed straight from CapturedFields by
        // RequestActionService.ApplyEffectAsync's "StartRetirementClock"
        // case, so no pre-staging on the entity is needed here (unlike
        // TreasuryNumber/JournalVoucherNumber/RefundReceivedAmountNgn
        // elsewhere in this API). PaymentMethod is not captured here at all
        // -- it is derived by CashAdvanceRequest.ApplyPaymentMethodPolicy,
        // not chosen by Finance at release time.
        var result = await actionService.ExecuteAsync(
            id, actingUser,
            new TransitionRequest(
                "RELEASE_CASH", dto.Comment,
                new Dictionary<string, object?>
                {
                    ["CashReleasedAt"] = dto.CashReleasedAt
                },
                dto.IdempotencyKey),
            cancellationToken);

        return result.ToApiResult(httpRequest.Path);
    }

    private static async Task<IResult> CaptureGlLinesAsync(
        Guid id,
        CaptureGlLinesDto dto,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        RequestActionService actionService,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.JournalVoucherNumber))
        {
            return ProblemResults.BadRequest("'journalVoucherNumber' is required.", httpRequest.Path);
        }

        if (dto.Lines is not { Count: > 0 })
        {
            return ProblemResults.BadRequest("At least one GL line is required.", httpRequest.Path);
        }

        var advance = await db.CashAdvanceRequests.FirstOrDefaultAsync(a => a.RequestId == id, cancellationToken);
        if (advance is null)
        {
            return ProblemResults.NotFound("Cash advance request", id, httpRequest.Path);
        }

        var existing = await db.GlPostingLines.Where(l => l.RequestId == id).ToListAsync(cancellationToken);
        db.GlPostingLines.RemoveRange(existing);

        foreach (var line in dto.Lines)
        {
            if (!Enum.TryParse<PostingSide>(line.Side, ignoreCase: true, out var side))
            {
                return ProblemResults.BadRequest($"'{line.Side}' is not a valid posting side.", httpRequest.Path);
            }

            db.GlPostingLines.Add(new GlPostingLine
            {
                RequestId = id,
                Side = side,
                AccountNumber = line.AccountNumber,
                Narration = line.Narration,
                AmountNgn = line.AmountNgn
            });
        }

        advance.JournalVoucherNumber = dto.JournalVoucherNumber;
        await db.SaveChangesAsync(cancellationToken);

        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);

        var result = await actionService.ExecuteAsync(
            id, actingUser,
            new TransitionRequest(
                "POST", dto.Comment,
                new Dictionary<string, object?>
                {
                    ["JournalVoucherNumber"] = dto.JournalVoucherNumber,
                    ["GlLineCount"] = dto.Lines.Count
                },
                dto.IdempotencyKey),
            cancellationToken);

        return result.ToApiResult(httpRequest.Path);
    }

    private static async Task<IResult> AuthorisePostingAsync(
        Guid id,
        ActionRequestDto? dto,
        HttpRequest httpRequest,
        RequestActionService actionService,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);

        var result = await actionService.ExecuteAsync(
            id, actingUser, new TransitionRequest("AUTHORISE", dto?.Comment, IdempotencyKey: dto?.IdempotencyKey),
            cancellationToken);

        return result.ToApiResult(httpRequest.Path);
    }

    private sealed record ReleaseCashDto(
        DateTimeOffset CashReleasedAt,
        string? Comment = null,
        string? IdempotencyKey = null);

    private sealed record GlLineDto(string Side, string AccountNumber, string? Narration, decimal AmountNgn);

    private sealed record CaptureGlLinesDto(
        string JournalVoucherNumber,
        IReadOnlyList<GlLineDto> Lines,
        string? Comment = null,
        string? IdempotencyKey = null);
}
