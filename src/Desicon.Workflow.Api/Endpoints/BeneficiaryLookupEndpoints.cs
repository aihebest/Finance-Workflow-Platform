using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Api.Endpoints;

/// <summary>
/// Read-only lookup of who an expense claim can be paid to.
///
/// WHY THIS EXISTS, GIVEN BeneficiaryEndpoints SAYS IT SHOULD NOT
/// --------------------------------------------------------------
/// That file states there is deliberately no general Beneficiary directory:
/// bank-details editing is scoped to a specific claim because that is the
/// only context Finance ever touches it in. That reasoning holds for
/// *writing*. It does not cover reading, and the capture form for
/// DEL-AC-FRM-002 has a "Name of the Beneficiary" field whose payload
/// requires a BeneficiaryId — with no way to obtain one, the form is
/// unfillable.
///
/// So this is a narrow read surface, not the directory that file warns
/// against: no create, no edit, and deliberately no bank details.
///
/// WHAT IS NOT RETURNED, AND WHY
/// -----------------------------
/// BankAccountNumber is Always Encrypted and BankName identifies where money
/// goes. Neither belongs in a list a requester browses to pick a payee, and
/// including them would put every beneficiary's banking arrangements behind
/// a single authenticated GET. The form needs a name and an id; it gets a
/// name and an id. HasBankDetails is exposed because a claim raised against
/// a payee who cannot be paid is a dead end the requester should see before
/// filling in eleven lines, not after Finance rejects it.
/// </summary>
public static class BeneficiaryLookupEndpoints
{
    public static void MapBeneficiaryLookupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/beneficiaries", ListAsync).RequireAuthorization();
    }

    private static async Task<IResult> ListAsync(
        WorkflowDbContext db,
        string? search,
        CancellationToken cancellationToken)
    {
        var query = db.Beneficiaries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(b => b.Name.Contains(term));
        }

        var beneficiaries = await query
            .OrderBy(b => b.Name)
            // Bounded rather than unbounded. This grows with every vendor and
            // one-off payee ever used, and a picker that returns everything
            // is a picker nobody can use. The search parameter is the way to
            // reach past the first page.
            .Take(200)
            .Select(b => new
            {
                b.Id,
                Type = b.Type.ToString(),
                b.Name,

                // Derived from BankDetailsSetAt, not from the bank columns.
                //
                // Beneficiary.HasBankDetails checks BankName and
                // BankAccountNumber, but that cannot be evaluated in SQL:
                // BankAccountNumber is Always Encrypted, and deterministic
                // encryption only supports equality against a *parameter*
                // the driver can encrypt. A literal like "" cannot be
                // encrypted, so the comparison fails at the server with an
                // encryption scheme mismatch -- surfacing here as a 500 on a
                // read that looks entirely ordinary.
                //
                // BankDetailsSetAt is not encrypted and carries the same
                // meaning: IBankDetailsAuditor stamps it on every change, and
                // it is null only for a beneficiary that has never had bank
                // details set. Using it also keeps this endpoint from
                // touching the encrypted column at all, which means the
                // lookup needs no Key Vault round trip to answer.
                HasBankDetails = b.BankDetailsSetAt != null,
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(beneficiaries);
    }
}
