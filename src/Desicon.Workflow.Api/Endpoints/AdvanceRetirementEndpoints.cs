using Desicon.Workflow.Api.Security;
using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Security;
using Desicon.Workflow.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Api.Endpoints;

/// <summary>
/// Drafting is a plain insert, not a guarded transition -- nothing here goes
/// through RequestActionService/WorkflowEngine, because a DRAFT has no
/// predecessor state an actor needed authority to leave. The engine gets
/// involved from SUBMIT onward, same as any other expense claim.
/// </summary>
public static class AdvanceRetirementEndpoints
{
    public static void MapAdvanceRetirementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/advances/{id:guid}/retire", RetireAsync);
    }

    private static async Task<IResult> RetireAsync(
        Guid id,
        WorkflowDbContext db,
        IWorkflowDefinitionProvider definitions,
        IRequestNumberGenerator numberGenerator,
        IWorkflowClock clock,
        IBankDetailsAuditor bankDetailsAuditor,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var advance = await db.CashAdvanceRequests
            .Include(a => a.Lines)
            .FirstOrDefaultAsync(a => a.RequestId == id, cancellationToken);

        if (advance is null)
        {
            return Results.NotFound($"Cash advance '{id}' does not exist.");
        }

        if (advance.RetirementBalanceNgn <= 0)
        {
            return Results.Conflict($"Cash advance '{id}' has no outstanding balance to retire.");
        }

        var now = clock.UtcNow;
        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);
        var effectiveActorId = actingUser.OnBehalfOf ?? actingUser.UserId;

        // advance.RequestId, not the not-yet-created expense's, is the
        // SecurityEvent context here: the expense row does not exist in the
        // database yet (it is only added to the DbContext below), and
        // ISecurityEventWriter commits through its own connection that must
        // see a real, already-persisted RequestId to satisfy the FK.
        var beneficiary = await FindOrCreateEmployeeBeneficiaryAsync(
            db, bankDetailsAuditor, advance.RequesterId, advance.RequestId, advance.ModuleKey,
            effectiveActorId, now, cancellationToken);

        // The current EXPENSE version, deliberately, not the advance's. This
        // creates a NEW claim, and a new request is raised under whatever
        // process is in force today. The advance's version pins the advance.
        var definition = await definitions.GetAsync("EXPENSE", cancellationToken);

        var expense = new ExpenseRequest
        {
            RequestNumber = await numberGenerator.GenerateAsync(definition, now, cancellationToken),
            FormRevision = definition.FormRevision,
            CurrentState = definition.InitialState.Key,
            StateEnteredAt = now,
            RequesterId = advance.RequesterId,
            DepartmentId = advance.DepartmentId,
            BeneficiaryId = beneficiary.Id,
            RetiresAdvanceId = advance.RequestId,

            // The outstanding balance, not the advance's original total:
            // across several partial claims this is what "Cash Advance
            // Taken" should net off each time, so the second claim does not
            // re-net an amount the first claim already retired.
            AdvanceAmountNgn = advance.RetirementBalanceNgn
        };

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var lineNumber = 1;

        foreach (var advanceLine in advance.Lines)
        {
            expense.Lines.Add(new ExpenseLine
            {
                RequestId = expense.RequestId,
                LineNumber = lineNumber++,
                Description = advanceLine.Description,

                // Not on AdvanceLine -- the advance carries allocation and
                // station scope at the header, not per line (see
                // CashAdvanceRequest), so every copied line inherits the
                // header's allocation and today's date stands in for an
                // expense date the advance never recorded.
                ExpenseDate = today,
                ProjectCode = advance.ProjectCode,
                CostCentreCode = advance.CostCentreCode,

                CurrencyCode = advanceLine.CurrencyCode,
                Amount = advanceLine.Amount,
                FxRate = advanceLine.FxRate,
                FxRateDate = advanceLine.FxRateDate,
                AmountNgn = advanceLine.AmountNgn
            });
        }

        expense.RecalculateTotals();
        expense.ApplyPaymentMethodPolicy(definition.GetPolicyValue("PAYMENT_METHOD_THRESHOLD_NGN", now));

        db.ExpenseRequests.Add(expense);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/api/v1/expenses/{expense.RequestId}",
            new AdvanceRetirementDraftResponse(
                expense.RequestId,
                expense.RequestNumber,
                expense.CurrentState,
                expense.AdvanceAmountNgn,
                expense.Lines.Count));
    }

    /// <summary>
    /// ExpenseRequest.BeneficiaryId is a required real FK (see
    /// ExpenseRequestConfiguration) with no notion of "the requester paying
    /// themselves" built in -- every claim pays a Beneficiary row. An advance
    /// being retired has no beneficiary of its own to reuse, so the
    /// requester's own Employee-type Beneficiary is found or created here.
    /// Bank details are sourced from the Employee record, never left blank:
    /// an Employee-type Beneficiary with no bank details on file would pass
    /// every guard silently and only surface as a payment failure at
    /// Treasury, so this fails loudly instead, before the row is ever
    /// created.
    /// </summary>
    /// <summary>
    /// Internal rather than private: RequestEndpoints resolves "pay me" on
    /// expense drafts through this same method. Two copies of "find or create
    /// the beneficiary for an employee" would be two places that could drift
    /// on whether bank details are required and whether the change is
    /// audited -- and only one of them would be the one anybody reads.
    /// </summary>
    internal static async Task<Beneficiary> FindOrCreateEmployeeBeneficiaryAsync(
        WorkflowDbContext db,
        IBankDetailsAuditor bankDetailsAuditor,
        Guid employeeId,
        Guid contextRequestId,
        string contextModuleKey,
        Guid actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await db.Beneficiaries
            .FirstOrDefaultAsync(
                b => b.Type == BeneficiaryType.Employee && b.EmployeeId == employeeId, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var employee = await db.Employees
            .FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken)
            ?? throw new InvalidOperationException($"Employee '{employeeId}' does not exist.");

        if (string.IsNullOrWhiteSpace(employee.BankName) || string.IsNullOrWhiteSpace(employee.BankAccountNumber))
        {
            throw new MissingBankDetailsException(
                $"Employee '{employee.FullName}' ({employee.StaffNumber}) has no bank details on file. " +
                "Record bank details for this employee before an advance can be retired to them.");
        }

        var beneficiary = new Beneficiary
        {
            Type = BeneficiaryType.Employee,
            Name = employee.FullName,
            EmployeeId = employee.Id
        };

        await bankDetailsAuditor.ApplyChangeAsync(
            beneficiary, employee.BankName, employee.BankAccountNumber,
            contextRequestId, contextModuleKey, actorId, now, cancellationToken);

        db.Beneficiaries.Add(beneficiary);
        return beneficiary;
    }
}

/// <summary>Thrown when a payee's underlying Employee record has no bank
/// details on file. Distinct from a generic InvalidOperationException (404)
/// or ArgumentException (400) -- the request is well-formed and the employee
/// exists, but the data needed to pay them does not, which is a 409-shaped
/// state conflict.</summary>
public sealed class MissingBankDetailsException : Exception
{
    public MissingBankDetailsException(string message) : base(message)
    {
    }
}

public sealed record AdvanceRetirementDraftResponse(
    Guid ExpenseRequestId,
    string RequestNumber,
    string CurrentState,
    decimal AdvanceAmountNgn,
    int LineCount);
