using Desicon.Workflow.Infrastructure.Workflow;

namespace Desicon.Workflow.Api.Endpoints;

/// <summary>
/// Read-only reflection of the published (data, not code) workflow
/// definitions -- what modules exist, and the states/transitions/captures a
/// frontend needs to render forms and action buttons without hardcoding the
/// workflow shape.
/// </summary>
public static class ModuleEndpoints
{
    public static void MapModuleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/modules").RequireAuthorization();

        group.MapGet("/", ListModulesAsync);
        group.MapGet("/{key}/form-schema", GetFormSchemaAsync);
    }

    private static async Task<IResult> ListModulesAsync(
        IWorkflowDefinitionProvider definitions, CancellationToken cancellationToken)
    {
        var all = await definitions.GetAllAsync(cancellationToken);

        return Results.Ok(all.Select(d => new
        {
            d.ModuleKey,
            d.DisplayName,
            d.FormCode,
            d.FormRevision,
            d.Version
        }));
    }

    private static async Task<IResult> GetFormSchemaAsync(
        string key, IWorkflowDefinitionProvider definitions, CancellationToken cancellationToken)
    {
        // An unknown key surfaces as InvalidOperationException, which
        // GlobalExceptionHandler already maps to 404 problem+json -- no need
        // to duplicate that here.
        var definition = await definitions.GetAsync(key, cancellationToken);

        return Results.Ok(new
        {
            definition.ModuleKey,
            definition.DisplayName,
            definition.FormCode,
            definition.FormRevision,
            definition.Version,
            definition.PolicyValues,
            States = definition.States.Select(s => new { s.Key, s.Label, Type = s.Type.ToString(), s.Sla }),
            Transitions = definition.Transitions.Select(t => new
            {
                t.From,
                t.To,
                t.Action,
                Actor = new { t.Actor.Role, t.Actor.Resolver, t.Actor.Arg },
                t.Guard,
                t.GuardMessage,
                t.RequiresComment,
                t.Captures,
                t.Records,
                t.Effect,
                t.Note
            })
        });
    }
}
