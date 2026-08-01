using Desicon.Workflow.Api.Endpoints;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Core.Guards;
using Desicon.Workflow.Core.Scheduling;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Security;
using Desicon.Workflow.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("WorkflowDb")
    ?? throw new InvalidOperationException("Connection string 'WorkflowDb' is not configured.");

builder.Services.AddDbContext<WorkflowDbContext>(options => options.UseSqlServer(connectionString));

builder.Services.AddSingleton<GuardEvaluator>();

// Holidays are a maintained table, not an algorithm -- see
// WorkingCalendarOptions.Holidays remarks. Seeded for the current and next
// calendar year so a SUBMIT near year-end still resolves an SLA that crosses
// into January.
builder.Services.AddSingleton<IWorkingCalendar>(_ =>
{
    var thisYear = DateTime.UtcNow.Year;
    var holidays = NigerianHolidays.FixedDatesFor(thisYear)
        .Concat(NigerianHolidays.FixedDatesFor(thisYear + 1))
        .ToHashSet();

    var options = new WorkingCalendarOptions { Holidays = holidays };
    return new WorkingCalendar(options);
});
builder.Services.AddSingleton<IWorkflowClock, WorkflowClock>();

builder.Services.AddScoped<IActorResolver, EmployeeActorResolver>();
builder.Services.AddScoped<WorkflowEngine>();
builder.Services.AddScoped<IRequestNumberGenerator, SqlSequenceRequestNumberGenerator>();
builder.Services.AddScoped<AdvanceRetirementHandler>();
builder.Services.AddScoped<RequestActionService>();

builder.Services.AddSingleton<ISecurityEventWriter, SecurityEventWriter>();

var definitionsPath = Path.GetFullPath(
    Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Workflow:DefinitionsPath"]
        ?? throw new InvalidOperationException("Configuration 'Workflow:DefinitionsPath' is not set.")));
builder.Services.AddSingleton<IWorkflowDefinitionProvider>(_ => new JsonWorkflowDefinitionProvider(definitionsPath));

builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

app.MapAdvanceRetirementEndpoints();

app.Run();
