using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Infrastructure.Workflow;

/// <summary>
/// Applies the expense module's "RetireLinkedAdvance" transition effect: when
/// a claim that retires a cash advance reaches AWAITING_PAYMENT, folds the
/// claim's total into the advance's running RetiredAmountNgn, records which
/// claim contributed how much (see AdvanceRetirementLink), and pushes the
/// advance through its own RETIRE transition so it gets a real audit event
/// rather than a side-effect no one can see in the advance's own history.
///
/// Depends on neither RequestActionService nor IServiceProvider, so DI stays
/// one-way (RequestActionService -> AdvanceRetirementHandler): the caller
/// hands in its own cascade-execution capability as a plain delegate on
/// RetireAsync instead.
/// </summary>
public sealed class AdvanceRetirementHandler
{
    private readonly WorkflowDbContext _db;
    private readonly IWorkflowClock _clock;

    public AdvanceRetirementHandler(WorkflowDbContext db, IWorkflowClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task RetireAsync(
        Guid expenseRequestId,
        Guid advanceRequestId,
        decimal amountAppliedNgn,
        Func<Guid, ActingUser, TransitionRequest, CancellationToken, Task<RequestActionResult>> executeCascaded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executeCascaded);

        var advance = await _db.CashAdvanceRequests
            .FirstOrDefaultAsync(a => a.RequestId == advanceRequestId, cancellationToken)
            ?? throw new InvalidOperationException($"Cash advance '{advanceRequestId}' does not exist.");

        var now = _clock.UtcNow;

        // Must happen before executeCascaded: the RETIRE transition's guards
        // (RetirementBalanceNgn > 0 / <= 0, see cash-advance.workflow.json)
        // choose PARTIALLY_RETIRED vs CLOSED based on the balance this claim
        // leaves behind, not the balance before it.
        advance.RetiredAmountNgn += amountAppliedNgn;
        advance.RecalculateRetirementStatus(now);

        _db.AdvanceRetirementLinks.Add(new AdvanceRetirementLink
        {
            ExpenseRequestId = expenseRequestId,
            CashAdvanceRequestId = advanceRequestId,
            AmountAppliedNgn = amountAppliedNgn,
            AppliedAt = now
        });

        // RETIRE's actor spec is resolver: "Requester" -- the paper form
        // treats retiring an advance as the advance-holder's own act,
        // regardless of who authorised the claim that triggered it here.
        var actingUser = new ActingUser(advance.RequesterId, new HashSet<string>());
        var transitionRequest = new TransitionRequest("RETIRE");

        var result = await executeCascaded(advanceRequestId, actingUser, transitionRequest, cancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Retiring advance '{advanceRequestId}' against claim '{expenseRequestId}' failed: " +
                $"{result.Message ?? result.Outcome.ToString()}");
        }
    }
}
