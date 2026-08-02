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
/// DEL-AC-FRM-002 (expense) specific capture endpoints. Everything here
/// exists because the corresponding workflow transition either has no
/// meaningful generic capture shape (a list of GL lines is not a scalar
/// payload field) or references a guard field that must be staged onto the
/// tracked entity before RequestActionService.ExecuteAsync runs -- see
/// RequestEndpoints.ExecuteActionAsync's TreasuryNumber handling for the one
/// case that *is* generic enough to live there instead.
/// </summary>
public static class ExpenseEndpoints
{
    public static void MapExpenseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/expenses").RequireAuthorization();

        group.MapGet("/{id:guid}/advance-netting", GetAdvanceNettingAsync);
        group.MapPost("/{id:guid}/acknowledge", AcknowledgeAsync);
        group.MapPost("/{id:guid}/gl-lines", CaptureGlLinesAsync);
        group.MapPost("/{id:guid}/authorise-posting", AuthorisePostingAsync);
        group.MapPost("/{id:guid}/refund-received", ConfirmRefundAsync);
    }

    private static async Task<IResult> GetAdvanceNettingAsync(
        Guid id,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        ReadAccessScope readAccess,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var expense = await db.ExpenseRequests
            .FirstOrDefaultAsync(e => e.RequestId == id, cancellationToken);

        if (expense is null)
        {
            return ProblemResults.NotFound("Expense request", id, httpRequest.Path);
        }

        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        var roles = currentUser.GetRoles();

        if (!await readAccess.CanReadAsync(expense, employee, roles, cancellationToken))
        {
            return ProblemResults.Forbidden("You do not have access to this request.", httpRequest.Path);
        }

        if (expense.RetiresAdvanceId is not { } advanceId)
        {
            return Results.Ok(new { RetiresAdvance = false });
        }

        var advance = await db.CashAdvanceRequests
            .FirstOrDefaultAsync(a => a.RequestId == advanceId, cancellationToken);

        if (advance is null)
        {
            return ProblemResults.NotFound("Cash advance request", advanceId, httpRequest.Path);
        }

        return Results.Ok(new
        {
            RetiresAdvance = true,
            advance.RequestId,
            advance.RequestNumber,
            advance.RetirementBalanceNgn,
            expense.AdvanceAmountNgn,
            expense.NetPayableNgn,
            expense.IsRefundDue
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

        var expense = await db.ExpenseRequests.FirstOrDefaultAsync(e => e.RequestId == id, cancellationToken);
        if (expense is null)
        {
            return ProblemResults.NotFound("Expense request", id, httpRequest.Path);
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

        // JournalVoucherNumber and the GL lines themselves are both guard
        // fields on the POST transition (GlDebitTotal/GlCreditTotal/
        // GlLineCount), and RunTransitionAsync reloads GL lines fresh from
        // the database by RequestId rather than from this in-memory
        // ChangeTracker -- so both must be committed before ExecuteAsync runs,
        // not merely added to the tracked collection.
        expense.JournalVoucherNumber = dto.JournalVoucherNumber;
        await db.SaveChangesAsync(cancellationToken);

        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);

        var result = await actionService.ExecuteAsync(
            id, actingUser,
            new TransitionRequest(
                "POST", dto.Comment,
                new Dictionary<string, object?>
                {
                    ["JournalVoucherNumber"] = dto.JournalVoucherNumber,
                    ["GlLineCount"] = dto.Lines.Count,
                    ["GlPostingLines"] = dto.Lines
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

    private static async Task<IResult> ConfirmRefundAsync(
        Guid id,
        ConfirmRefundDto dto,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        RequestActionService actionService,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        if (dto.RefundReceivedAmountNgn <= 0)
        {
            return ProblemResults.BadRequest("'refundReceivedAmountNgn' must be greater than zero.", httpRequest.Path);
        }

        var expense = await db.ExpenseRequests.FirstOrDefaultAsync(e => e.RequestId == id, cancellationToken);
        if (expense is null)
        {
            return ProblemResults.NotFound("Expense request", id, httpRequest.Path);
        }

        // RefundReceivedAmountNgn is a guard field on CONFIRM_REFUND, so it
        // must be on the tracked entity before ExecuteAsync re-evaluates the
        // guard against it.
        expense.RefundReceivedAmountNgn = dto.RefundReceivedAmountNgn;
        await db.SaveChangesAsync(cancellationToken);

        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);

        var result = await actionService.ExecuteAsync(
            id, actingUser,
            new TransitionRequest(
                "CONFIRM_REFUND", dto.Comment,
                new Dictionary<string, object?> { ["RefundReceivedAmountNgn"] = dto.RefundReceivedAmountNgn },
                dto.IdempotencyKey),
            cancellationToken);

        return result.ToApiResult(httpRequest.Path);
    }

    private sealed record GlLineDto(string Side, string AccountNumber, string? Narration, decimal AmountNgn);

    private sealed record CaptureGlLinesDto(
        string JournalVoucherNumber,
        IReadOnlyList<GlLineDto> Lines,
        string? Comment = null,
        string? IdempotencyKey = null);

    private sealed record ConfirmRefundDto(
        decimal RefundReceivedAmountNgn,
        string? Comment = null,
        string? IdempotencyKey = null);
}
