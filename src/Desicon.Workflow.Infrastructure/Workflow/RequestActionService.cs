using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Audit;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.Notifications;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Domain.Security;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Desicon.Workflow.Infrastructure.Workflow;

/// <summary>
/// The single place a workflow transition is carried out end to end: engine
/// evaluation, state mutation, SLA recomputation, the sealed audit event that
/// makes it provable, and the outbox row that will eventually notify someone.
/// All of it lands in one database transaction, or none of it does.
///
/// Two layers sit on top of the engine's own authorisation and guards.
/// First, GlPostingLines is loaded explicitly by RequestId immediately after
/// the request (see the comment at that call site) so the POSTING guard
/// evaluates against real data. Second, after the engine has authorised and
/// guard-checked a transition, EvaluatePolicyViolation re-checks it against
/// two hardcoded rules -- no self-approval, no maker-checker collision --
/// independent of whatever the JSON definition's own guards say. Either
/// layer failing rolls the transaction back and writes a SecurityEvent
/// through a connection that survives that rollback (see
/// ISecurityEventWriter).
///
/// Some effects (see ApplyEffectAsync's "RetireLinkedAdvance" case) need to
/// execute a second, independent transition -- against a different request --
/// in the same transaction as the one already in flight. ExecuteAsync and
/// ExecuteCascadedAsync are both thin wrappers around the shared
/// RunTransitionAsync core: only the outermost caller (ExecuteAsync) owns
/// beginning, committing and rolling back the transaction; a cascaded call
/// reuses whatever transaction is already open and lets a failure propagate
/// as an exception, so the ordinary "await using" rollback-on-dispose already
/// relied upon elsewhere in this file unwinds the whole thing.
/// </summary>
public sealed class RequestActionService
{
    /// <summary>
    /// Resolvers under which the actor is *expected* to be the requester
    /// themselves (e.g. SUBMIT, RESUBMIT, and the beneficiary's ACKNOWLEDGE
    /// step). The self-approval check below only applies outside this set --
    /// otherwise it would block legitimate self-service actions.
    /// </summary>
    private static readonly HashSet<string> SelfServiceResolvers =
        new(StringComparer.Ordinal) { "Requester", "Beneficiary" };

    private readonly WorkflowDbContext _db;
    private readonly WorkflowEngine _engine;
    private readonly IWorkflowDefinitionProvider _definitions;
    private readonly IWorkflowClock _clock;
    private readonly ISecurityEventWriter _securityEvents;
    private readonly AdvanceRetirementHandler _advanceRetirement;
    private readonly IActorResolver _actorResolver;

    public RequestActionService(
        WorkflowDbContext db,
        WorkflowEngine engine,
        IWorkflowDefinitionProvider definitions,
        IWorkflowClock clock,
        ISecurityEventWriter securityEvents,
        AdvanceRetirementHandler advanceRetirement,
        IActorResolver actorResolver)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _securityEvents = securityEvents ?? throw new ArgumentNullException(nameof(securityEvents));
        _advanceRetirement = advanceRetirement ?? throw new ArgumentNullException(nameof(advanceRetirement));
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
    }

    public async Task<RequestActionResult> ExecuteAsync(
        Guid requestId,
        ActingUser actingUser,
        TransitionRequest transitionRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actingUser);
        ArgumentNullException.ThrowIfNull(transitionRequest);

        var idempotencyKey = string.IsNullOrWhiteSpace(transitionRequest.IdempotencyKey)
            ? null
            : transitionRequest.IdempotencyKey;

        if (idempotencyKey is not null)
        {
            var replay = await FindReplayAsync(requestId, idempotencyKey, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        return await RunTransitionAsync(
            requestId, actingUser, transitionRequest, idempotencyKey, transaction, cancellationToken);
    }

    /// <summary>
    /// Runs a second transition inside a transaction the outermost
    /// ExecuteAsync call already opened -- see ApplyEffectAsync's
    /// "RetireLinkedAdvance" case, the only current caller. Never begins,
    /// commits or rolls back a transaction itself: whatever outcome it
    /// returns, including failure, is reported to the caller rather than
    /// unwound here, because this DbContext's ambient transaction belongs to
    /// the outermost call.
    /// </summary>
    internal Task<RequestActionResult> ExecuteCascadedAsync(
        Guid requestId,
        ActingUser actingUser,
        TransitionRequest transitionRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actingUser);
        ArgumentNullException.ThrowIfNull(transitionRequest);

        return RunTransitionAsync(
            requestId, actingUser, transitionRequest, idempotencyKey: null, transaction: null, cancellationToken);
    }

    private async Task<RequestActionResult> RunTransitionAsync(
        Guid requestId,
        ActingUser actingUser,
        TransitionRequest transitionRequest,
        string? idempotencyKey,
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var isCascaded = transaction is null;

        var request = await _db.Requests
            .Include(r => ((ExpenseRequest)r).Lines)
            .Include(r => ((CashAdvanceRequest)r).Lines)
            .FirstOrDefaultAsync(r => r.RequestId == requestId, cancellationToken)
            ?? throw new InvalidOperationException($"Request '{requestId}' does not exist.");

        // GlPostingLine keys to the shared base Request row, not to either
        // sibling table (see ExpenseRequestConfiguration) -- under TPT a
        // single FK column cannot back two sibling-type navigations, so this
        // can never be a mapped Include(). Loaded explicitly by RequestId so
        // the POSTING guard (GlDebitTotal / GlCreditTotal / GlLineCount)
        // sees the real rows instead of the always-empty Ignore()d list.
        if (request is ExpenseRequest or CashAdvanceRequest)
        {
            var glPostingLines = await _db.Set<GlPostingLine>()
                .AsNoTracking()
                .Where(l => l.RequestId == requestId)
                .ToListAsync(cancellationToken);

            switch (request)
            {
                case ExpenseRequest expense: expense.GlPostingLines = glPostingLines; break;
                case CashAdvanceRequest advance: advance.GlPostingLines = glPostingLines; break;
            }
        }

        // Staged for the same reason GlPostingLines is above: SUBMIT's guard
        // (BLOCK_NEW_ADVANCE_WHEN_OVERDUE) needs to know whether this
        // requester has another advance currently Overdue, and that is a
        // cross-request fact CashAdvanceRequest cannot compute on its own.
        // IX_Advance_Overdue (RetirementStatus, RetirementDueDate) covers
        // this query.
        if (request is CashAdvanceRequest thisAdvance)
        {
            thisAdvance.HasOverdueAdvance = await _db.CashAdvanceRequests
                .AsNoTracking()
                .Where(a => a.RequesterId == thisAdvance.RequesterId
                    && a.RequestId != requestId
                    && a.RetirementStatus == RetirementStatus.Overdue)
                .AnyAsync(cancellationToken);
        }

        var definition = await _definitions.GetAsync(request.ModuleKey, cancellationToken);

        var effectiveActorId = actingUser.OnBehalfOf ?? actingUser.UserId;
        request.ActorId = effectiveActorId;

        var result = await _engine.ExecuteAsync(
            definition, request, actingUser, transitionRequest, cancellationToken);

        if (!result.Succeeded)
        {
            // Nothing durable changed -- ActorId is a transient, unmapped
            // property, so there is nothing to unwind beyond the transaction.
            // A cascaded call does not own the transaction, so it leaves
            // rolling it back to whichever caller does.
            if (!isCascaded)
            {
                await transaction!.RollbackAsync(cancellationToken);
            }

            if (result.Outcome is TransitionOutcome.NotAuthorised or TransitionOutcome.GuardFailed)
            {
                await LogDenialAsync(
                    request, actingUser, transitionRequest,
                    request.CurrentState, result.Outcome, result.Message, cancellationToken);
            }

            return new RequestActionResult(
                result.Outcome, result.FromState, result.ToState, result.Message, null, null);
        }

        var transition = result.Transition!;
        var now = _clock.UtcNow;

        request.CurrentState = result.ToState!;
        request.StateEnteredAt = now;
        request.EscalationCount = 0;
        request.ReminderCount = 0;

        if (definition.FindState(result.ToState!)?.Type == StateType.Terminal)
        {
            request.ClosedAt = now;
        }

        ApplyRecordedActor(request, transition.Records, effectiveActorId);
        await ApplyEffectAsync(request, transition, transitionRequest, definition, cancellationToken);

        // Defence-in-depth, independent of the definition's own guards: the
        // engine has already authorised this transition against the JSON
        // definition, but a misconfigured (or forged) definition must not be
        // able to let a self-approval or maker-checker violation through.
        var policyViolation = EvaluatePolicyViolation(request, transition, effectiveActorId);
        if (policyViolation is not null)
        {
            if (!isCascaded)
            {
                await transaction!.RollbackAsync(cancellationToken);
                _db.ChangeTracker.Clear();
            }

            await LogDenialAsync(
                request, actingUser, transitionRequest,
                result.FromState, TransitionOutcome.PolicyViolation, policyViolation, cancellationToken);

            return new RequestActionResult(
                TransitionOutcome.PolicyViolation, result.FromState, result.ToState, policyViolation, null, null);
        }

        request.CurrentActorId = await ResolveNextActorAsync(request, definition, cancellationToken);
        request.SlaDueAt = _engine.ComputeSlaDueAt(definition, result.ToState!);

        var previousHash = await _db.AuditEvents
            .Where(e => e.RequestId == requestId)
            .OrderByDescending(e => e.AuditEventId)
            .Select(e => e.EventHash)
            .FirstOrDefaultAsync(cancellationToken);

        var auditEvent = new AuditEvent
        {
            RequestId = requestId,
            EventType = transition.Action,
            FromState = result.FromState,
            ToState = result.ToState,
            ActorId = effectiveActorId,
            ActorRole = transition.Actor.Role ?? transition.Actor.Resolver ?? "Unknown",
            OnBehalfOfUserId = actingUser.OnBehalfOf,
            Reason = transitionRequest.Comment,
            PayloadJson = SerialisePayload(transitionRequest.CapturedFields),
            OccurredAtUtc = now,
            IdempotencyKey = idempotencyKey
        };
        auditEvent.Seal(previousHash);

        _db.AuditEvents.Add(auditEvent);

        foreach (var outboxMessage in BuildOutboxMessages(definition, request, result, now))
        {
            _db.OutboxMessages.Add(outboxMessage);
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsIdempotencyKeyConflict(ex))
        {
            // A concurrent call carrying the same idempotency key committed
            // first; this call is the retry, not a second execution. Only
            // the outermost call can recover from this -- a cascaded call
            // has no idempotency key of its own (see ExecuteCascadedAsync)
            // and no transaction of its own to roll back, so it simply
            // propagates the failure to whichever caller does.
            if (isCascaded)
            {
                throw;
            }

            await transaction!.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();

            var winner = idempotencyKey is null
                ? null
                : await FindReplayAsync(requestId, idempotencyKey, cancellationToken);

            return winner ?? throw new InvalidOperationException(
                "Concurrent idempotent execution could not be resolved.", ex);
        }

        if (!isCascaded)
        {
            await transaction!.CommitAsync(cancellationToken);
        }

        return new RequestActionResult(
            TransitionOutcome.Succeeded,
            result.FromState,
            result.ToState,
            result.Message,
            auditEvent.AuditEventId,
            request.SlaDueAt);
    }

    private async Task<RequestActionResult?> FindReplayAsync(
        Guid requestId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var existing = await _db.AuditEvents
            .AsNoTracking()
            .Where(e => e.RequestId == requestId && e.IdempotencyKey == idempotencyKey)
            .OrderByDescending(e => e.AuditEventId)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            return null;
        }

        var currentSlaDueAt = await _db.Requests
            .AsNoTracking()
            .Where(r => r.RequestId == requestId)
            .Select(r => r.SlaDueAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new RequestActionResult(
            TransitionOutcome.Succeeded,
            existing.FromState,
            existing.ToState,
            "Replayed: this action already executed under the supplied idempotency key.",
            existing.AuditEventId,
            currentSlaDueAt,
            WasIdempotentReplay: true);
    }

    /// <summary>
    /// Self-approval and maker-checker, enforced in code rather than trusted
    /// to the definition's own guard expressions. Returns a human-readable
    /// reason when a check fires, otherwise null.
    /// </summary>
    private static string? EvaluatePolicyViolation(
        Request request, WorkflowTransition transition, Guid effectiveActorId)
    {
        if (!SelfServiceResolvers.Contains(transition.Actor.Resolver ?? string.Empty) &&
            effectiveActorId == request.RequesterId)
        {
            return "Self-approval blocked: the acting user is this request's own requester.";
        }

        var (postedByUserId, authorisedByUserId) = request switch
        {
            ExpenseRequest expense => (expense.PostedByUserId, expense.AuthorisedByUserId),
            CashAdvanceRequest advance => (advance.PostedByUserId, advance.AuthorisedByUserId),
            _ => ((Guid?)null, (Guid?)null)
        };

        if (postedByUserId is { } poster && authorisedByUserId is { } authoriser && poster == authoriser)
        {
            return "Maker-checker blocked: PostedByUserId and AuthorisedByUserId are the same user.";
        }

        return null;
    }

    private Task LogDenialAsync(
        Request request,
        ActingUser actingUser,
        TransitionRequest transitionRequest,
        string? fromState,
        TransitionOutcome outcome,
        string? detail,
        CancellationToken cancellationToken)
    {
        var securityEvent = new SecurityEvent
        {
            RequestId = request.RequestId,
            ModuleKey = request.ModuleKey,
            Action = transitionRequest.Action,
            FromState = fromState,
            Reason = outcome.ToString(),
            Detail = detail,
            AttemptedByUserId = actingUser.UserId,
            OnBehalfOfUserId = actingUser.OnBehalfOf,
            OccurredAtUtc = _clock.UtcNow
        };

        return _securityEvents.WriteAsync(securityEvent, cancellationToken);
    }

    /// <summary>
    /// Populates Request.CurrentActorId for the state just entered, so a fast
    /// indexed query (GET /my/inbox) can find "is this in my queue" without
    /// re-running actor resolution per request. Only resolver-based actor
    /// specs (Requester, Beneficiary, LineManagerOf, DepartmentHeadOf,
    /// CurrentActor) ever collapse to a single person in this model; a
    /// role-only spec (e.g. FinanceOfficer) cannot, and is left null here --
    /// InboxStateIndex covers that case by state membership instead. A
    /// resolver whose set spans more than one person (delegation active on a
    /// resolver step) is also left null rather than guessing which one is
    /// "current": both the delegator and the delegate remain authorised to
    /// act, and a caller can still reach them via the engine's own
    /// authorisation check when they submit an action.
    /// </summary>
    private async Task<Guid?> ResolveNextActorAsync(
        Request request, WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        var candidates = new HashSet<Guid>();

        foreach (var transition in definition.TransitionsFrom(request.CurrentState))
        {
            if (transition.Actor.Resolver is null)
            {
                return null;
            }

            var resolved = await _actorResolver.ResolveAsync(transition.Actor, request, cancellationToken);
            candidates.UnionWith(resolved);

            if (candidates.Count > 1)
            {
                return null;
            }
        }

        return candidates.Count == 1 ? candidates.Single() : null;
    }

    private static void ApplyRecordedActor(Request request, string? propertyName, Guid actorId)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return;
        }

        var property = request.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

        if (property is null || property.PropertyType != typeof(Guid?) || !property.CanWrite)
        {
            throw new InvalidOperationException(
                $"Transition names '{propertyName}' as a Records target, but " +
                $"'{request.GetType().Name}' has no writable Guid? property of that name.");
        }

        property.SetValue(request, actorId);
    }

    private async Task ApplyEffectAsync(
        Request request,
        WorkflowTransition transition,
        TransitionRequest transitionRequest,
        WorkflowDefinition definition,
        CancellationToken cancellationToken)
    {
        switch (transition.Effect)
        {
            case null:
                return;

            case "IncrementRevisionNumber":
                request.RevisionNumber++;
                return;

            case "StartRetirementClock":
            {
                // Mirrors CashAdvanceRequest.ReleaseCash, but goes through
                // IWorkflowClock (interface) rather than a raw
                // IWorkingCalendar so this service depends only on the
                // abstraction WorkflowEngine itself depends on.
                var advance = (CashAdvanceRequest)request;
                var releasedAt = ReadDateTimeOffset(transitionRequest.CapturedFields, "CashReleasedAt");

                advance.CashReleasedAt = releasedAt;
                advance.RetirementDueDate = _clock.AddWorkingHours(
                    releasedAt, advance.RetirementWindowHours, definition.WorkingCalendar);
                advance.RetirementStatus = RetirementStatus.NotDue;
                return;
            }

            case "RetireLinkedAdvance":
            {
                var expense = (ExpenseRequest)request;
                if (expense.RetiresAdvanceId is { } advanceId)
                {
                    await _advanceRetirement.RetireAsync(
                        expense.RequestId, advanceId, expense.TotalAmountNgn, ExecuteCascadedAsync, cancellationToken);
                }

                return;
            }

            default:
                throw new InvalidOperationException($"Unknown transition effect '{transition.Effect}'.");
        }
    }

    private static DateTimeOffset ReadDateTimeOffset(
        IReadOnlyDictionary<string, object?>? capturedFields, string key)
    {
        if (capturedFields is null || !capturedFields.TryGetValue(key, out var raw) || raw is null)
        {
            throw new InvalidOperationException(
                $"'{key}' is required to apply this transition's effect.");
        }

        return raw switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            string s when DateTimeOffset.TryParse(
                s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
            JsonElement je when je.TryGetDateTimeOffset(out var parsed) => parsed,
            _ => throw new InvalidOperationException($"'{key}' could not be interpreted as a date/time.")
        };
    }

    private static string SerialisePayload(IReadOnlyDictionary<string, object?>? capturedFields) =>
        capturedFields is null || capturedFields.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(capturedFields);

    private static IEnumerable<OutboxMessage> BuildOutboxMessages(
        WorkflowDefinition definition, Request request, TransitionResult result, DateTimeOffset now)
    {
        // SLA_REMINDER / SLA_BREACHED rules are driven by the (separate,
        // not-yet-built) scheduled SLA sweep, not by a transition -- they are
        // deliberately not matched here.
        foreach (var rule in definition.Notifications)
        {
            if (!string.Equals(rule.On, "STATE_ENTERED", StringComparison.Ordinal) &&
                !string.Equals(rule.On, result.ToState, StringComparison.Ordinal))
            {
                continue;
            }

            yield return new OutboxMessage
            {
                RequestId = request.RequestId,
                Template = rule.Template,
                RecipientRolesJson = JsonSerializer.Serialize(rule.To),
                PayloadJson = JsonSerializer.Serialize(new
                {
                    request.RequestId,
                    request.RequestNumber,
                    request.ModuleKey,
                    FromState = result.FromState,
                    ToState = result.ToState
                }),
                Status = OutboxMessageStatus.Pending,
                CreatedAt = now
            };
        }
    }

    private static bool IsIdempotencyKeyConflict(DbUpdateException ex) =>
        ex.InnerException is SqlException sql &&
        (sql.Number == 2601 || sql.Number == 2627) &&
        sql.Message.Contains("UQ_AuditEvent_Idempotency", StringComparison.OrdinalIgnoreCase);
}
