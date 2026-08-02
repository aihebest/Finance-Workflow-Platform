using System.Threading.RateLimiting;
using Azure.Identity;
using Desicon.Workflow.Api.Endpoints;
using Desicon.Workflow.Api.Http;
using Desicon.Workflow.Api.Security;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Core.Guards;
using Desicon.Workflow.Core.Scheduling;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Security;
using Desicon.Workflow.Infrastructure.Workflow;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.AlwaysEncrypted.AzureKeyVaultProvider;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("WorkflowDb")
    ?? throw new InvalidOperationException("Connection string 'WorkflowDb' is not configured.");

// Beneficiary.BankAccountNumber is Always Encrypted (see
// Migrations/*_ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber.cs and
// docs/04-Security-and-DevSecOps.md) with a Key-Vault-backed column master
// key, so the driver needs this provider registered -- once, process-wide,
// before any SqlConnection opens -- to unwrap the column encryption key.
// DefaultAzureCredential resolves to Managed Identity in Azure and falls
// back to az-cli/VS credentials for local dev against a dev Key Vault.
SqlConnection.RegisterColumnEncryptionKeyStoreProviders(new Dictionary<string, SqlColumnEncryptionKeyStoreProvider>
{
    [SqlColumnEncryptionAzureKeyVaultProvider.ProviderName] =
        new SqlColumnEncryptionAzureKeyVaultProvider(new DefaultAzureCredential())
});

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
builder.Services.AddSingleton<IBankDetailsAuditor, BankDetailsAuditor>();

var definitionsPath = Path.GetFullPath(
    Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Workflow:DefinitionsPath"]
        ?? throw new InvalidOperationException("Configuration 'Workflow:DefinitionsPath' is not set.")));
builder.Services.AddSingleton<IWorkflowDefinitionProvider>(_ => new JsonWorkflowDefinitionProvider(definitionsPath));

// The definitions are immutable for the process lifetime (see
// WorkflowDefinitionValidator's own treatment of them), so the inbox index
// derived from them is built once, synchronously, at startup rather than
// per-request.
builder.Services.AddSingleton(sp =>
{
    var definitions = sp.GetRequiredService<IWorkflowDefinitionProvider>();
    var all = definitions.GetAllAsync().GetAwaiter().GetResult();
    return new InboxStateIndex(all);
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
builder.Services.AddScoped<ReadAccessScope>();

// Entra ID (Azure AD) v2.0 tokens, validated against the tenant's own JWKS
// via OIDC discovery -- Authority alone is enough for the handler to fetch
// and cache signing keys, no manual JWKS wiring needed. MapInboundClaims is
// turned off so "oid" and "roles" keep their short names instead of being
// rewritten onto long http://schemas... claim-mapping URIs.
var azureAd = builder.Configuration.GetSection("AzureAd");
var tenantId = azureAd["TenantId"] ?? throw new InvalidOperationException("Configuration 'AzureAd:TenantId' is not set.");
var instance = azureAd["Instance"] ?? throw new InvalidOperationException("Configuration 'AzureAd:Instance' is not set.");
var audience = azureAd["Audience"] ?? throw new InvalidOperationException("Configuration 'AzureAd:Audience' is not set.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"{instance.TrimEnd('/')}/{tenantId}/v2.0";
        options.Audience = audience;
        options.MapInboundClaims = false;
    });

builder.Services.AddAuthorization();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Doc 04's documented threshold: 100 requests/minute per authenticated user
// (falling back to remote IP for anything that reaches the limiter
// unauthenticated), 429 with Retry-After on rejection. Per-endpoint 10/min
// limiters for auth/upload endpoints are not wired here: authentication is
// entirely Entra-hosted (no local auth endpoint exists to limit) and no
// upload endpoint exists in this API surface (attachments are out of scope
// per doc 05 -- see final summary).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var key = context.User.FindFirst("oid")?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";

        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: "Rate limit exceeded. Retry after the window indicated by the Retry-After header.",
            instance: context.HttpContext.Request.Path,
            type: "https://desicon.internal/problems/rate-limited"
        ).ExecuteAsync(context.HttpContext);
    };
});

builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Must run after UseAuthentication: the GlobalLimiter partition key reads
// context.User.FindFirst("oid") to bucket by authenticated user, falling
// back to remote IP only for anonymous callers. Before this middleware
// ran ahead of authentication, context.User was always the unauthenticated
// default principal at partition-key time, so every request -- regardless
// of caller -- fell back to the IP/"unknown" bucket and shared one global
// rate-limit budget instead of one per user.
app.UseRateLimiter();

app.MapAdvanceRetirementEndpoints();
app.MapModuleEndpoints();
app.MapRequestEndpoints();
app.MapExpenseEndpoints();
app.MapCashAdvanceEndpoints();
app.MapBeneficiaryEndpoints();

app.Run();

// Exposes the top-level-statement-generated Program class to
// WebApplicationFactory<Program> in Desicon.Workflow.IntegrationTests --
// the SDK's auto-generated partial is internal, which a separate test
// assembly cannot see.
public partial class Program
{
}
