using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Infrastructure.Security;

/// <summary>
/// The IDOR control doc 04 §1 requires: "Every read goes through an
/// authorisation filter that scopes by requester, reporting line or role --
/// never by trusting an ID in the URL." Deliberately separate from
/// WorkflowEngine's authorisation, which governs whether an action may be
/// taken (write path); this governs whether a request may be seen at all
/// (read path), and is broader -- a line manager can see a report's request
/// without being the current approver on it.
///
/// A caller can see a request when any of the following holds:
///  - they raised it (or a delegator who raised it delegated to them);
///  - it is currently in their queue (Request.CurrentActorId, or a
///    role-gated queue they hold a role for -- see InboxStateIndex);
///  - they are the requester's line manager or department head (direct
///    reporting line, one level -- the same convention EmployeeActorResolver
///    applies to LineManagerOf/DepartmentHeadOf);
///  - they hold a role with cross-cutting visibility (CostControlOfficer,
///    TreasuryOfficer, FinanceManager, ProcurementOfficer): these roles
///    process every request that reaches their stage regardless of
///    department, so scoping them to "their queue only" would hide history
///    they are expected to review. Administrator is deliberately excluded --
///    doc 04 is explicit that Administrator cannot read request line detail.
/// </summary>
public sealed class ReadAccessScope
{
    /// <remarks>
    /// Read access, not write. Splitting FinanceOfficer into a Cost Control
    /// and a Treasury role at workflow version 3 separates who may *act* at
    /// each stage; it deliberately does not narrow who may *look*. Both desks
    /// handle the same claims in sequence, and a Treasury officer who cannot
    /// see what Cost Control queried is being asked to post blind.
    ///
    /// FinanceOfficer was removed on 19 August 2026, once no request was
    /// pinned to version 2 any more. It granted everything both replacement
    /// roles grant; leaving it here would have kept a way to read every
    /// request in the system under a role nobody is supposed to hold.
    ///
    /// DirectorOfFinance added 25 August 2026, after the Director of Finance
    /// followed an approval email to a request waiting on him and was told
    /// "You do not have access to this request."
    ///
    /// Cost Control, Treasury, the Accounts Manager and Procurement were all
    /// here. The one person who authorises every payment in the company was
    /// not -- and DMD_APPROVAL is role-gated, so CurrentActorId is null there
    /// by design, which removed the only other clause that could have let him
    /// in. The gate this platform is built around could not open the thing it
    /// gates.
    ///
    /// Nothing failed. The notification sent, the link resolved, sign-in
    /// worked, and the refusal was correct according to this list. It was the
    /// list that was wrong, and only a real approver following a real email
    /// could find it.
    /// </remarks>
    private static readonly HashSet<string> CrossCuttingRoles =
        new(StringComparer.Ordinal)
        {
            "CostControlOfficer",
            "TreasuryOfficer",
            "FinanceManager",
            "DirectorOfFinance",
            "ProcurementOfficer"
        };

    private readonly WorkflowDbContext _db;
    private readonly IWorkflowClock _clock;
    private readonly InboxStateIndex _inboxStates;

    public ReadAccessScope(WorkflowDbContext db, IWorkflowClock clock, InboxStateIndex inboxStates)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _inboxStates = inboxStates ?? throw new ArgumentNullException(nameof(inboxStates));
    }

    /// <summary>
    /// Whether the request is sitting in a queue one of these roles holds.
    /// </summary>
    /// <remarks>
    /// The class docstring above has always said read access includes "a
    /// role-gated queue they hold a role for -- see InboxStateIndex". It did
    /// not: CanReadAsync and ScopeAsync both checked only CurrentActorId,
    /// which is null by design on a role-gated state because no single person
    /// holds it.
    ///
    /// So the documented rule and the enforced rule differed, and the
    /// difference was invisible for as long as every role that needed it
    /// happened to be in CrossCuttingRoles. The Director of Finance was not,
    /// and the gap surfaced as a senior approver being refused a request that
    /// was waiting on him.
    ///
    /// Adding DirectorOfFinance to that set fixes this instance. This fixes
    /// the rule, so the next role added to a definition does not depend on
    /// somebody also remembering a list in a different file.
    /// </remarks>
    private bool HoldsRoleGatedQueue(Request request, IReadOnlySet<string> roles) =>
        _inboxStates.StatesFor(roles)
            .Any(s => string.Equals(s.ModuleKey, request.ModuleKey, StringComparison.Ordinal)
                   && string.Equals(s.State, request.CurrentState, StringComparison.Ordinal));

    /// <summary>Applies the scoping filter to a Requests (or subclass)
    /// queryable. Compose this before any caller-supplied filter (state,
    /// module, date range) so a caller cannot widen it by construction.</summary>
    public async Task<IQueryable<TRequest>> ScopeAsync<TRequest>(
        IQueryable<TRequest> query,
        Employee viewer,
        IReadOnlySet<string> roles,
        CancellationToken cancellationToken)
        where TRequest : Request
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(roles);

        if (roles.Overlaps(CrossCuttingRoles))
        {
            return query;
        }

        var visibleActorIds = await ResolveVisibleActorIdsAsync(viewer.Id, cancellationToken);

        var visibleRequesterIds = new HashSet<Guid>(visibleActorIds);
        var reportIds = await _db.Employees
            .AsNoTracking()
            .Where(e => e.LineManagerId != null && visibleActorIds.Contains(e.LineManagerId.Value))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
        visibleRequesterIds.UnionWith(reportIds);

        var headedDepartmentIds = await _db.Departments
            .AsNoTracking()
            .Where(d => d.DepartmentHeadId != null && visibleActorIds.Contains(d.DepartmentHeadId.Value))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        // The same role-gated queues CanReadAsync honours, flattened to the
        // "module|state" keys GetInboxAsync already uses. Without this a list
        // and the detail page would disagree about the same request, which is
        // worse than either rule alone.
        var roleStateKeys = _inboxStates.StatesFor(roles)
            .Select(s => s.ModuleKey + "|" + s.State)
            .ToHashSet(StringComparer.Ordinal);

        return query.Where(r =>
            visibleRequesterIds.Contains(r.RequesterId) ||
            (r.CurrentActorId != null && visibleActorIds.Contains(r.CurrentActorId.Value)) ||
            headedDepartmentIds.Contains(r.DepartmentId) ||
            roleStateKeys.Contains(r.ModuleKey + "|" + r.CurrentState));
    }

    /// <summary>True if the viewer may see this single, already-loaded request.
    /// Detail endpoints use this rather than re-querying through ScopeAsync.</summary>
    public async Task<bool> CanReadAsync(
        Request request, Employee viewer, IReadOnlySet<string> roles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(roles);

        if (roles.Overlaps(CrossCuttingRoles))
        {
            return true;
        }

        var visibleActorIds = await ResolveVisibleActorIdsAsync(viewer.Id, cancellationToken);

        if (visibleActorIds.Contains(request.RequesterId) ||
            (request.CurrentActorId is { } currentActorId && visibleActorIds.Contains(currentActorId)) ||
            HoldsRoleGatedQueue(request, roles))
        {
            return true;
        }

        var requesterManagerId = await _db.Employees
            .AsNoTracking()
            .Where(e => e.Id == request.RequesterId)
            .Select(e => e.LineManagerId)
            .FirstOrDefaultAsync(cancellationToken);

        if (requesterManagerId is { } managerId && visibleActorIds.Contains(managerId))
        {
            return true;
        }

        var departmentHeadId = await _db.Departments
            .AsNoTracking()
            .Where(d => d.Id == request.DepartmentId)
            .Select(d => d.DepartmentHeadId)
            .FirstOrDefaultAsync(cancellationToken);

        return departmentHeadId is { } headId && visibleActorIds.Contains(headId);
    }

    /// <summary>The viewer, plus anyone who has delegated to the viewer for
    /// the current instant -- one level, matching
    /// EmployeeActorResolver.ExpandWithDelegatesAsync's own convention, so a
    /// delegate's read access matches exactly what they can act on.</summary>
    private async Task<HashSet<Guid>> ResolveVisibleActorIdsAsync(Guid viewerId, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var delegatorIds = await _db.Delegations
            .AsNoTracking()
            .Where(d => d.IsActive && d.ToEmployeeId == viewerId && d.StartsAt <= now && d.EndsAt >= now)
            .Select(d => d.FromEmployeeId)
            .ToListAsync(cancellationToken);

        var visible = new HashSet<Guid>(delegatorIds) { viewerId };
        return visible;
    }
}
