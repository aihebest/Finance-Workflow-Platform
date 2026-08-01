using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;
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

        var beneficiary = await FindOrCreateEmployeeBeneficiaryAsync(db, advance.RequesterId, cancellationToken);

        var definition = await definitions.GetAsync("EXPENSE", cancellationToken);
        var now = clock.UtcNow;

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
    /// Bank details are left blank: Employee carries none to source them
    /// from, and Finance can fill them in on the draft before submission.
    /// </summary>
    private static async Task<Beneficiary> FindOrCreateEmployeeBeneficiaryAsync(
        WorkflowDbContext db, Guid employeeId, CancellationToken cancellationToken)
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

        var beneficiary = new Beneficiary
        {
            Type = BeneficiaryType.Employee,
            Name = employee.FullName,
            BankName = string.Empty,
            BankAccountNumber = string.Empty,
            EmployeeId = employee.Id
        };

        db.Beneficiaries.Add(beneficiary);
        return beneficiary;
    }
}

public sealed record AdvanceRetirementDraftResponse(
    Guid ExpenseRequestId,
    string RequestNumber,
    string CurrentState,
    decimal AdvanceAmountNgn,
    int LineCount);
