using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Infrastructure.Notifications;

/// <summary>
/// One resolution attempt: the addresses found, and the specifiers that could
/// not be resolved to anyone.
/// </summary>
public sealed record RecipientResolution(
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> UnresolvedSpecifiers);

/// <summary>
/// Turns the recipient specifiers a workflow definition declares
/// ("CurrentActor", "Requester", "EscalationTarget", ...) into email
/// addresses.
///
/// Most of the work already exists: <see cref="IActorResolver"/> resolves
/// Requester, Beneficiary, CurrentActor, LineManagerOf and DepartmentHeadOf,
/// and expands each through active delegations — so a notification follows a
/// delegation the same way authority does. Reusing it means a person who is
/// authorised to act and a person who is told to act cannot drift apart,
/// which they would immediately if this had its own copy of the rules.
///
/// UNRESOLVED SPECIFIERS ARE REPORTED, NOT SWALLOWED
/// -------------------------------------------------
/// "FinanceManager" is a role, and there is no role-membership store for
/// anything to consult — EmployeeActorResolver says so itself, returning an
/// empty set for role-only specs and letting the engine treat that as "the
/// whole role". That convention works for authorisation, where an empty set
/// widens access to everyone in the role. It is exactly wrong for
/// notification, where it would silently mean "send to nobody".
///
/// So unresolved specifiers come back named. The dispatcher fails those
/// messages with the specifier in LastError rather than marking them sent,
/// because a finance approval nobody was told about is the failure this
/// system exists to prevent.
/// </summary>
public sealed class NotificationRecipientResolver
{
    private readonly WorkflowDbContext _db;
    private readonly IActorResolver _actorResolver;
    private readonly NotificationOptions _options;

    public NotificationRecipientResolver(
        WorkflowDbContext db, IActorResolver actorResolver, NotificationOptions options)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<RecipientResolution> ResolveAsync(
        IReadOnlyList<string> specifiers,
        Request request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specifiers);
        ArgumentNullException.ThrowIfNull(request);

        var employeeIds = new HashSet<Guid>();
        var mailboxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();

        foreach (var specifier in specifiers)
        {
            var spec = MapToActorSpec(specifier);

            if (spec is null)
            {
                // A role rather than a person. Checked only after the
                // person-resolvers, so a configured mailbox can never shadow
                // "Requester" or "CurrentActor" -- it fills the gap those
                // leave, and does not compete with them.
                if (_options.RoleMailboxes.TryGetValue(specifier, out var mailbox) &&
                    !string.IsNullOrWhiteSpace(mailbox))
                {
                    mailboxes.Add(mailbox.Trim());
                    continue;
                }

                unresolved.Add(specifier);
                continue;
            }

            var resolved = await _actorResolver.ResolveAsync(spec, request, cancellationToken);

            if (resolved.Count == 0)
            {
                unresolved.Add(specifier);
                continue;
            }

            employeeIds.UnionWith(resolved);
        }

        if (employeeIds.Count > 0)
        {
            var employeeAddresses = await _db.Employees
                .AsNoTracking()
                .Where(e => employeeIds.Contains(e.Id) && e.IsActive && e.Email != "")
                .Select(e => e.Email)
                .Distinct()
                .ToListAsync(cancellationToken);

            mailboxes.UnionWith(employeeAddresses);
        }

        // One case-insensitive set for both, so a role mailbox that happens to
        // be an individual's address does not produce two copies of the same
        // email differing only in capitalisation.
        return new RecipientResolution(mailboxes.ToList(), unresolved);
    }

    /// <summary>
    /// Maps a notification specifier onto the actor resolver's vocabulary.
    /// Returns null where no mapping exists, which the caller reports rather
    /// than treating as "nobody".
    /// </summary>
    private static ActorSpec? MapToActorSpec(string specifier) => specifier switch
    {
        "Requester" => new ActorSpec { Resolver = "Requester" },
        "Beneficiary" => new ActorSpec { Resolver = "Beneficiary" },
        "CurrentActor" => new ActorSpec { Resolver = "CurrentActor" },

        // The line manager of the person who raised the request, not of
        // whoever happens to hold it now.
        "LineManager" => new ActorSpec { Resolver = "LineManagerOf", Arg = "RequesterId" },

        // After EscalationSweep moves a request, CurrentActorId names the
        // escalation target — that is what escalation means here. Resolving
        // it to the same person is correct rather than lazy: the notification
        // is dispatched after the move, so "who is it with now" and "who was
        // it escalated to" are the same question.
        "EscalationTarget" => new ActorSpec { Resolver = "CurrentActor" },

        // "FinanceManager" and any other role-only specifier: no membership
        // store exists. Deliberately unmapped so it surfaces as a named
        // failure instead of an empty recipient list.
        _ => null
    };
}
