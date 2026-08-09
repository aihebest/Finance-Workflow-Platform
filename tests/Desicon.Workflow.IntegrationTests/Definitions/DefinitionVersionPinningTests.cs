using Desicon.Workflow.Infrastructure.Workflow;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Definitions;

/// <summary>
/// Publishing a new definition version must not disturb requests already in
/// flight under an older one.
///
/// Before this, definitions resolved by module key alone. Every request —
/// including ones raised weeks earlier — was evaluated against whatever was
/// deployed now, so a request sitting in a state the new version removed had
/// no transitions out of it: an empty list, no error anywhere, and it vanished
/// from the worklist of the only people who could act on it while staying
/// open.
///
/// It happened twice in dev on 8 August 2026, from two unrelated causes, and
/// would happen again on every future definition change. These tests are the
/// reason it should not.
///
/// Runs against a temp directory, not a database, so it needs no
/// Testcontainers fixture -- same as WorkflowDefinitionGuardFieldTests
/// alongside it.
/// </summary>
public sealed class DefinitionVersionPinningTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wf-defs-" + Guid.NewGuid().ToString("N")[..8]);

    public DefinitionVersionPinningTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// Writes one definition file. <c>extraState</c> is present in v3 only, so
    /// the two versions differ by more than a number — a test comparing version
    /// numbers alone would pass even if the wrong definition came back.
    /// </summary>
    private void WriteDefinition(string moduleKey, int version, string effectiveFrom, string? extraState = null)
    {
        var states = extraState is null
            ? """{ "key": "DRAFT", "type": "initial", "label": "Draft" }, { "key": "CLOSED", "type": "terminal", "label": "Closed" }"""
            : $$"""{ "key": "DRAFT", "type": "initial", "label": "Draft" }, { "key": "{{extraState}}", "label": "Extra" }, { "key": "CLOSED", "type": "terminal", "label": "Closed" }""";

        var transitions = extraState is null
            ? """{ "from": "DRAFT", "to": "CLOSED", "action": "SUBMIT", "actor": { "resolver": "Requester" } }"""
            : $$"""{ "from": "DRAFT", "to": "{{extraState}}", "action": "SUBMIT", "actor": { "resolver": "Requester" } }, { "from": "{{extraState}}", "to": "CLOSED", "action": "APPROVE", "actor": { "resolver": "Requester" } }""";

        File.WriteAllText(
            Path.Combine(_directory, $"{moduleKey.ToLowerInvariant()}.v{version}.workflow.json"),
            $$"""
            {
              "moduleKey": "{{moduleKey}}",
              "displayName": "{{moduleKey}} v{{version}}",
              "formCode": "TEST-{{version}}",
              "formRevision": "0{{version}}",
              "version": {{version}},
              "effectiveFrom": "{{effectiveFrom}}",
              "numberFormat": "T-{seq:000}",
              "workingCalendar": "NG_STANDARD",
              "states": [ {{states}} ],
              "transitions": [ {{transitions}} ]
            }
            """);
    }

    [Fact]
    public async Task A_request_pinned_to_an_older_version_still_resolves_after_a_newer_one_ships()
    {
        WriteDefinition("EXPENSE", 2, "2026-08-08T00:00:00Z");
        WriteDefinition("EXPENSE", 3, "2026-08-20T00:00:00Z", extraState: "EXTRA_APPROVAL");

        using var provider = new JsonWorkflowDefinitionProvider(_directory);

        var pinned = await provider.GetAsync("EXPENSE", 2);

        pinned.Version.Should().Be(2);
        pinned.FindState("EXTRA_APPROVAL").Should()
            .BeNull("a request raised under v2 must not acquire a state v3 introduced");
        pinned.TransitionsFrom("DRAFT").Should().ContainSingle()
            .Which.To.Should().Be("CLOSED");
    }

    [Fact]
    public async Task A_new_request_gets_the_highest_version_already_in_force()
    {
        WriteDefinition("EXPENSE", 2, "2026-08-08T00:00:00Z");

        // Dated in the future: published, but not yet the process. A request
        // raised today must not be raised under it -- the same rule
        // GetPolicyValue applies to policy rows, and the reason a
        // future-dated policy value blocked all request creation once before.
        WriteDefinition("EXPENSE", 3, "2099-01-01T00:00:00Z", extraState: "EXTRA_APPROVAL");

        using var provider = new JsonWorkflowDefinitionProvider(_directory);

        var current = await provider.GetAsync("EXPENSE");

        current.Version.Should().Be(2, "version 3 is published but does not take effect until 2099");
    }

    /// <summary>
    /// The failure that matters most, because the alternative is silent.
    /// Deleting the definition file a request is pinned to must raise, not fall
    /// back to the current version — falling back is precisely the behaviour
    /// this mechanism replaces.
    /// </summary>
    [Fact]
    public async Task A_missing_pinned_version_fails_loudly_rather_than_falling_back()
    {
        WriteDefinition("EXPENSE", 3, "2026-08-20T00:00:00Z");

        using var provider = new JsonWorkflowDefinitionProvider(_directory);

        var act = async () => await provider.GetAsync("EXPENSE", 2);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("version 2").And
            .Contain("3", "the message should say what IS published, so the fix is obvious");
    }

    /// <summary>
    /// Two files claiming the same module and version used to overwrite each
    /// other in directory-enumeration order: no error, and which one won
    /// depended on the filesystem.
    /// </summary>
    [Fact]
    public async Task Two_files_declaring_the_same_module_and_version_are_refused()
    {
        WriteDefinition("EXPENSE", 2, "2026-08-08T00:00:00Z");

        File.WriteAllText(
            Path.Combine(_directory, "expense.duplicate.workflow.json"),
            """
            {
              "moduleKey": "EXPENSE",
              "displayName": "A second EXPENSE v2",
              "formCode": "TEST-2",
              "formRevision": "02",
              "version": 2,
              "effectiveFrom": "2026-08-08T00:00:00Z",
              "numberFormat": "T-{seq:000}",
              "workingCalendar": "NG_STANDARD",
              "states": [ { "key": "DRAFT", "type": "initial", "label": "Draft" } ],
              "transitions": []
            }
            """);

        using var provider = new JsonWorkflowDefinitionProvider(_directory);

        var act = async () => await provider.GetAsync("EXPENSE", 2);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("version 2");
    }

    /// <summary>
    /// GetAllAsync feeds InboxStateIndex, which must cover every version: a
    /// request pinned to v2 can sit in a state only v2 declares, and it still
    /// has to reach somebody's worklist. Indexing current versions only would
    /// hide exactly the requests most likely to be forgotten.
    /// </summary>
    [Fact]
    public async Task All_versions_are_returned_for_indexing()
    {
        WriteDefinition("EXPENSE", 2, "2026-08-08T00:00:00Z");
        WriteDefinition("EXPENSE", 3, "2026-08-20T00:00:00Z", extraState: "EXTRA_APPROVAL");

        using var provider = new JsonWorkflowDefinitionProvider(_directory);

        var all = await provider.GetAllAsync();

        all.Should().HaveCount(2);
        all.Select(d => d.Version).OrderBy(v => v).Should().Equal(2, 3);
    }
}
