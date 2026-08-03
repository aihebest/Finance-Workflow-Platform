using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Desicon.Workflow.Infrastructure.Persistence;

namespace Desicon.Workflow.Functions.Infrastructure;

/// <summary>
/// A mutual-exclusion lock for timer functions, held in SQL Server via
/// sp_getapplock.
///
/// WHY THIS EXISTS
/// ---------------
/// A timer trigger fires once per instance. On a plan that scales out, that
/// means every instance runs the sweep. For reminders the cost is duplicate
/// mail; for escalation it is two transfers of authority for the same
/// request, and an audit trail that names a non-actor twice. Neither is
/// recoverable by retry, because both have already happened.
///
/// WHY NOT [Singleton]
/// -------------------
/// The WebJobs Singleton attribute takes a blob lease on AzureWebJobsStorage.
/// It works, but it puts the lock in a different system from the data it
/// guards: diagnosing a stuck sweep then means correlating a storage lease
/// with database state, and the storage account has its own identity, network
/// and lifecycle concerns. The action pipeline already serialises concurrent
/// work with UPDLOCK in this same database, so an application lock here is
/// the same mechanism at a coarser grain, visible to the same queries and
/// released by the same transaction semantics.
///
/// SEMANTICS THAT MATTER
/// ---------------------
/// * Session-scoped, so the lock is released when the connection returns to
///   the pool even if the process is killed mid-sweep. A Transaction-scoped
///   lock would be tidier but requires the caller to own the transaction,
///   which a sweep that commits in batches does not.
/// * Non-blocking by default (timeout 0). A second instance that cannot take
///   the lock should skip this tick, not queue behind the first and then run
///   the same sweep immediately afterwards.
/// * Returns whether the lock was taken rather than throwing. Losing the race
///   is the expected outcome on a scaled-out plan, not an error, and logging
///   it as one trains people to ignore the log.
/// </summary>
internal sealed class SqlApplicationLock : IAsyncDisposable
{
    private readonly SqlConnection? _connection;
    private readonly string _resourceName;

    private SqlApplicationLock(SqlConnection? connection, string resourceName, bool acquired)
    {
        _connection = connection;
        _resourceName = resourceName;
        Acquired = acquired;
    }

    /// <summary>True when this process holds the lock and should do the work.</summary>
    public bool Acquired { get; }

    /// <summary>
    /// Attempts to take a named application lock. The caller must check
    /// <see cref="Acquired"/>; a false result means another instance is
    /// already running this sweep.
    /// </summary>
    public static async Task<SqlApplicationLock> TryAcquireAsync(
        WorkflowDbContext db,
        string resourceName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        // A dedicated connection, not db.Database.GetDbConnection(): a
        // session-scoped lock lives for the life of its connection, and the
        // context's connection is opened and closed around each operation by
        // EF's connection management. Borrowing it would release the lock at
        // an arbitrary point mid-sweep.
        var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "sp_getapplock";
        command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = resourceName });
        command.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.NVarChar, 32) { Value = "Exclusive" });
        command.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.NVarChar, 32) { Value = "Session" });
        command.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = (int)timeout.TotalMilliseconds });

        var returnValue = new SqlParameter { Direction = ParameterDirection.ReturnValue };
        command.Parameters.Add(returnValue);

        await command.ExecuteNonQueryAsync(cancellationToken);

        // sp_getapplock returns >= 0 on success (0 granted, 1 granted after
        // waiting) and < 0 on failure (-1 timeout, -2 cancelled, -3 deadlock
        // victim, -999 parameter error). Anything negative means someone else
        // holds it or the call was malformed; both mean do not proceed.
        var result = (int)(returnValue.Value ?? -999);
        var acquired = result >= 0;

        if (!acquired)
        {
            await connection.DisposeAsync();
            return new SqlApplicationLock(null, resourceName, false);
        }

        return new SqlApplicationLock(connection, resourceName, true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            // Explicit release rather than relying on connection close. With
            // pooling, closing returns the connection to the pool and the
            // reset that frees session locks is not guaranteed to have run
            // before another caller borrows it.
            await using var command = _connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = "sp_releaseapplock";
            command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = _resourceName });
            command.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.NVarChar, 32) { Value = "Session" });

            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
            // The lock is session-scoped, so a failure to release explicitly
            // still resolves when the connection closes below. Throwing here
            // would mask whatever the sweep itself was reporting.
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
