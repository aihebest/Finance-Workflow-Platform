using Desicon.Workflow.Core.Guards;
using Desicon.Workflow.Core.Validation;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Workflow;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Definitions;

/// <summary>
/// Standing regression test: every *.workflow.json under modules/ must stay
/// structurally valid, and every guard's field references must resolve
/// against the module's real field schema. Runs against a directory, not a
/// DB, so it needs no Testcontainers fixture and stays fast.
/// </summary>
public sealed class WorkflowDefinitionGuardFieldTests
{
    private static readonly string ModulesDirectory = ResolveModulesDirectory();

    [Fact]
    public async Task Every_definition_passes_structural_validation_with_no_errors()
    {
        var provider = new JsonWorkflowDefinitionProvider(ModulesDirectory);
        var definitions = await provider.GetAllAsync();
        var schema = new RequestGuardFieldSchema();

        definitions.Should().NotBeEmpty();

        foreach (var definition in definitions)
        {
            var result = WorkflowDefinitionValidator.Validate(definition, schema);
            var errors = result.Findings.Where(f => f.Severity == "Error").ToList();

            errors.Should().BeEmpty(
                "module '{0}' should have no validation errors, found: {1}",
                definition.ModuleKey,
                string.Join("; ", errors.Select(f => $"{f.Code}: {f.Message}")));
        }
    }

    [Fact]
    public async Task Every_guard_expression_parses_without_a_syntax_error()
    {
        var provider = new JsonWorkflowDefinitionProvider(ModulesDirectory);
        var definitions = await provider.GetAllAsync();

        foreach (var definition in definitions)
        {
            foreach (var transition in definition.Transitions.Where(t => t.Guard is not null))
            {
                var act = () => GuardParser.Parse(transition.Guard!);

                act.Should().NotThrow(
                    "{0} transition '{1}' from '{2}' guard should be syntactically valid",
                    definition.ModuleKey, transition.Action, transition.From);
            }
        }
    }

    [Fact]
    public async Task Every_guard_field_reference_resolves_against_its_modules_field_schema()
    {
        var provider = new JsonWorkflowDefinitionProvider(ModulesDirectory);
        var definitions = await provider.GetAllAsync();
        var schema = new RequestGuardFieldSchema();
        var registeredModuleKeys = RequestGuardFieldSchema.ModuleKeys.ToHashSet(StringComparer.Ordinal);

        var unresolved = new List<string>();

        foreach (var definition in definitions)
        {
            // Modules formally registered with RequestGuardFieldSchema
            // (today: EXPENSE, CASH_ADVANCE) are checked against their own
            // module+base field union. Anything not registered there -- as
            // of this writing, only LEAVE_REQUEST, see the genericity
            // finding recorded in LeaveRequestWorkflowTests -- has no
            // subclass to add fields of its own, so base Request fields are
            // the correct (and only safe) set to check it against. This
            // keeps the assertion meaningful for every definition file, not
            // only the two the production validator has a registered
            // schema for today.
            var knownFields = registeredModuleKeys.Contains(definition.ModuleKey)
                ? schema.GetFieldNames(definition.ModuleKey)
                : Request.FieldNames;

            foreach (var transition in definition.Transitions.Where(t => t.Guard is not null))
            {
                var node = GuardParser.Parse(transition.Guard!);

                foreach (var fieldName in CollectFieldNames(node).Distinct(StringComparer.Ordinal))
                {
                    if (!knownFields.Contains(fieldName))
                    {
                        unresolved.Add(
                            $"{definition.ModuleKey}: transition '{transition.Action}' from " +
                            $"'{transition.From}' references unknown field '{fieldName}'.");
                    }
                }
            }
        }

        unresolved.Should().BeEmpty();
    }

    private static IEnumerable<string> CollectFieldNames(GuardNode node)
    {
        switch (node)
        {
            case FieldNode field:
                yield return field.Name;
                break;

            case UnaryNode unary:
                foreach (var name in CollectFieldNames(unary.Operand))
                {
                    yield return name;
                }

                break;

            case BinaryNode binary:
                foreach (var name in CollectFieldNames(binary.Left))
                {
                    yield return name;
                }

                foreach (var name in CollectFieldNames(binary.Right))
                {
                    yield return name;
                }

                break;

            case FunctionNode function:
                foreach (var argument in function.Arguments)
                {
                    foreach (var name in CollectFieldNames(argument))
                    {
                        yield return name;
                    }
                }

                break;
        }
    }

    private static string ResolveModulesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "modules")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                "Could not locate the repository's 'modules' directory from the test output path.");
        }

        return Path.Combine(directory.FullName, "modules");
    }
}
