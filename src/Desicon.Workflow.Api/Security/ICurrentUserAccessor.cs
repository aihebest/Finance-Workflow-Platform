using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.People;

namespace Desicon.Workflow.Api.Security;

/// <summary>
/// Resolves the authenticated Entra ID principal to the Employee record and
/// role set the rest of the API works in terms of. There is no local role
/// table (see Employee's own remarks and doc 04 §1) -- roles come straight
/// off the token's "roles" claim, Entra security groups surfaced as app
/// roles, not a database lookup.
/// </summary>
public interface ICurrentUserAccessor
{
    /// <summary>The signed-in employee. Throws if the token's object id does
    /// not match any Employee.EntraObjectId -- an authenticated principal with
    /// no corresponding staff record is a provisioning gap, not a request this
    /// API can serve.</summary>
    Task<Employee> GetEmployeeAsync(CancellationToken cancellationToken = default);

    /// <summary>The role set from the token's "roles" claim.</summary>
    IReadOnlySet<string> GetRoles();

    /// <summary>Employee plus roles, packaged as the engine's own ActingUser.
    /// OnBehalfOf is always null here -- this API has no "act as a delegate
    /// explicitly" affordance; a delegate reaches the same result by simply
    /// being present in the resolved actor set (see
    /// EmployeeActorResolver.ExpandWithDelegatesAsync).</summary>
    Task<ActingUser> GetActingUserAsync(CancellationToken cancellationToken = default);
}
