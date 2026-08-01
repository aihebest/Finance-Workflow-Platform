using System.Globalization;
using System.Text.RegularExpressions;
using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Infrastructure.Workflow;

/// <summary>
/// One native SQL Server SEQUENCE per (module, year), created lazily on first
/// use rather than pre-declared in a migration -- years are unbounded, so
/// there is no finite set of sequences a migration could create up front.
/// docs/03-Data-Model-ERD.md is explicit that RequestNumber must come from a
/// database sequence, not an application counter, precisely so a failed
/// insert never leaves a reusable gap.
///
/// The sequence name is built from WorkflowDefinition.ModuleKey and a year,
/// both restricted to safe characters below (module keys are deployment
/// data, not user input, but the name still lands directly in DDL/DML text
/// because SQL Server cannot parameterise an object identifier).
/// </summary>
public sealed partial class SqlSequenceRequestNumberGenerator : IRequestNumberGenerator
{
    private const int InvalidObjectName = 208;
    private const int ObjectAlreadyExists = 2714;

    private readonly WorkflowDbContext _db;

    public SqlSequenceRequestNumberGenerator(WorkflowDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<string> GenerateAsync(
        WorkflowDefinition definition, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var year = now.Year;
        var sequenceName = BuildSequenceName(definition.ModuleKey, year);
        var seq = await NextValueAsync(sequenceName, cancellationToken);

        return FormatNumber(definition.NumberFormat, year, seq);
    }

    private async Task<long> NextValueAsync(string sequenceName, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadNextValueAsync(sequenceName, cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == InvalidObjectName)
        {
            // Sequence does not exist yet for this (module, year) pair --
            // the common case is the steady-state read succeeding, so this
            // path only runs once per module per year.
            await CreateSequenceIfMissingAsync(sequenceName, cancellationToken);
            return await ReadNextValueAsync(sequenceName, cancellationToken);
        }
    }

    private async Task<long> ReadNextValueAsync(string sequenceName, CancellationToken cancellationToken)
    {
        // SqlQuery (parameterised) cannot be used here: {sequenceName} is a
        // SQL identifier, not a value, and identifiers cannot be parameters.
        // Safe because every caller reaches this only through GenerateAsync,
        // which builds sequenceName via BuildSequenceName -- validated against
        // ValidIdentifier() first.
#pragma warning disable EF1002
        var results = await _db.Database
            .SqlQueryRaw<long>($"SELECT NEXT VALUE FOR dbo.[{sequenceName}]")
            .ToListAsync(cancellationToken);
#pragma warning restore EF1002

        return results[0];
    }

    private async Task CreateSequenceIfMissingAsync(string sequenceName, CancellationToken cancellationToken)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                $"IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = N'{sequenceName}' " +
                $"AND schema_id = SCHEMA_ID(N'dbo')) " +
                $"CREATE SEQUENCE dbo.[{sequenceName}] AS BIGINT START WITH 1 INCREMENT BY 1 NO CACHE;",
                cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == ObjectAlreadyExists)
        {
            // Lost the create race to a concurrent request for the same
            // (module, year) -- the sequence exists now regardless of who
            // won, which is all that matters.
        }
    }

    private static string BuildSequenceName(string moduleKey, int year)
    {
        if (!ValidIdentifier().IsMatch(moduleKey))
        {
            throw new InvalidOperationException(
                $"Module key '{moduleKey}' is not a valid SQL identifier fragment.");
        }

        return $"Seq_Request_{moduleKey}_{year.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatNumber(string numberFormat, int year, long seq) =>
        PlaceholderPattern().Replace(numberFormat, match =>
        {
            var format = match.Groups["fmt"].Success ? match.Groups["fmt"].Value : null;

            return match.Groups["name"].Value switch
            {
                "yyyy" => year.ToString("D4", CultureInfo.InvariantCulture),
                "yy" => (year % 100).ToString("D2", CultureInfo.InvariantCulture),
                "seq" => seq.ToString(format ?? "0", CultureInfo.InvariantCulture),
                _ => match.Value
            };
        });

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex ValidIdentifier();

    [GeneratedRegex(@"\{(?<name>yyyy|yy|seq)(:(?<fmt>[^}]+))?\}")]
    private static partial Regex PlaceholderPattern();
}
