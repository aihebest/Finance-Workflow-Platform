using Desicon.Workflow.Infrastructure.Persistence;
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
}
