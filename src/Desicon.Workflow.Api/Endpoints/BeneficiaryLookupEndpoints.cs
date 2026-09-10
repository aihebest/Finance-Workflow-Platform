using Desicon.Workflow.Api.Http;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.People;
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
/// So this is a narrow surface, not the directory that file warns against:
/// no edit, and deliberately no bank details, anywhere.
///
/// It gained a create on 10 September 2026, and the distinction still holds.
/// What it creates is a *name* — a payee row with no account behind it, which
/// no money can reach until the desk that pays records one. Writing where the
/// money goes remains scoped to a claim, audited, and split from the person who
/// raised it. See CreateAsync.
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
        app.MapPost("/api/v1/beneficiaries", CreateAsync).RequireAuthorization();
    }

    public sealed record CreateBeneficiaryDto(string? Name);

    /// <summary>
    /// Naming a payee who is not on the list yet.
    /// </summary>
    /// <remarks>
    /// Asked for by Aihe on 10 September 2026: the beneficiary on DEL-AC-FRM-002
    /// should be typable, because the person collecting the money is often a
    /// vendor, a casual worker or a new starter who exists in no list here. The
    /// form was unfillable for them, which is the same dead end this file was
    /// created to fix -- one step further out.
    ///
    /// WHY A REQUESTER MAY DO THIS, WHEN THEY MAY NOT SET BANK DETAILS
    /// ---------------------------------------------------------------
    /// This creates a name and nothing else. The row has no bank details, so it
    /// is inert: EXECUTE_PAYMENT guards on
    /// <c>PaymentMethod == 'Cash' || BeneficiaryHasBankDetails == true</c>, and
    /// a payee created here satisfies neither above the cash threshold. Money
    /// cannot reach it until somebody records an account, and that is
    /// BeneficiaryEndpoints -- scoped to a claim, audited, and subject to the
    /// maker-checker rule that stops the person who set the account also
    /// approving the payment into it.
    ///
    /// So the split is deliberate: a requester says who they paid; a different
    /// desk says where the money goes. Letting the requester do both is exactly
    /// what that rule exists to prevent.
    ///
    /// AN EXACT NAME MATCH IS REFUSED RATHER THAN REUSED
    /// -------------------------------------------------
    /// Returning the existing row would be convenient and wrong. Two employees
    /// in this system share the display name "Best Aihebholoria", and on 9
    /// August 2026 a claim was raised against the wrong one -- §3e of the
    /// go-live checklist. Silently resolving a typed name to whichever row
    /// matched first would rebuild that fault and hide it inside a convenience.
    /// Creating a duplicate would be worse still. So the caller is told the name
    /// exists and sent to the list, where staff number and email distinguish
    /// people a name cannot.
    /// </remarks>
    private static async Task<IResult> CreateAsync(
        CreateBeneficiaryDto dto,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        CancellationToken cancellationToken)
    {
        var name = dto.Name?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            return ProblemResults.BadRequest("'name' is required.", httpRequest.Path);
        }

        if (name.Length > 200)
        {
            return ProblemResults.BadRequest(
                "'name' is longer than 200 characters.", httpRequest.Path);
        }

        var existing = await db.Beneficiaries
            .AsNoTracking()
            .Where(b => b.Name == name)
            .Select(b => new { b.Id, b.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return ProblemResults.BadRequest(
                $"'{existing.Name}' is already on the list of people this claim can be paid to. " +
                "Choose them there rather than adding a second one -- two payees with the same " +
                "name is how a claim gets paid to the wrong person.",
                httpRequest.Path);
        }

        var beneficiary = new Beneficiary
        {
            Type = BeneficiaryType.Other,
            Name = name,

            // Deliberately blank. Bank details are recorded against a claim by
            // the desk that pays it -- see the remarks above and
            // BeneficiaryEndpoints.
            BankName = string.Empty,
            BankAccountNumber = string.Empty
        };

        db.Beneficiaries.Add(beneficiary);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/v1/beneficiaries?search={Uri.EscapeDataString(name)}", new
        {
            beneficiary.Id,
            Type = beneficiary.Type.ToString(),
            beneficiary.Name,
            HasBankDetails = false,
            StaffNumber = (string?)null,
            Email = (string?)null
        });
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

                // Who this actually is, beyond a name.
                //
                // Two employees can share a display name -- dev has two rows
                // reading "Best Aihebholoria" -- and on 9 Aug 2026 a claim was
                // raised against the wrong one. Nothing downstream could have
                // caught it: every approval screen shows a name too, so the
                // Director of Finance would have authorised it and Treasury
                // would have paid it, with a complete and honest audit trail
                // behind a payment to the wrong person.
                //
                // Null for vendors and one-off payees, which have no employee
                // row. Correlated subqueries rather than a join so the shape
                // of the projection stays flat and Beneficiary needs no
                // navigation property it does not otherwise want.
                StaffNumber = db.Employees
                    .Where(e => e.Id == b.EmployeeId)
                    .Select(e => e.StaffNumber)
                    .FirstOrDefault(),

                Email = db.Employees
                    .Where(e => e.Id == b.EmployeeId)
                    .Select(e => e.Email)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(beneficiaries);
    }
}
