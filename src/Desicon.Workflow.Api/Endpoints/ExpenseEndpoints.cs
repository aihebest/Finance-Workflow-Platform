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
        group.MapPost("/{id:guid}/execute-payment", ExecutePaymentAsync);
        group.MapPost("/{id:guid}/mark-posted", MarkPostedAsync);
    }

    /// <summary>
    /// The Accounts Officer records that she has posted this in Business
    /// Central, and under which document number.
    /// </summary>
    /// <remarks>
    /// Replaces the gl-lines endpoint's role in definition version 2. No
    /// journal lines are captured any more: BC owns the ledger, and a second
    /// copy of the same journal in another system is two versions of the truth
    /// waiting to disagree. What this platform owns is the approval trail and
    /// the reference that joins it to the ledger entry.
    ///
    /// Needs its own endpoint for the same reason gl-lines did:
    /// BcDocumentNumber is a guard field on MARK_POSTED, and the guard reads it
    /// off the tracked entity, so it must be committed before ExecuteAsync
    /// runs rather than merely travelling inside TransitionRequest.
    /// </remarks>
    private static async Task<IResult> MarkPostedAsync(
        Guid id,
        MarkPostedDto dto,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        RequestActionService actionService,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.BcDocumentNumber))
        {
            return ProblemResults.BadRequest("'bcDocumentNumber' is required.", httpRequest.Path);
        }

        var expense = await db.ExpenseRequests.FirstOrDefaultAsync(e => e.RequestId == id, cancellationToken);
        if (expense is null)
        {
            return ProblemResults.NotFound("Expense request", id, httpRequest.Path);
        }

        expense.BcDocumentNumber = dto.BcDocumentNumber.Trim();
        await db.SaveChangesAsync(cancellationToken);

        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);

        var result = await actionService.ExecuteAsync(
            id, actingUser,
            new TransitionRequest(
                "MARK_POSTED", dto.Comment,
                new Dictionary<string, object?> { ["BcDocumentNumber"] = expense.BcDocumentNumber },
                dto.IdempotencyKey),
            cancellationToken);

        return result.ToApiResult(httpRequest.Path);
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

    /// <summary>
    /// Records that the money left, and moves the claim to the beneficiary for
    /// confirmation.
    ///
    /// This needs its own endpoint for a reason worth stating plainly, because
    /// it is not obvious from the definition. EXECUTE_PAYMENT declares
    /// <c>"captures": ["PaymentReference", "PaymentDate"]</c>, and
    /// ExpenseRequest has a column for each -- but RequestActionService only
    /// ever serialises captured fields into the audit event's PayloadJson (see
    /// SerialisePayload). Nothing copies them onto the entity. Sent through the
    /// generic /actions endpoint the transition therefore succeeds, the request
    /// moves to AWAITING_ACK, and both columns stay NULL: the payment appears
    /// to have been recorded, and the reference is retrievable only by reading
    /// audit JSON. A claim with no bank reference against it is precisely the
    /// state the 18-month-old unpaid claims were in.
    ///
    /// PaymentMethod is not a parameter. It is derived at submission by
    /// ExpenseRequest.ApplyPaymentMethodPolicy from the net payable against
    /// PAYMENT_METHOD_THRESHOLD_NGN, and letting Finance override it here would
    /// make the threshold advisory.
    /// </summary>
    private static async Task<IResult> ExecutePaymentAsync(
        Guid id,
        ExecutePaymentDto dto,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        RequestActionService actionService,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.PaymentReference))
        {
            return ProblemResults.BadRequest("'paymentReference' is required.", httpRequest.Path);
        }

        var expense = await db.ExpenseRequests.FirstOrDefaultAsync(e => e.RequestId == id, cancellationToken);
        if (expense is null)
        {
            return ProblemResults.NotFound("Expense request", id, httpRequest.Path);
        }

        var paymentDate = dto.PaymentDate ?? DateTimeOffset.UtcNow;

        // Written before ExecuteAsync rather than after, so a transition that
        // the engine refuses cannot leave a payment reference recorded against
        // a claim that was never paid. If ExecuteAsync fails these are still
        // committed -- a deliberate trade: an unpaid claim carrying a
        // reference someone typed is recoverable and visible, whereas a paid
        // claim carrying none is the failure this endpoint exists to prevent.
        expense.PaymentReference = dto.PaymentReference.Trim();
        expense.PaymentDate = paymentDate;
        await db.SaveChangesAsync(cancellationToken);

        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);

        var result = await actionService.ExecuteAsync(
            id, actingUser,
            new TransitionRequest(
                "EXECUTE_PAYMENT", dto.Comment,
                new Dictionary<string, object?>
                {
                    ["PaymentReference"] = expense.PaymentReference,
                    ["PaymentDate"] = paymentDate
                },
                dto.IdempotencyKey),
            cancellationToken);

        return result.ToApiResult(httpRequest.Path);
    }

    private sealed record GlLineDto(string Side, string AccountNumber, string? Narration, decimal AmountNgn);

    private sealed record MarkPostedDto(
        string BcDocumentNumber,
        string? Comment = null,
        string? IdempotencyKey = null);

    private sealed record ExecutePaymentDto(
        string PaymentReference,
        DateTimeOffset? PaymentDate = null,
        string? Comment = null,
        string? IdempotencyKey = null);

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
