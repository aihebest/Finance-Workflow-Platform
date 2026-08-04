using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Core.Guards;
using Desicon.Workflow.Core.Scheduling;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Desicon.Workflow.Infrastructure.DependencyInjection;

/// <summary>
/// Registrations shared by every host that runs workflow logic — currently
/// the API and the Functions app.
///
/// This exists because the two had begun to diverge. Both need the same
/// database context, working calendar, clock, definition provider, actor
/// resolver and engine, and both were registering them independently. The
/// holiday seeding in particular was duplicated: two copies of the same
/// twelve lines, either of which could be changed without the other, in a
/// system where a wrong holiday set silently moves SLA deadlines and
/// retirement due dates.
///
/// Host-specific concerns stay in the host. The API keeps authentication,
/// rate limiting, HTTP context accessors and the inbox index; the Functions
/// app keeps nothing beyond this, which is the point.
/// </summary>
public static class WorkflowPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the workflow platform services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">
    /// SQL connection string. Must carry "Column Encryption Setting=Enabled"
    /// in any environment where Beneficiary.BankAccountNumber is encrypted,
    /// and the caller must have registered a column encryption key store
    /// provider before the first connection opens.
    /// </param>
    /// <param name="definitionsPath">
    /// Directory holding the *.workflow.json module definitions.
    /// </param>
    public static IServiceCollection AddWorkflowPlatform(
        this IServiceCollection services,
        string connectionString,
        string definitionsPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionsPath);

        services.AddDbContext<WorkflowDbContext>(options => options.UseSqlServer(connectionString));

        services.AddSingleton<GuardEvaluator>();

        // Holidays are a maintained table, not an algorithm — see
        // WorkingCalendarOptions.Holidays. Seeded for the current and next
        // calendar year so work starting near year-end still resolves a
        // deadline that crosses into January.
        //
        // An empty or stale set does not fail loudly. It silently moves every
        // SLA deadline and every retirement due date, and the first people to
        // notice are the ones being wrongly chased. This needs an owner and an
        // annual update, which is a process commitment, not a code one.
        services.AddSingleton<IWorkingCalendar>(_ =>
        {
            var thisYear = DateTime.UtcNow.Year;
            var holidays = NigerianHolidays.FixedDatesFor(thisYear)
                .Concat(NigerianHolidays.FixedDatesFor(thisYear + 1))
                .ToHashSet();

            return new WorkingCalendar(new WorkingCalendarOptions { Holidays = holidays });
        });

        services.AddSingleton<IWorkflowClock, WorkflowClock>();

        // Immutable for the process lifetime, so read once rather than per
        // request — see WorkflowDefinitionValidator's own treatment of them.
        services.AddSingleton<IWorkflowDefinitionProvider>(
            _ => new JsonWorkflowDefinitionProvider(definitionsPath));

        services.AddScoped<IActorResolver, EmployeeActorResolver>();
        services.AddScoped<WorkflowEngine>();
        services.AddScoped<IRequestNumberGenerator, SqlSequenceRequestNumberGenerator>();

        return services;
    }
}
