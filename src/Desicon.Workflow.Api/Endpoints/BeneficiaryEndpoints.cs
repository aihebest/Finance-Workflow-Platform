using Desicon.Workflow.Api.Http;
using Desicon.Workflow.Api.Security;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Security;
using Desicon.Workflow.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Api.Endpoints;

/// <summary>
/// Editing bank details on the Beneficiary an expense claim pays. Deliberately
/// scoped to a specific claim rather than a general Beneficiary directory --
/// there is no such directory in this codebase, and Finance only ever touches
/// bank details while working a specific claim. Every change goes through
/// IBankDetailsAuditor, the same path FindOrCreateEmployeeBeneficiaryAsync
/// uses, so BankDetailsSetByUserId/BankDetailsSetAt and the SecurityEvent
/// audit trail cannot be bypassed by editing here instead.
/// </summary>
public static class BeneficiaryEndpoints
{
    public static void MapBeneficiaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/v1/requests/{id:guid}/beneficiary/bank-details", UpdateBankDetailsAsync)
            .RequireAuthorization();
    }

    private static async Task<IResult> UpdateBankDetailsAsync(
        Guid id,
        UpdateBankDetailsDto dto,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        IBankDetailsAuditor bankDetailsAuditor,
        ICurrentUserAccessor currentUser,
        IWorkflowClock clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.BankName) || string.IsNullOrWhiteSpace(dto.BankAccountNumber))
        {
            return ProblemResults.BadRequest(
                "'bankName' and 'bankAccountNumber' are both required.", httpRequest.Path);
        }

        var expense = await db.ExpenseRequests.FirstOrDefaultAsync(e => e.RequestId == id, cancellationToken);
        if (expense is null)
        {
            return ProblemResults.NotFound("Expense request", id, httpRequest.Path);
        }

        // Once AuthorisedByUserId is set, the maker-checker check this data
        // feeds has already run -- editing the account number after that
        // point is exactly the swap-the-account-before-payment risk
        // maker-checker exists to stop, not a case to accommodate.
        if (expense.AuthorisedByUserId is not null)
        {
            return ProblemResults.BadRequest(
                "Bank details cannot be changed after this claim has been authorised for payment.",
                httpRequest.Path);
        }

        var beneficiary = await db.Beneficiaries
            .FirstOrDefaultAsync(b => b.Id == expense.BeneficiaryId, cancellationToken);
        if (beneficiary is null)
        {
            return ProblemResults.NotFound("Beneficiary", expense.BeneficiaryId, httpRequest.Path);
        }

        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);
        var effectiveActorId = actingUser.OnBehalfOf ?? actingUser.UserId;

        await bankDetailsAuditor.ApplyChangeAsync(
            beneficiary, dto.BankName, dto.BankAccountNumber,
            expense.RequestId, expense.ModuleKey, effectiveActorId, clock.UtcNow, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { beneficiary.Id, beneficiary.BankName, beneficiary.BankAccountNumber });
    }

    private sealed record UpdateBankDetailsDto(string BankName, string BankAccountNumber);
}
