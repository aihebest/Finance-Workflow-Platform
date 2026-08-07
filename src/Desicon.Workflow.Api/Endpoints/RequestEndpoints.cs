using System.Globalization;
using System.Text;
using System.Text.Json;
using Desicon.Workflow.Api.Http;
using Desicon.Workflow.Api.Security;
using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Security;
using Desicon.Workflow.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Api.Endpoints;

/// <summary>
/// The generic engine surface from docs/05-API-Frontend-and-Operations.md
/// §1: draft creation, the read/update/submit/action/history lifecycle, and
/// the three worklist views (inbox, my requests, my advances). Everything
/// module-specific (expense/cash-advance capture endpoints) lives in its own
/// file and calls back through the same RequestActionService, so there is
/// still exactly one path a state transition can take.
/// </summary>
public static class RequestEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> EditableStates =
        new(StringComparer.Ordinal) { "DRAFT", "RETURNED" };

    private static readonly HashSet<string> OpenAdvanceStates =
        new(StringComparer.Ordinal) { "CASH_RELEASE", "AWAITING_ACK", "OUTSTANDING", "PARTIALLY_RETIRED" };

    public static void MapRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var requests = app.MapGroup("/api/v1/requests").RequireAuthorization();

        requests.MapPost("/", CreateDraftAsync);
        requests.MapGet("/", ListAsync);
        requests.MapGet("/{id:guid}", GetByIdAsync);
        requests.MapPut("/{id:guid}", UpdateDraftAsync);
        requests.MapPost("/{id:guid}/submit", SubmitAsync);
        requests.MapPost("/{id:guid}/actions", ExecuteActionAsync);
        requests.MapGet("/{id:guid}/history", GetHistoryAsync);

        var my = app.MapGroup("/api/v1/my").RequireAuthorization();

        my.MapGet("/inbox", GetInboxAsync);
        my.MapGet("/requests", GetMyRequestsAsync);
        my.MapGet("/advances", GetMyAdvancesAsync);
    }

    private static async Task<IResult> CreateDraftAsync(
        CreateRequestDto dto,
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        WorkflowDbContext db,
        IWorkflowDefinitionProvider definitions,
        IRequestNumberGenerator numberGenerator,
        IWorkflowClock clock,
        ICurrentUserAccessor currentUser,
        IBankDetailsAuditor bankDetailsAuditor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.ModuleKey))
        {
            return ProblemResults.BadRequest("'moduleKey' is required.", httpRequest.Path);
        }

        if (dto.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return ProblemResults.BadRequest("'payload' is required.", httpRequest.Path);
        }

        // Definition lookup happens before payload parsing so an unknown
        // moduleKey reports as "no such module" (404, via
        // GlobalExceptionHandler) rather than a confusing parse failure.
        var definition = await definitions.GetAsync(dto.ModuleKey, cancellationToken);
        var employee = await currentUser.GetEmployeeAsync(cancellationToken);

        var paymentMethodThresholdNgn = definition.GetPolicyValue("PAYMENT_METHOD_THRESHOLD_NGN", clock.UtcNow);

        Request request;
        try
        {
            request = dto.ModuleKey switch
            {
                "EXPENSE" => BuildExpenseDraft(dto.Payload, paymentMethodThresholdNgn),
                "CASH_ADVANCE" => BuildCashAdvanceDraft(dto.Payload, paymentMethodThresholdNgn),
                _ => throw new ArgumentException(
                    $"Module '{dto.ModuleKey}' does not support draft creation via this endpoint.")
            };
        }
        catch (JsonException ex)
        {
            return ProblemResults.BadRequest($"'payload' could not be parsed: {ex.Message}", httpRequest.Path);
        }

        var now = clock.UtcNow;

        request.RequestNumber = await numberGenerator.GenerateAsync(definition, now, cancellationToken);
        request.FormRevision = definition.FormRevision;
        request.CurrentState = definition.InitialState.Key;
        request.StateEnteredAt = now;
        request.RequesterId = employee.Id;
        request.DepartmentId = employee.DepartmentId;

        // "Pay me": resolve the requester's own beneficiary now that the
        // request has an id, so the SecurityEvent the auditor writes points
        // at a real request rather than a placeholder. RequestId is assigned
        // at construction, so it is available before SaveChanges.
        //
        // Throws MissingBankDetailsException when the employee has no bank
        // details on file — a 409, and the correct answer: the claim is
        // well-formed and the person exists, but there is nowhere to pay
        // them.
        if (request is ExpenseRequest expenseRequest && expenseRequest.BeneficiaryId == Guid.Empty)
        {
            var beneficiary = await AdvanceRetirementEndpoints.FindOrCreateEmployeeBeneficiaryAsync(
                db, bankDetailsAuditor, employee.Id, request.RequestId, dto.ModuleKey, employee.Id, now, cancellationToken);

            expenseRequest.BeneficiaryId = beneficiary.Id;
        }

        db.Add(request);
        await db.SaveChangesAsync(cancellationToken);

        httpResponse.SetETag(request.RowVersion);

        return Results.Created($"/api/v1/requests/{request.RequestId}", ToDetailDto(request));
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        WorkflowDbContext db,
        ReadAccessScope readAccess,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var request = await db.Requests
            .Include(r => ((ExpenseRequest)r).Lines)
            .Include(r => ((CashAdvanceRequest)r).Lines)
            .FirstOrDefaultAsync(r => r.RequestId == id, cancellationToken);

        if (request is null)
        {
            return ProblemResults.NotFound("Request", id, httpRequest.Path);
        }

        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        var roles = currentUser.GetRoles();

        if (!await readAccess.CanReadAsync(request, employee, roles, cancellationToken))
        {
            return ProblemResults.Forbidden("You do not have access to this request.", httpRequest.Path);
        }

        await AttachGlPostingLinesAsync(db, request, cancellationToken);

        httpResponse.SetETag(request.RowVersion);

        return Results.Ok(ToDetailDto(request));
    }

    private static async Task<IResult> UpdateDraftAsync(
        Guid id,
        JsonElement payload,
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        WorkflowDbContext db,
        IWorkflowDefinitionProvider definitions,
        IWorkflowClock clock,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var request = await db.Requests
            .Include(r => ((ExpenseRequest)r).Lines)
            .Include(r => ((CashAdvanceRequest)r).Lines)
            .FirstOrDefaultAsync(r => r.RequestId == id, cancellationToken);

        if (request is null)
        {
            return ProblemResults.NotFound("Request", id, httpRequest.Path);
        }

        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        if (request.RequesterId != employee.Id)
        {
            return ProblemResults.Forbidden("Only the requester may edit this draft.", httpRequest.Path);
        }

        if (!EditableStates.Contains(request.CurrentState))
        {
            return ProblemResults.BadRequest(
                $"Request is in state '{request.CurrentState}' and can no longer be edited.", httpRequest.Path);
        }

        if (!httpRequest.TryGetIfMatchRowVersion(out var ifMatch))
        {
            return ProblemResults.PreconditionRequired(httpRequest.Path);
        }

        if (request.RowVersion is null || !ifMatch.AsSpan().SequenceEqual(request.RowVersion))
        {
            return ProblemResults.PreconditionFailed(httpRequest.Path);
        }

        try
        {
            switch (request)
            {
                case ExpenseRequest expense:
                {
                    var definition = await definitions.GetAsync(expense.ModuleKey, cancellationToken);
                    ApplyExpenseFields(
                        expense, payload, definition.GetPolicyValue("PAYMENT_METHOD_THRESHOLD_NGN", clock.UtcNow));
                    break;
                }
                case CashAdvanceRequest advance:
                {
                    var definition = await definitions.GetAsync(advance.ModuleKey, cancellationToken);
                    ApplyCashAdvanceFields(
                        advance, payload, definition.GetPolicyValue("PAYMENT_METHOD_THRESHOLD_NGN", clock.UtcNow));
                    break;
                }
                default:
                    return ProblemResults.BadRequest("This module does not support updates.", httpRequest.Path);
            }
        }
        catch (JsonException ex)
        {
            return ProblemResults.BadRequest($"'payload' could not be parsed: {ex.Message}", httpRequest.Path);
        }

        await db.SaveChangesAsync(cancellationToken);

        httpResponse.SetETag(request.RowVersion);

        return Results.Ok(ToDetailDto(request));
    }

    private static async Task<IResult> SubmitAsync(
        Guid id,
        ActionRequestDto? dto,
        HttpRequest httpRequest,
        RequestActionService actionService,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);

        var result = await actionService.ExecuteAsync(
            id, actingUser, new TransitionRequest("SUBMIT", dto?.Comment, IdempotencyKey: dto?.IdempotencyKey),
            cancellationToken);

        return result.ToApiResult(httpRequest.Path);
    }

    private static async Task<IResult> ExecuteActionAsync(
        Guid id,
        ExecuteActionDto dto,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        RequestActionService actionService,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Action))
        {
            return ProblemResults.BadRequest("'action' is required.", httpRequest.Path);
        }

        // TreasuryNumber is both a Captures entry and a guard-evaluated field
        // (both modules' FINANCE_VERIFY VERIFY transition) -- the guard reads
        // it straight off the tracked Request (see WorkflowEngine.
        // TryEvaluateGuard / RequestActionService.RunTransitionAsync's
        // identity-map reload), so it must land on the entity before
        // ExecuteAsync runs, not merely inside TransitionRequest.
        // CapturedFields. No other capture in either module's definition is
        // also a guard field once module-specific captures (JournalVoucherNumber,
        // GlPostingLines) are handled by their own dedicated endpoint instead of
        // this generic one, so this narrow, named staging step is enough.
        if (dto.Payload is not null && dto.Payload.TryGetValue("TreasuryNumber", out var raw) &&
            TryReadString(raw) is { } treasuryNumber)
        {
            var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == id, cancellationToken);
            if (request is null)
            {
                return ProblemResults.NotFound("Request", id, httpRequest.Path);
            }

            request.TreasuryNumber = treasuryNumber;
            await db.SaveChangesAsync(cancellationToken);
        }

        var actingUser = await currentUser.GetActingUserAsync(cancellationToken);

        var result = await actionService.ExecuteAsync(
            id, actingUser, new TransitionRequest(dto.Action, dto.Comment, dto.Payload, dto.IdempotencyKey),
            cancellationToken);

        return result.ToApiResult(httpRequest.Path);
    }

    private static async Task<IResult> GetHistoryAsync(
        Guid id,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        ReadAccessScope readAccess,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var request = await db.Requests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RequestId == id, cancellationToken);

        if (request is null)
        {
            return ProblemResults.NotFound("Request", id, httpRequest.Path);
        }

        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        var roles = currentUser.GetRoles();

        if (!await readAccess.CanReadAsync(request, employee, roles, cancellationToken))
        {
            return ProblemResults.Forbidden("You do not have access to this request.", httpRequest.Path);
        }

        var events = await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.RequestId == id)
            .OrderBy(e => e.AuditEventId)
            .Select(e => new
            {
                e.AuditEventId,
                e.EventType,
                e.FromState,
                e.ToState,
                e.ActorId,
                e.ActorRole,
                e.OnBehalfOfUserId,
                e.Reason,
                e.PayloadJson,
                e.OccurredAtUtc
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(events);
    }

    private static async Task<IResult> ListAsync(
        string? state,
        string? module,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int? pageSize,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        ReadAccessScope readAccess,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        var roles = currentUser.GetRoles();

        var scoped = await readAccess.ScopeAsync(
            db.Requests
                .Include(r => ((ExpenseRequest)r).Lines)
                .Include(r => ((CashAdvanceRequest)r).Lines),
            employee, roles, cancellationToken);

        if (!string.IsNullOrWhiteSpace(state))
        {
            scoped = scoped.Where(r => r.CurrentState == state);
        }

        if (!string.IsNullOrWhiteSpace(module))
        {
            scoped = scoped.Where(r => r.ModuleKey == module);
        }

        if (from is { } fromValue)
        {
            scoped = scoped.Where(r => r.StateEnteredAt >= fromValue);
        }

        if (to is { } toValue)
        {
            scoped = scoped.Where(r => r.StateEnteredAt <= toValue);
        }

        // Single-column cursor on StateEnteredAt rather than a full
        // composite (StateEnteredAt, RequestId) keyset: the latter needs EF
        // to translate a Guid.CompareTo() tie-break into T-SQL, which this
        // offline environment has no way to verify against SQL Server. The
        // accepted edge case -- two requests entering the same state within
        // the same tick, one skipped across a page boundary -- is rare
        // enough on this table to not be worth an unverifiable translation.
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeCursor(cursor, out var cursorValue))
            {
                return ProblemResults.BadRequest("'cursor' is not valid.", httpRequest.Path);
            }

            scoped = scoped.Where(r => r.StateEnteredAt < cursorValue);
        }

        var size = pageSize is > 0 and <= 200 ? pageSize.Value : 50;

        var page = await scoped
            .OrderByDescending(r => r.StateEnteredAt)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > size;
        var items = hasMore ? page.Take(size).ToList() : page;
        var nextCursor = hasMore ? EncodeCursor(items[^1].StateEnteredAt) : null;

        return Results.Ok(new { Items = items.Select(ToSummaryDto), NextCursor = nextCursor });
    }

    private static async Task<IResult> GetInboxAsync(
        WorkflowDbContext db,
        InboxStateIndex inboxIndex,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        var roles = currentUser.GetRoles();

        var roleStateKeys = inboxIndex.StatesFor(roles)
            .Select(s => s.ModuleKey + "|" + s.State)
            .ToHashSet(StringComparer.Ordinal);

        // One predicate rather than Concat(...).Distinct(), and no Includes.
        //
        // The previous shape included ExpenseRequest.Lines and
        // CashAdvanceRequest.Lines and then concatenated two queries and
        // deduplicated them. Two problems, and the first hid the second:
        //
        //   * EF cannot translate a collection include through Concat +
        //     Distinct, and threw "Unable to translate a collection subquery
        //     in a projection" at runtime. This endpoint had never executed
        //     against a real user -- every call failed earlier, at
        //     authentication or at employee resolution -- so nothing had ever
        //     reached the query.
        //   * The includes were never used. ToSummaryDto projects twelve
        //     scalar columns and touches no line. The query was loading every
        //     line of every open request in order to discard them.
        //
        // "Mine, or in a state my roles can act on" is a single OR. It reads
        // as the sentence it is, needs no Distinct because a row is returned
        // once regardless of why it matched, and lets the covering index on
        // (ClosedAt, SlaDueAt) do its job.
        var requests = await db.Requests
            .AsNoTracking()
            .Where(r => r.ClosedAt == null
                        && (r.CurrentActorId == employee.Id
                            || roleStateKeys.Contains(r.ModuleKey + "|" + r.CurrentState)))
            .OrderBy(r => r.SlaDueAt ?? DateTimeOffset.MaxValue)
            .ToListAsync(cancellationToken);

        return Results.Ok(requests.Select(ToSummaryDto));
    }

    private static async Task<IResult> GetMyRequestsAsync(
        WorkflowDbContext db, ICurrentUserAccessor currentUser, CancellationToken cancellationToken)
    {
        var employee = await currentUser.GetEmployeeAsync(cancellationToken);

        // Same reasoning as the inbox: ToSummaryDto projects scalars only, so
        // including the line collections loaded every line of every request
        // this person has ever raised in order to discard them.
        var mine = await db.Requests
            .AsNoTracking()
            .Where(r => r.RequesterId == employee.Id)
            .OrderByDescending(r => r.StateEnteredAt)
            .ToListAsync(cancellationToken);

        return Results.Ok(mine.Select(ToSummaryDto));
    }

    private static async Task<IResult> GetMyAdvancesAsync(
        WorkflowDbContext db, ICurrentUserAccessor currentUser, CancellationToken cancellationToken)
    {
        var employee = await currentUser.GetEmployeeAsync(cancellationToken);

        var advances = await db.CashAdvanceRequests
            .Include(a => a.Lines)
            .Where(a => a.RequesterId == employee.Id && OpenAdvanceStates.Contains(a.CurrentState))
            .OrderByDescending(a => a.StateEnteredAt)
            .ToListAsync(cancellationToken);

        return Results.Ok(advances.Select(ToDetailDto));
    }

    internal static async Task AttachGlPostingLinesAsync(
        WorkflowDbContext db, Request request, CancellationToken cancellationToken)
    {
        // GlPostingLine keys to the shared base Request row, not to either
        // sibling table (TPT: one FK column, two sibling tables) -- it can
        // never be a mapped Include(), same reasoning as
        // RequestActionService.RunTransitionAsync.
        if (request is not (ExpenseRequest or CashAdvanceRequest))
        {
            return;
        }

        var lines = await db.GlPostingLines
            .AsNoTracking()
            .Where(l => l.RequestId == request.RequestId)
            .ToListAsync(cancellationToken);

        switch (request)
        {
            case ExpenseRequest expense: expense.GlPostingLines = lines; break;
            case CashAdvanceRequest advance: advance.GlPostingLines = lines; break;
        }
    }

    private static ExpenseRequest BuildExpenseDraft(JsonElement payload, decimal paymentMethodThresholdNgn) =>
        ApplyExpenseFields(new ExpenseRequest(), payload, paymentMethodThresholdNgn);

    private static ExpenseRequest ApplyExpenseFields(
        ExpenseRequest expense, JsonElement payload, decimal paymentMethodThresholdNgn)
    {
        var data = payload.Deserialize<ExpenseDraftPayload>(JsonOptions)
            ?? throw new JsonException("Expense payload deserialised to null.");

        if (!Enum.TryParse<ReceiptStatus>(data.ReceiptStatus, ignoreCase: true, out var receiptStatus))
        {
            throw new ArgumentException($"'{data.ReceiptStatus}' is not a valid receipt status.");
        }

        // Guid.Empty is the sentinel for "pay me", resolved by
        // CreateDraftAsync once the request exists and can carry the audit
        // context. Left here rather than resolved in this method because
        // ApplyExpenseFields is synchronous and has no database access --
        // making it async to reach the auditor would push that dependency
        // through every caller including the update path.
        expense.BeneficiaryId = data.BeneficiaryId ?? Guid.Empty;
        expense.ReceiptStatus = receiptStatus;
        expense.Lines.Clear();

        var lineNumber = 1;
        foreach (var line in data.Lines)
        {
            var expenseLine = new ExpenseLine
            {
                RequestId = expense.RequestId,
                LineNumber = lineNumber++,
                Description = line.Description,
                ExpenseDate = line.ExpenseDate,
                ExpenseCategoryId = line.ExpenseCategoryId,
                ProjectCode = line.ProjectCode,
                CostCentreCode = line.CostCentreCode,
                CurrencyCode = line.CurrencyCode,
                Amount = line.Amount,
                FxRate = line.FxRate,
                FxRateDate = line.FxRateDate
            };

            if (!expenseLine.HasValidAllocation)
            {
                throw new ArgumentException(
                    $"Line {expenseLine.LineNumber}: exactly one of projectCode or costCentreCode must be set.");
            }

            expenseLine.RecalculateNgn();
            expense.Lines.Add(expenseLine);
        }

        expense.RecalculateTotals();
        expense.ApplyPaymentMethodPolicy(paymentMethodThresholdNgn);
        return expense;
    }

    private static CashAdvanceRequest BuildCashAdvanceDraft(JsonElement payload, decimal paymentMethodThresholdNgn) =>
        ApplyCashAdvanceFields(new CashAdvanceRequest(), payload, paymentMethodThresholdNgn);

    private static CashAdvanceRequest ApplyCashAdvanceFields(
        CashAdvanceRequest advance, JsonElement payload, decimal paymentMethodThresholdNgn)
    {
        var data = payload.Deserialize<CashAdvanceDraftPayload>(JsonOptions)
            ?? throw new JsonException("Cash advance payload deserialised to null.");

        if (!Enum.TryParse<AllocationType>(data.AllocationType, ignoreCase: true, out var allocationType))
        {
            throw new ArgumentException($"'{data.AllocationType}' is not a valid allocation type.");
        }

        if (!Enum.TryParse<StationScope>(data.StationScope, ignoreCase: true, out var stationScope))
        {
            throw new ArgumentException($"'{data.StationScope}' is not a valid station scope.");
        }

        advance.Purpose = data.Purpose;
        advance.AllocationType = allocationType;
        advance.ProjectCode = data.ProjectCode;
        advance.CostCentreCode = data.CostCentreCode;
        advance.StationScope = stationScope;
        advance.HasSupportingDocuments = data.HasSupportingDocuments;

        if (!advance.HasValidAllocation)
        {
            throw new ArgumentException(
                allocationType == AllocationType.Project
                    ? "projectCode is required when allocationType is Project."
                    : "costCentreCode is required when allocationType is CostCentre.");
        }

        advance.Lines.Clear();

        var lineNumber = 1;
        foreach (var line in data.Lines)
        {
            var advanceLine = new AdvanceLine
            {
                RequestId = advance.RequestId,
                LineNumber = lineNumber++,
                Description = line.Description,
                CurrencyCode = line.CurrencyCode,
                Amount = line.Amount,
                FxRate = line.FxRate,
                FxRateDate = line.FxRateDate
            };

            advanceLine.RecalculateNgn();
            advance.Lines.Add(advanceLine);
        }

        advance.RecalculateTotals();
        advance.ApplyPaymentMethodPolicy(paymentMethodThresholdNgn);
        return advance;
    }

    private static string? TryReadString(object? raw) => raw switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null
    };

    private static string EncodeCursor(DateTimeOffset stateEnteredAt) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            stateEnteredAt.UtcTicks.ToString(CultureInfo.InvariantCulture)));

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset stateEnteredAt)
    {
        stateEnteredAt = default;

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                return false;
            }

            stateEnteredAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static object ToSummaryDto(Request request) => new
    {
        request.RequestId,
        request.RequestNumber,
        request.ModuleKey,
        request.CurrentState,
        request.CurrentActorId,
        request.RequesterId,
        request.DepartmentId,
        request.TotalAmountNgn,
        request.StateEnteredAt,
        request.SlaDueAt,
        request.SubmittedAt,
        request.ClosedAt
    };

    internal static object ToDetailDto(Request request) => request switch
    {
        ExpenseRequest expense => new
        {
            expense.RequestId,
            expense.RequestNumber,
            expense.ModuleKey,
            expense.FormCode,
            expense.FormRevision,
            expense.TreasuryNumber,
            expense.JournalVoucherNumber,
            expense.CurrentState,
            expense.CurrentActorId,
            expense.StateEnteredAt,
            expense.SlaDueAt,
            expense.RevisionNumber,
            expense.RequesterId,
            expense.DepartmentId,
            expense.TotalAmountNgn,
            expense.SubmittedAt,
            expense.ClosedAt,
            expense.BeneficiaryId,
            expense.RetiresAdvanceId,
            expense.AdvanceAmountNgn,
            expense.NetPayableNgn,
            expense.IsRefundDue,
            ReceiptStatus = expense.ReceiptStatus.ToString(),
            PaymentMethod = expense.PaymentMethod.ToString(),
            expense.PostedByUserId,
            expense.AuthorisedByUserId,
            expense.PostedAt,
            expense.PaymentReference,
            expense.PaymentDate,
            expense.AcknowledgedByUserId,
            expense.AcknowledgedAt,
            expense.RefundReceivedAmountNgn,
            Lines = expense.Lines.OrderBy(l => l.LineNumber).Select(l => new
            {
                l.LineId,
                l.LineNumber,
                l.Description,
                l.ExpenseDate,
                l.ExpenseCategoryId,
                l.ProjectCode,
                l.CostCentreCode,
                l.CurrencyCode,
                l.Amount,
                l.FxRate,
                l.FxRateDate,
                l.AmountNgn
            }),
            GlPostingLines = expense.GlPostingLines.Select(ToGlLineDto)
        },

        CashAdvanceRequest advance => new
        {
            advance.RequestId,
            advance.RequestNumber,
            advance.ModuleKey,
            advance.FormCode,
            advance.FormRevision,
            advance.TreasuryNumber,
            advance.JournalVoucherNumber,
            advance.CurrentState,
            advance.CurrentActorId,
            advance.StateEnteredAt,
            advance.SlaDueAt,
            advance.RevisionNumber,
            advance.RequesterId,
            advance.DepartmentId,
            advance.TotalAmountNgn,
            advance.SubmittedAt,
            advance.ClosedAt,
            advance.Purpose,
            AllocationType = advance.AllocationType.ToString(),
            advance.ProjectCode,
            advance.CostCentreCode,
            StationScope = advance.StationScope.ToString(),
            advance.HasSupportingDocuments,
            PaymentMethod = advance.PaymentMethod.ToString(),
            advance.CashReleasedAt,
            advance.AcknowledgedByUserId,
            advance.AcknowledgedAt,
            advance.RetirementDueDate,
            RetirementStatus = advance.RetirementStatus.ToString(),
            advance.PostedByUserId,
            advance.AuthorisedByUserId,
            advance.RetiredAmountNgn,
            advance.RetirementBalanceNgn,
            Lines = advance.Lines.OrderBy(l => l.LineNumber).Select(l => new
            {
                l.LineId,
                l.LineNumber,
                l.Description,
                l.CurrencyCode,
                l.Amount,
                l.FxRate,
                l.FxRateDate,
                l.AmountNgn
            }),
            GlPostingLines = advance.GlPostingLines.Select(ToGlLineDto)
        },

        _ => ToSummaryDto(request)
    };

    internal static object ToGlLineDto(GlPostingLine line) => new
    {
        line.PostingLineId,
        Side = line.Side.ToString(),
        line.AccountNumber,
        line.Narration,
        line.AmountNgn
    };

    private sealed record ExpenseLinePayload(
        string Description,
        DateOnly ExpenseDate,
        int? ExpenseCategoryId,
        string? ProjectCode,
        string? CostCentreCode,
        string CurrencyCode,
        decimal Amount,
        decimal FxRate,
        DateOnly FxRateDate);

    /// <summary>
    /// BeneficiaryId is optional, and omitting it means "pay me".
    ///
    /// DEL-AC-FRM-002 says "Please issue payment in favour of company/staff",
    /// and the ordinary case is a member of staff claiming their own
    /// expenses. Requiring an explicit id made that case impossible: a
    /// Beneficiary row is only ever created by the advance-retirement path,
    /// so a requester with no prior advance had nobody to select — including
    /// themselves.
    ///
    /// Resolved server-side rather than by the client sending its own
    /// employee's beneficiary id, because creating one requires bank details
    /// and writes a SecurityEvent. That has to happen through
    /// IBankDetailsAuditor on the server, not by a browser asserting who it
    /// is paying.
    /// </summary>
    private sealed record ExpenseDraftPayload(
        Guid? BeneficiaryId,
        string ReceiptStatus,
        IReadOnlyList<ExpenseLinePayload> Lines);

    private sealed record AdvanceLinePayload(
        string Description,
        string CurrencyCode,
        decimal Amount,
        decimal FxRate,
        DateOnly FxRateDate);

    private sealed record CashAdvanceDraftPayload(
        string Purpose,
        string AllocationType,
        string? ProjectCode,
        string? CostCentreCode,
        string StationScope,
        bool HasSupportingDocuments,
        IReadOnlyList<AdvanceLinePayload> Lines);
}

/// <summary>{ moduleKey, payload } -- payload is module-specific, parsed
/// against ExpenseDraftPayload/CashAdvanceDraftPayload by ModuleKey.</summary>
public sealed record CreateRequestDto(string ModuleKey, JsonElement Payload);

/// <summary>Body for the generic POST /actions mutation entry point.</summary>
public sealed record ExecuteActionDto(
    string Action,
    string? Comment = null,
    IReadOnlyDictionary<string, object?>? Payload = null,
    string? IdempotencyKey = null);

/// <summary>Shared minimal body for the thin action wrappers (submit,
/// acknowledge, and the other module-specific single-purpose endpoints)
/// that need nothing beyond an optional comment and idempotency key.</summary>
public sealed record ActionRequestDto(string? Comment = null, string? IdempotencyKey = null);
