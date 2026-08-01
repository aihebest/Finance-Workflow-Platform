using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Api.Security;

public sealed class CurrentUserAccessor : ICurrentUserAccessor
{
    /// <summary>Entra ID v2.0 tokens carry the object id as "oid". Also
    /// accepted: the claim-mapping URI ASP.NET Core's JWT handler produces
    /// when MapInboundClaims is left at its (deprecated) default of true, so
    /// this keeps working regardless of how that option is set.</summary>
    private static readonly string[] ObjectIdClaimTypes =
    {
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier"
    };

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly WorkflowDbContext _db;
    private Employee? _cachedEmployee;

    public CurrentUserAccessor(IHttpContextAccessor httpContextAccessor, WorkflowDbContext db)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<Employee> GetEmployeeAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedEmployee is not null)
        {
            return _cachedEmployee;
        }

        var objectId = GetObjectId();

        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.EntraObjectId == objectId, cancellationToken)
            ?? throw new UnprovisionedUserException(objectId);

        if (!employee.IsActive)
        {
            throw new UnprovisionedUserException(objectId);
        }

        _cachedEmployee = employee;
        return employee;
    }

    public IReadOnlySet<string> GetRoles()
    {
        var principal = _httpContextAccessor.HttpContext?.User
            ?? throw new InvalidOperationException("No authenticated principal is available on the current request.");

        return principal.FindAll("roles")
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<ActingUser> GetActingUserAsync(CancellationToken cancellationToken = default)
    {
        var employee = await GetEmployeeAsync(cancellationToken);
        return new ActingUser(employee.Id, GetRoles());
    }

    private string GetObjectId()
    {
        var principal = _httpContextAccessor.HttpContext?.User
            ?? throw new InvalidOperationException("No authenticated principal is available on the current request.");

        foreach (var claimType in ObjectIdClaimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw new InvalidOperationException("The authenticated token carries no object id ('oid') claim.");
    }
}

/// <summary>An Entra ID principal authenticated successfully but has no
/// matching (or an inactive) Employee record. Distinct from "not
/// authenticated" -- the token is valid, provisioning is not.</summary>
public sealed class UnprovisionedUserException : Exception
{
    public UnprovisionedUserException(string entraObjectId)
        : base($"No active Employee record is linked to Entra object id '{entraObjectId}'.")
    {
        EntraObjectId = entraObjectId;
    }

    public string EntraObjectId { get; }
}
