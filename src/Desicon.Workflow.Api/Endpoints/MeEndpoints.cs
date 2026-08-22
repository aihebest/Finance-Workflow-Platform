using Desicon.Workflow.Api.Security;
using Microsoft.AspNetCore.Mvc;

namespace Desicon.Workflow.Api.Endpoints;

/// <summary>
/// Who the API thinks the caller is.
/// </summary>
/// <remarks>
/// Added 22 Aug 2026 because the browser and the API disagreed about the same
/// person. The SPA decided whether to draw the Reports tab by reading
/// `roles` off the MSAL account's idTokenClaims; the API reads it off the
/// access token it is actually sent. Those are two different tokens, populated
/// at different moments, and for the Cost Control desk the first one had no
/// roles in it at all -- so the tab never appeared for somebody the API would
/// have let straight in.
///
/// The fix is not to guess harder at the token shape. It is to stop having two
/// sources: the API already resolves identity and roles for every request it
/// serves, so it can simply say so. The browser now asks rather than infers,
/// and the two can no longer drift apart.
///
/// EMPLOYEE IS NULLABLE, DELIBERATELY
/// ----------------------------------
/// GetEmployeeAsync throws when the token's object id matches no Employee row
/// -- correct for an endpoint that is about to act on a request, and wrong
/// here. That gap is the single most repeated failure on this project: four
/// people so far have held a role claim with no staff record, received their
/// notifications, and been unable to open one.
///
/// This endpoint reports it instead of failing on it, so the browser can say
/// what is actually wrong rather than showing an empty screen.
/// </remarks>
public static class MeEndpoints
{
    public static void MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/me", GetAsync).RequireAuthorization();
    }

    private static async Task<IResult> GetAsync(
        [FromServices] ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var roles = currentUser.GetRoles().OrderBy(r => r, StringComparer.Ordinal).ToList();

        try
        {
            var employee = await currentUser.GetEmployeeAsync(cancellationToken);

            return Results.Ok(new
            {
                Roles = roles,
                HasEmployeeRecord = true,
                Employee = new
                {
                    employee.Id,
                    employee.StaffNumber,
                    employee.FullName,
                    employee.Email,
                    employee.DepartmentId
                }
            });
        }
        catch (InvalidOperationException)
        {
            // Authenticated, possibly holding several roles, and unknown to the
            // org chart. Reported rather than thrown: the caller needs to be
            // told which half is missing, and a 500 tells them neither.
            return Results.Ok(new
            {
                Roles = roles,
                HasEmployeeRecord = false,
                Employee = (object?)null
            });
        }
    }
}
