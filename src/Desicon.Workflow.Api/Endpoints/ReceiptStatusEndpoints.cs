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
/// The requester answering, on a claim they already hold, whether the receipts
/// are attached.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS
/// ---------------
/// Found 6 September 2026: retiring a cash advance could not be completed by
/// anybody, and never could.
///
/// <c>AdvanceRetirementEndpoints.RetireAsync</c> creates the linked expense
/// claim as a direct insert rather than through draft creation, and never sets
/// <c>ReceiptStatus</c> — so it takes the property default, which is
/// <see cref="ReceiptStatus.No"/>. EXPENSE's SUBMIT guard requires
/// <c>ReceiptStatus != 'No'</c>. The requester pressed "Retire this advance",
/// was taken to the claim the server had just made for them, and could not
/// move it.
///
/// The way out existed on paper. <c>PUT /api/v1/requests/{id}</c> reaches
/// <c>UpdateDraftAsync</c>, which calls the same <c>ApplyExpenseFields</c> that
/// draft creation does, and that sets the field. Nothing called it: the SPA has
/// no update-draft function and no edit-draft route. So the endpoint that could
/// have fixed this shipped, was tested, and was never wired to a button.
///
/// This mattered more than the other gaps of its kind. The Director of Finance's
/// objection to cash advances is that around 98 percent of them were never
/// retired. Retirement is the single behaviour this platform most needs to make
/// easy, and it was the one that did not work.
///
/// WHY NOT JUST CALL THE PUT
/// -------------------------
/// <c>ApplyExpenseFields</c> is a full replace — it clears the lines and
/// rebuilds them from the payload. Changing one radio button would mean the
/// browser round-tripping every line the server generated from the advance and
/// sending them all back, so a field this endpoint did not know about, or a
/// rounding difference in a re-serialised amount, would quietly rewrite the
/// claim. For a retirement claim those lines are the account of what the money
/// was spent on. They should not travel through the browser to change something
/// else.
///
/// So: a narrow endpoint that changes exactly one field, on the same pattern as
/// <see cref="AllocationEndpoints"/> — gated on who and on which states, and
/// writing its own hash-chained audit event.
///
/// WHY THE OLD VALUE IS RECORDED
/// -----------------------------
/// This field is the requester's assertion about themselves, and version 7 of
/// both modules exists because such an assertion is not evidence. It is still
/// worth keeping: a claim that said No, then Yes, then was submitted is a
/// different story from one that said Yes throughout, and the attachment
/// timestamps sit beside it in the same trail.
/// </remarks>
public static class ReceiptStatusEndpoints
{
    /// <summary>
    /// The states in which a claim still belongs to its requester. Matches
    /// <c>RequestEndpoints.EditableStates</c> deliberately: this is an edit,
    /// and it should be possible in exactly the states any other edit is.
    /// RETURNED is included for the obvious case — an approver sent it back
    /// asking for the receipts.
    /// </summary>
    private static readonly HashSet<string> EditableStates =
        new(StringComparer.Ordinal) { "DRAFT", "RETURNED" };

    public static void MapReceiptStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPatch("/api/v1/requests/{id:guid}/receipt-status", SetReceiptStatusAsync)
           .RequireAuthorization();
    }

    public sealed record SetReceiptStatusDto(string? ReceiptStatus);

    private static async Task<IResult> SetReceiptStatusAsync(
        Guid id,
        SetReceiptStatusDto dto,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        ICurrentUserAccessor currentUser,
        IWorkflowClock clock,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ReceiptStatus>(dto.ReceiptStatus, ignoreCase: true, out var requested))
        {
            return ProblemResults.BadRequest(
                "'receiptStatus' must be Yes, No or Incomplete.", httpRequest.Path);
        }

        var request = await db.Requests
            .FirstOrDefaultAsync(r => r.RequestId == id, cancellationToken);

        if (request is null)
        {
            return ProblemResults.NotFound("Request", id, httpRequest.Path);
        }

        if (request is not ExpenseRequest expense)
        {
            return ProblemResults.BadRequest(
                "Receipt status is a field on an expense claim. This request is not one.",
                httpRequest.Path);
        }

        var employee = await currentUser.GetEmployeeAsync(cancellationToken);

        // The requester, and only the requester. This is their statement about
        // their own claim, and an approver who thinks it is wrong has Return
        // rather than a way to answer on somebody else's behalf.
        if (expense.RequesterId != employee.Id)
        {
            return ProblemResults.Forbidden(
                "Only the person who raised this claim may set its receipt status.",
                httpRequest.Path);
        }

        if (!EditableStates.Contains(expense.CurrentState))
        {
            return ProblemResults.Forbidden(
                $"Receipt status can only be changed while a claim is with its requester. " +
                $"This one is at {expense.CurrentState}.",
                httpRequest.Path);
        }

        var before = expense.ReceiptStatus;

        if (before == requested)
        {
            return ProblemResults.BadRequest(
                $"The claim already records receipts as '{requested}'.", httpRequest.Path);
        }

        expense.ReceiptStatus = requested;

        var previousHash = await db.AuditEvents
            .Where(e => e.RequestId == id)
            .OrderByDescending(e => e.AuditEventId)
            .Select(e => e.EventHash)
            .FirstOrDefaultAsync(cancellationToken);

        var auditEvent = new AuditEvent
        {
            RequestId = id,
            EventType = "RECEIPT_STATUS_SET",

            // Same state either side. Nothing moved; the requester answered a
            // question about a claim that stayed exactly where it was.
            FromState = expense.CurrentState,
            ToState = expense.CurrentState,
            ActorId = employee.Id,
            ActorRole = "Requester",
            PayloadJson = JsonSerializer.Serialize(
                new { Before = before.ToString(), After = requested.ToString() }),
            OccurredAtUtc = clock.UtcNow
        };
        auditEvent.Seal(previousHash);

        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new
        {
            expense.RequestId,
            expense.RequestNumber,
            ReceiptStatus = requested.ToString()
        });
    }
}
