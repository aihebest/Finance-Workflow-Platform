using Desicon.Workflow.Core.Engine;

namespace Desicon.Workflow.Domain.Requests;

/// <summary>
/// Engine-facing request record. Everything the workflow engine needs lives
/// here; everything module-specific lives in a child table sharing this
/// primary key.
///
/// Three separate reference numbers are carried deliberately. They are not
/// aliases for one another -- RequestNumber is ours, TreasuryNumber belongs to
/// Treasury and JournalVoucherNumber to Accounts, and reconciliation against
/// their records needs all three.
/// </summary>
public class Request : IWorkflowSubject
{
    public Guid RequestId { get; protected set; } = Guid.NewGuid();

    /// <summary>System-generated, e.g. EXP-2026-000123.</summary>
    public string RequestNumber { get; set; } = string.Empty;

    public string ModuleKey { get; set; } = string.Empty;

    /// <summary>QMS form code, e.g. DEL-AC-FRM-002.</summary>
    public string FormCode { get; set; } = string.Empty;

    /// <summary>The revision the request was captured under. Stamped at
    /// submission so this request reprints in its own layout after a later
    /// revision ships -- an ISO 9001 document-control requirement, not a
    /// nicety.</summary>
    public string FormRevision { get; set; } = string.Empty;

    /// <summary>
    /// The workflow definition version this request was raised under. Stamped
    /// at creation and never changed.
    /// </summary>
    /// <remarks>
    /// The states and transitions available to a request are whatever its own
    /// version declares, for as long as it stays open, however many newer
    /// versions are published meanwhile. Publishing a change must not move the
    /// ground under work already in flight.
    ///
    /// FormRevision above is the same idea for the printed form, and the
    /// reasoning was already written down there: a request reprints in the
    /// layout it was captured under. That the process itself needed the same
    /// treatment was the part nobody had joined up -- the document was
    /// version-controlled and the workflow was not.
    /// </remarks>
    public int DefinitionVersion { get; set; }

    /// <summary>Assigned by Treasury on receipt.</summary>
    public string? TreasuryNumber { get; set; }

    /// <summary>Assigned by Accounts at GL posting.</summary>
    /// <remarks>
    /// Retained but no longer captured by either module. GL posting moved to
    /// Business Central in definition version 2, so new requests carry
    /// <see cref="BcDocumentNumber"/> instead. Kept because requests raised
    /// under version 1 have one, and dropping the column would erase the only
    /// reference by which those reconcile.
    /// </remarks>
    public string? JournalVoucherNumber { get; set; }

    /// <summary>
    /// The Business Central document number the Accounts Officer posted this
    /// under.
    /// </summary>
    /// <remarks>
    /// The single key joining this approval trail to the ledger entry. This
    /// platform records that a posting happened and where to find it; BC holds
    /// the journal itself. Without this number, reconciling the two systems
    /// means matching on amounts and dates, which stops working the first time
    /// two people claim the same sum on the same day.
    ///
    /// Required by the MARK_POSTED guard rather than optional, for that
    /// reason: the records where someone was in a hurry are exactly the ones
    /// later needing to be traced.
    /// </remarks>
    public string? BcDocumentNumber { get; set; }

    public string CurrentState { get; set; } = string.Empty;

    public Guid? CurrentActorId { get; set; }

    public DateTimeOffset StateEnteredAt { get; set; }

    public DateTimeOffset? SlaDueAt { get; set; }

    public int EscalationCount { get; set; }

    public int ReminderCount { get; set; }

    /// <summary>Incremented when a returned request is resubmitted, so an
    /// approver reviewing revision 3 can still see what revision 1 said.</summary>
    public int RevisionNumber { get; set; } = 1;

    public Guid RequesterId { get; set; }

    public int DepartmentId { get; set; }

    public decimal TotalAmountNgn { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Optimistic concurrency. Two department heads acting on the same
    /// escalated item in the same second is not hypothetical; the second must
    /// get a clean conflict, not a silent overwrite.</summary>
    public byte[]? RowVersion { get; set; }

    public bool IsClosed => ClosedAt.HasValue;

    /// <summary>
    /// The single declaration of which fields this class exposes to the
    /// guard evaluator. FieldNames and TryGetField both read this same
    /// dictionary -- one list, one dispatch table -- so they cannot drift
    /// apart the way a parallel HashSet and switch statement could.
    /// </summary>
    private static readonly Dictionary<string, Func<Request, object?>> Accessors =
        new(StringComparer.Ordinal)
        {
            [nameof(RequestId)] = r => r.RequestId,
            [nameof(RequestNumber)] = r => r.RequestNumber,
            [nameof(ModuleKey)] = r => r.ModuleKey,
            [nameof(TreasuryNumber)] = r => r.TreasuryNumber,
            [nameof(JournalVoucherNumber)] = r => r.JournalVoucherNumber,
            [nameof(BcDocumentNumber)] = r => r.BcDocumentNumber,
            [nameof(CurrentState)] = r => r.CurrentState,
            [nameof(RequesterId)] = r => r.RequesterId,
            [nameof(DepartmentId)] = r => r.DepartmentId,
            [nameof(CurrentActorId)] = r => r.CurrentActorId,
            [nameof(TotalAmountNgn)] = r => r.TotalAmountNgn,
            [nameof(RevisionNumber)] = r => r.RevisionNumber,
            [nameof(EscalationCount)] = r => r.EscalationCount,
            [nameof(SubmittedAt)] = r => r.SubmittedAt,
            [nameof(ActorId)] = r => r.ActorId,
        };

    /// <summary>The field names this allowlist is the entire surface a
    /// workflow definition can see -- there is no reflection fallback, so a
    /// definition cannot reach a field the aggregate has not published.</summary>
    public static IReadOnlySet<string> FieldNames { get; } =
        Accessors.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Exposes fields to the guard evaluator. Module subclasses override to
    /// add their own via the same accessor-dictionary pattern, calling base
    /// last.
    /// </summary>
    public virtual bool TryGetField(string name, out object? value)
    {
        if (Accessors.TryGetValue(name, out var accessor))
        {
            value = accessor(this);
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Set by the action pipeline immediately before guard evaluation, so a
    /// definition can express maker-checker rules such as
    /// "ActorId != PostedByUserId".
    /// </summary>
    public Guid? ActorId { get; set; }
}
