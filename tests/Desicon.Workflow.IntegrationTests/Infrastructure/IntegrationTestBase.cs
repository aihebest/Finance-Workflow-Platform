using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>Resets the database (via Respawn) before every test method, so
/// tests can run in any order against the one shared container/factory.</summary>
[Collection(IntegrationTestCollection.Name)]
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected IntegrationTestBase(WorkflowApiFixture fixture)
    {
        Fixture = fixture;
    }

    protected WorkflowApiFixture Fixture { get; }

    public Task InitializeAsync() => Fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    protected async Task<T> WithDbAsync<T>(Func<WorkflowDbContext, Task<T>> action)
    {
        using var scope = Fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        return await action(db);
    }

    protected async Task WithDbAsync(Func<WorkflowDbContext, Task> action)
    {
        using var scope = Fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        await action(db);
    }

    /// <summary>
    /// The human-readable number for a request id.
    /// </summary>
    /// <remarks>
    /// Lived as a private helper on ReportTests until a second test class
    /// needed it. Here rather than duplicated, because the tests that assert
    /// against what a person sees on screen all need the number rather than
    /// the GUID, and there will be more of them.
    /// </remarks>
    protected Task<string> RequestNumberOfAsync(Guid requestId) =>
        WithDbAsync(async db => await db.Requests
            .AsNoTracking()
            .Where(r => r.RequestId == requestId)
            .Select(r => r.RequestNumber)
            .SingleAsync());
}
