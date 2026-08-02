using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for the real Entra JWT bearer handler. CurrentUserAccessor only
/// cares about an "oid" claim (falling back to a long claim-mapping URI this
/// handler never emits, since MapInboundClaims is off in Program.cs too) and
/// repeated "roles" claims read via principal.FindAll("roles") -- so a
/// header-driven fake identity satisfies it without any token machinery.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string ObjectIdHeader = "X-Test-Oid";
    public const string RolesHeader = "X-Test-Roles";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ObjectIdHeader, out var objectId) || string.IsNullOrWhiteSpace(objectId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("oid", objectId!) };

        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            claims.AddRange(roles.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(role => new Claim("roles", role)));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
