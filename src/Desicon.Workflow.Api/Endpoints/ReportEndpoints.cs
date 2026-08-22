using Desicon.Workflow.Api.Http;
using Desicon.Workflow.Api.Security;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Security;
using Desicon.Workflow.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Api.Endpoints;

/// <summary>
/// Two questions Finance cannot currently answer without asking somebody.
/// </summary>
/// <remarks>
/// Everything before this was per-person: My Inbox shows what is waiting for
/// <em>you</em>, My Advances what <em>you</em> owe. Nobody could see the whole
/// picture, so "how much cash are we holding out there" and "why has that
/// claim not been paid" were answered by phoning Accounts.
///
/// WHY THIS DOES NOT USE ReadAccessScope
/// -------------------------------------
/// That scopes a caller to requests they are party to -- their own, their
/// reports', their queue. It is exactly right for a request and exactly wrong
/// for a total: a Head of Department passing through it would see a
/// company-wide figure computed from the rows they happen to be allowed to
/// read, which is a number that means nothing and looks authoritative.
///
/// So access here is a flat role check, decided 22 August 2026: the four
/// finance roles and nobody else. A Head of Department sees their inbox, not
/// company spend.
/// </remarks>
public static class ReportEndpoints
{
    /// <summary>
    /// Who may see figures spanning departments.
    /// </summary>
    /// <remarks>
    /// Deliberately not Administrator. docs/04 is explicit that an
    /// administrator manages configuration and cannot read request detail;
    /// aggregate spend is request detail with the names removed, and "with the
    /// names removed" is not the same as "not sensitive".
    /// </remarks>
    private static readonly HashSet<string> ReportingRoles =
        new(StringComparer.Ordinal)
        {
            "CostControlOfficer",
            "TreasuryOfficer",
            "FinanceManager",
            "DirectorOfFinance"
        };

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/reports").RequireAuthorization();

        group.MapGet("/outstanding-advances", OutstandingAdvancesAsync);
        group.MapGet("/pipeline", PipelineAsync);
    }

    private static IResult? RefuseIfNotFinance(ICurrentUserAccessor currentUser, HttpRequest httpRequest) =>
        currentUser.GetRoles().Overlaps(ReportingRoles)
            ? null
            : ProblemResults.Forbidden(
                "Reports covering more than one department are restricted to Finance. Your own requests are on My Inbox.",
                httpRequest.Path);

    /// <summary>
    /// Company cash that has been released and not yet accounted for.
    /// </summary>
    /// <remarks>
    /// DEL-AC-FRM-003 makes an unretired advance the personal liability of the
    /// recipient. Nothing has ever shown that liability in one place, so the
    /// only way to know the total was to add it up by hand from the ledger.
    ///
    /// Sorted by how overdue, not by amount. A large advance retired on time is
    /// not a problem; a small one three weeks late is the beginning of one, and
    /// a report sorted by size hides exactly the rows worth chasing.
    /// </remarks>
    private static async Task<IResult> OutstandingAdvancesAsync(
        HttpRequest httpRequest,
        [FromServices] WorkflowDbContext db,
        [FromServices] ICurrentUserAccessor currentUser,
        [FromServices] IWorkflowClock clock,
        CancellationToken cancellationToken)
    {
        if (RefuseIfNotFinance(currentUser, httpRequest) is { } refusal)
        {
            return refusal;
        }

        var now = clock.UtcNow;

        var rows = await db.CashAdvanceRequests
            .AsNoTracking()
            .Where(a => a.ClosedAt == null && a.CashReleasedAt != null)
            .Join(db.Employees.AsNoTracking(), a => a.RequesterId, e => e.Id, (a, e) => new { a, e })
            .Join(db.Departments.AsNoTracking(), x => x.a.DepartmentId, d => d.Id, (x, d) => new
            {
                x.a.RequestId,
                x.a.RequestNumber,
                x.a.CurrentState,
                Requester = x.e.FullName,
                RequesterEmail = x.e.Email,
                Department = d.Code,
                DepartmentName = d.Name,
                x.a.TotalAmountNgn,
                x.a.RetiredAmountNgn,

                // Computed on the entity, so not a column and not filterable
                // in SQL. Derived here rather than pulled into memory first.
                BalanceNgn = x.a.TotalAmountNgn - x.a.RetiredAmountNgn,
                x.a.CashReleasedAt,
                x.a.RetirementDueDate,
                x.a.StationScope,
                x.a.Purpose
            })
            .Where(r => r.BalanceNgn > 0)
            .ToListAsync(cancellationToken);

        var advances = rows
            .Select(r => new
            {
                r.RequestId,
                r.RequestNumber,
                r.CurrentState,
                r.Requester,
                r.RequesterEmail,
                r.Department,
                r.DepartmentName,
                r.Purpose,
                StationScope = r.StationScope.ToString(),
                r.TotalAmountNgn,
                r.RetiredAmountNgn,
                r.BalanceNgn,
                r.CashReleasedAt,
                DueAt = r.RetirementDueDate,

                // Calendar days, as in the payment digest and for the same
                // reason: the money has been out of the company for that long
                // regardless of whose working week it was.
                DaysOverdue = r.RetirementDueDate is { } due && due < now
                    ? (int)Math.Floor((now - due).TotalDays)
                    : 0,

                IsOverdue = r.RetirementDueDate is { } d && d < now
            })
            .OrderByDescending(a => a.DaysOverdue)
            .ThenByDescending(a => a.BalanceNgn)
            .ToList();

        return Results.Ok(new
        {
            AsAt = now,
            Totals = new
            {
                Count = advances.Count,
                OutstandingNgn = advances.Sum(a => a.BalanceNgn),
                OverdueCount = advances.Count(a => a.IsOverdue),
                OverdueNgn = advances.Where(a => a.IsOverdue).Sum(a => a.BalanceNgn)
            },
            ByDepartment = advances
                .GroupBy(a => new { a.Department, a.DepartmentName })
                .Select(g => new
                {
                    g.Key.Department,
                    g.Key.DepartmentName,
                    Count = g.Count(),
                    OutstandingNgn = g.Sum(a => a.BalanceNgn),
                    OverdueCount = g.Count(a => a.IsOverdue)
                })
                .OrderByDescending(d => d.OutstandingNgn)
                .ToList(),
            Advances = advances
        });
    }

    /// <summary>
    /// Every open request, what state it is in, and how long it has sat there.
    /// </summary>
    /// <remarks>
    /// The answer to "why has my claim not been paid" without anyone phoning
    /// Accounts, and to "where does this actually jam" without anyone guessing.
    ///
    /// Holder is a person for a resolver-based state and a ROLE for a role-
    /// gated one -- COST_CONTROL_VERIFY is nobody's individually, which is why
    /// Request.CurrentActorId is null there by design. Reporting that as
    /// "unassigned" would be both wrong and alarming; it names the desk.
    ///
    /// Resolved against each request's OWN pinned definition version, not the
    /// current one. A request raised under an older version may sit in a state
    /// the current version does not declare, and showing it against today's
    /// process would quietly misattribute it.
    /// </remarks>
    private static async Task<IResult> PipelineAsync(
        HttpRequest httpRequest,
        [FromServices] WorkflowDbContext db,
        [FromServices] ICurrentUserAccessor currentUser,
        [FromServices] IWorkflowDefinitionProvider definitions,
        [FromServices] IWorkflowClock clock,
        CancellationToken cancellationToken)
    {
        if (RefuseIfNotFinance(currentUser, httpRequest) is { } refusal)
        {
            return refusal;
        }

        var now = clock.UtcNow;

        // (module, version, state) -> the role that gates it, if any.
        var all = await definitions.GetAllAsync(cancellationToken);
        var roleByState = new Dictionary<(string Module, int Version, string State), string>();

        foreach (var definition in all)
        {
            foreach (var transition in definition.Transitions)
            {
                if (transition.Actor.Resolver is null && transition.Actor.Role is { } role)
                {
                    roleByState.TryAdd((definition.ModuleKey, definition.Version, transition.From), role);
                }
            }
        }

        var rows = await db.Requests
            .AsNoTracking()
            .Where(r => r.ClosedAt == null && r.SubmittedAt != null)
            .GroupJoin(db.Employees.AsNoTracking(), r => r.CurrentActorId, e => (Guid?)e.Id, (r, es) => new { r, es })
            .SelectMany(x => x.es.DefaultIfEmpty(), (x, actor) => new
            {
                x.r.RequestId,
                x.r.RequestNumber,
                x.r.ModuleKey,
                x.r.DefinitionVersion,
                x.r.CurrentState,
                x.r.TotalAmountNgn,
                x.r.StateEnteredAt,
                x.r.SlaDueAt,
                x.r.RevisionNumber,
                ActorName = actor != null ? actor.FullName : null
            })
            .ToListAsync(cancellationToken);

        var requests = rows
            .Select(r => new
            {
                r.RequestId,
                r.RequestNumber,
                r.ModuleKey,
                r.CurrentState,
                r.TotalAmountNgn,
                WaitingSince = r.StateEnteredAt,
                DaysWaiting = Math.Max(0, (int)Math.Floor((now - r.StateEnteredAt).TotalDays)),

                // A name if one person holds it, the role if a desk does, and
                // null only if neither -- which would be a genuine defect and
                // is worth being able to see rather than papering over.
                Holder = r.ActorName
                    ?? (roleByState.TryGetValue((r.ModuleKey, r.DefinitionVersion, r.CurrentState), out var role)
                        ? role
                        : null),

                HolderIsRole = r.ActorName is null,
                r.SlaDueAt,
                SlaBreached = r.SlaDueAt is { } due && due < now,
                r.RevisionNumber
            })
            .OrderByDescending(r => r.DaysWaiting)
            .ThenByDescending(r => r.TotalAmountNgn)
            .ToList();

        return Results.Ok(new
        {
            AsAt = now,
            Totals = new
            {
                Count = requests.Count,
                ValueNgn = requests.Sum(r => r.TotalAmountNgn),
                BreachedCount = requests.Count(r => r.SlaBreached)
            },
            ByState = requests
                .GroupBy(r => new { r.ModuleKey, r.CurrentState, r.Holder, r.HolderIsRole })
                .Select(g => new
                {
                    g.Key.ModuleKey,
                    State = g.Key.CurrentState,
                    g.Key.Holder,
                    g.Key.HolderIsRole,
                    Count = g.Count(),
                    ValueNgn = g.Sum(r => r.TotalAmountNgn),
                    OldestDays = g.Max(r => r.DaysWaiting),
                    BreachedCount = g.Count(r => r.SlaBreached)
                })
                .OrderByDescending(s => s.OldestDays)
                .ToList(),
            Requests = requests
        });
    }
}
