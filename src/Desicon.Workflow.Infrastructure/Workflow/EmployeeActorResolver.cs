using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Infrastructure.Workflow;

/// <summary>
/// Resolves ActorSpec.Resolver against real Employee/Department/Beneficiary/
/// Delegation data. Exactly five resolvers: Requester, Beneficiary,
/// LineManagerOf, DepartmentHeadOf, CurrentActor. ProjectManagerOf is named
/// in WorkflowDefinitionValidator.KnownResolvers but nothing in the People
/// model backs it yet, so it throws rather than silently resolving to no one.
///
/// Every resolution is expanded for active delegations: if employee B holds a
/// delegation from A covering the current instant, B is added to whatever set
/// A resolved into. This is one level only -- a delegates for a delegate is
/// out of scope, matching the brief's "if B holds a delegation from A" wording.
///
/// Results are cached per (RequestId, Resolver, Arg) for the lifetime of this
/// instance. Register this resolver scoped-per-request in DI so that lifetime
/// matches "cache per request scope": one instance, and therefore one cache,
/// per WorkflowEngine.ExecuteAsync / GetAvailableTransitionsAsync call.
/// </summary>
public sealed class EmployeeActorResolver : IActorResolver
{
    private static readonly HashSet<Guid> EmptySet = new();

    private readonly WorkflowDbContext _db;
    private readonly IWorkflowClock _clock;
    private readonly Dictionary<(Guid RequestId, string Resolver, string? Arg), IReadOnlySet<Guid>> _cache = new();

    public EmployeeActorResolver(WorkflowDbContext db, IWorkflowClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<IReadOnlySet<Guid>> ResolveAsync(
        ActorSpec spec, IWorkflowSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(subject);

        if (spec.Resolver is null)
        {
            // Role-only spec: WorkflowEngine.IsAuthorisedAsync treats an empty
            // set here as "the whole role membership", not "nobody" -- there
            // is no role-membership store for this resolver to consult, so it
            // returns empty and lets the engine apply that convention.
            return EmptySet;
        }

        var cacheKey = (subject.RequestId, spec.Resolver, spec.Arg);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var baseSet = await ResolveBaseAsync(spec, subject, cancellationToken);
        var expanded = await ExpandWithDelegatesAsync(baseSet, cancellationToken);

        _cache[cacheKey] = expanded;
        return expanded;
    }

    private async Task<HashSet<Guid>> ResolveBaseAsync(
        ActorSpec spec, IWorkflowSubject subject, CancellationToken cancellationToken)
    {
        switch (spec.Resolver)
        {
            case "Requester":
                return new HashSet<Guid> { subject.RequesterId };

            case "Beneficiary":
                return subject.TryGetField("BeneficiaryId", out var beneficiaryRaw) &&
                       TryReadGuid(beneficiaryRaw, out var beneficiaryId)
                    ? await ResolveBeneficiaryEmployeeAsync(beneficiaryId, cancellationToken)
                    : EmptySet;

            case "LineManagerOf":
                return await ResolveLineManagerAsync(spec.Arg, subject, cancellationToken);

            case "DepartmentHeadOf":
                return await ResolveDepartmentHeadAsync(spec.Arg, subject, cancellationToken);

            case "CurrentActor":
                return subject.TryGetField("CurrentActorId", out var currentActorRaw) &&
                       TryReadGuid(currentActorRaw, out var currentActorId)
                    ? new HashSet<Guid> { currentActorId }
                    : EmptySet;

            default:
                throw new NotSupportedException(
                    $"Actor resolver '{spec.Resolver}' is not implemented.");
        }
    }

    private async Task<HashSet<Guid>> ResolveBeneficiaryEmployeeAsync(
        Guid beneficiaryId, CancellationToken cancellationToken)
    {
        var employeeId = await _db.Beneficiaries
            .AsNoTracking()
            .Where(b => b.Id == beneficiaryId)
            .Select(b => b.EmployeeId)
            .FirstOrDefaultAsync(cancellationToken);

        // A vendor or "Other" beneficiary has no EmployeeId and so never has
        // a login of their own -- correctly resolves to no one, not an error.
        return employeeId is { } id ? new HashSet<Guid> { id } : EmptySet;
    }

    private async Task<HashSet<Guid>> ResolveLineManagerAsync(
        string? arg, IWorkflowSubject subject, CancellationToken cancellationToken)
    {
        if (arg is null || !subject.TryGetField(arg, out var raw) || !TryReadGuid(raw, out var employeeId))
        {
            return EmptySet;
        }

        var managerId = await _db.Employees
            .AsNoTracking()
            .Where(e => e.Id == employeeId)
            .Select(e => e.LineManagerId)
            .FirstOrDefaultAsync(cancellationToken);

        // No manager on file (top of the hierarchy, or an unknown employee
        // id) means no one is authorised at this step -- a data problem for
        // an admin to fix, not something this resolver should paper over.
        return managerId is { } id ? new HashSet<Guid> { id } : EmptySet;
    }

    private async Task<HashSet<Guid>> ResolveDepartmentHeadAsync(
        string? arg, IWorkflowSubject subject, CancellationToken cancellationToken)
    {
        if (arg is null || !subject.TryGetField(arg, out var raw) || raw is not int departmentId)
        {
            return EmptySet;
        }

        var headId = await _db.Departments
            .AsNoTracking()
            .Where(d => d.Id == departmentId)
            .Select(d => d.DepartmentHeadId)
            .FirstOrDefaultAsync(cancellationToken);

        return headId is { } id ? new HashSet<Guid> { id } : EmptySet;
    }

    private async Task<IReadOnlySet<Guid>> ExpandWithDelegatesAsync(
        HashSet<Guid> baseSet, CancellationToken cancellationToken)
    {
        if (baseSet.Count == 0)
        {
            return baseSet;
        }

        var now = _clock.UtcNow;

        var delegateIds = await _db.Delegations
            .AsNoTracking()
            .Where(d => d.IsActive && d.StartsAt <= now && d.EndsAt >= now && baseSet.Contains(d.FromEmployeeId))
            .Select(d => d.ToEmployeeId)
            .ToListAsync(cancellationToken);

        if (delegateIds.Count == 0)
        {
            return baseSet;
        }

        baseSet.UnionWith(delegateIds);
        return baseSet;
    }

    /// <summary>
    /// A non-null <c>Guid?</c> boxes as a plain boxed <see cref="Guid"/>, so
    /// this pattern-matches correctly whether the source field was declared
    /// <c>Guid</c> or <c>Guid?</c>. A null <c>Guid?</c> boxes as a null
    /// reference and correctly falls through to false.
    /// </summary>
    private static bool TryReadGuid(object? raw, out Guid id)
    {
        if (raw is Guid g)
        {
            id = g;
            return true;
        }

        id = Guid.Empty;
        return false;
    }
}
