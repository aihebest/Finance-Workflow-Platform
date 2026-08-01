using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;
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
///  - they hold a role with cross-cutting visibility (FinanceOfficer,
///    FinanceManager, ProcurementOfficer): these roles process every
///    request that reaches their stage regardless of department, so
///    scoping them to "their queue only" would hide history they are
///    expected to review. Administrator is deliberately excluded -- doc 04
///    is explicit that Administrator cannot read request line detail.
/// </summary>
public sealed class ReadAccessScope
{
    private static readonly HashSet<string> CrossCuttingRoles =
        new(StringComparer.Ordinal) { "FinanceOfficer", "FinanceManager", "ProcurementOfficer" };

    private readonly WorkflowDbContext _db;
    private readonly IWorkflowClock _clock;

    public ReadAccessScope(WorkflowDbContext db, IWorkflowClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

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

        return query.Where(r =>
            visibleRequesterIds.Contains(r.RequesterId) ||
            (r.CurrentActorId != null && visibleActorIds.Contains(r.CurrentActorId.Value)) ||
            headedDepartmentIds.Contains(r.DepartmentId));
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
            (request.CurrentActorId is { } currentActorId && visibleActorIds.Contains(currentActorId)))
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
