-------------------------------------------------------------------------------
-- Backfill Requests.SubmittedAt from the audit trail.
--
-- WHY THIS EXISTS
-- ---------------
-- Request.SubmittedAt has existed since the first migration -- entity, EF
-- configuration, guard field, three DTOs and a composite index -- and nothing
-- ever wrote to it. That was fixed in RequestActionService on 22 Aug 2026, so
-- every request submitted from that date forward carries the value.
--
-- Everything submitted BEFORE that date still has NULL, permanently, and the
-- pipeline report filters on it:
--
--     .Where(r => r.ClosedAt == null && r.SubmittedAt != null)
--
-- So an open request raised before 22 Aug 2026 is invisible in "What's waiting
-- on whom" -- not late, not queued, not anywhere. The report would show zero
-- and look calm while work sat in somebody's queue.
--
-- This is the same failure the report was built to end. A number that reads
-- zero because nothing is there is fine. A number that reads zero because the
-- rows were filtered out is worse than no number at all, because it is
-- believed.
--
-- WHERE THE VALUE COMES FROM
-- --------------------------
-- The audit trail, which has been correct all along. Every request leaves
-- DRAFT exactly once, and that event is its submission. AuditEvents is
-- append-only and hash-chained, so this is a reconstruction from evidence
-- rather than a guess:
--
--     MIN(OccurredAtUtc) WHERE FromState = 'DRAFT'
--
-- DRAFT is the initial state of both live modules (expense-reimbursement and
-- cash-advance, workflow v4) -- checked, not assumed.
--
-- RESUBMIT is deliberately not matched: it moves RETURNED -> DEPT_HEAD, so its
-- FromState is RETURNED. A claim that was returned and resubmitted keeps its
-- original submission date, which is what an SLA should be measured from.
--
-- SAFETY
-- ------
-- Reports by default and changes nothing. Set @Apply = 1 to write.
-- Only ever fills NULLs -- it cannot overwrite a value the application wrote,
-- so it is safe to run twice.
--
-- Usage -- Invoke-Sqlcmd, not sqlcmd. sqlcmd is not installed on this
-- machine and every other .sql in this directory is run the same way:
--
--   . .\scripts\dev-db-connect.ps1      # dot-source: refreshes firewall + $token
--
--   Invoke-Sqlcmd -ServerInstance "sql-desicon-fw-dev.database.windows.net" `
--     -Database "DesiconFinanceWorkflow" -AccessToken $token `
--     -InputFile "scripts/backfill-submitted-at.sql" `
--     -Variable @("Apply=0") -Verbose        # report only
--
-- Apply=1 to write. The variable is required, deliberately: Invoke-Sqlcmd
-- fails on an undefined $(Apply) rather than guessing, and failing to run is
-- the safe outcome for a script that can modify a column the reports depend
-- on.
-------------------------------------------------------------------------------

SET NOCOUNT ON;

DECLARE @Apply bit = CASE WHEN '$(Apply)' = '1' THEN 1 ELSE 0 END;

-------------------------------------------------------------------------------
-- 1. What is actually missing
-------------------------------------------------------------------------------

SELECT
    'Requests with no SubmittedAt' AS Finding,
    COUNT(*)                                                   AS Total,
    SUM(CASE WHEN r.ClosedAt IS NULL THEN 1 ELSE 0 END)        AS StillOpen,
    SUM(CASE WHEN r.ClosedAt IS NULL THEN 1 ELSE 0 END)        AS InvisibleInPipelineReport
FROM dbo.Requests AS r
WHERE r.SubmittedAt IS NULL;

-- The open ones, named. These are the rows the report is currently hiding.
SELECT
    r.RequestNumber,
    r.ModuleKey,
    r.DefinitionVersion,
    r.CurrentState,
    r.TotalAmountNgn,
    r.StateEnteredAt,
    a.RecoveredSubmittedAt
FROM dbo.Requests AS r
OUTER APPLY (
    SELECT MIN(ae.OccurredAtUtc) AS RecoveredSubmittedAt
    FROM dbo.AuditEvents AS ae
    WHERE ae.RequestId = r.RequestId
      AND ae.FromState = 'DRAFT'
) AS a
WHERE r.SubmittedAt IS NULL
  AND r.ClosedAt IS NULL
ORDER BY a.RecoveredSubmittedAt;

-- Anything here cannot be repaired from the trail and needs a human decision.
-- Expected to be empty: a request that never left DRAFT is still a draft, and
-- a draft has no submission date because it was never submitted.
SELECT
    r.RequestNumber,
    r.CurrentState,
    'No DRAFT departure in the audit trail' AS Why
FROM dbo.Requests AS r
WHERE r.SubmittedAt IS NULL
  AND r.ClosedAt IS NULL
  AND r.CurrentState <> 'DRAFT'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.AuditEvents AS ae
      WHERE ae.RequestId = r.RequestId AND ae.FromState = 'DRAFT'
  );

-------------------------------------------------------------------------------
-- 2. Repair
-------------------------------------------------------------------------------

IF @Apply = 0
BEGIN
    PRINT '';
    PRINT 'REPORT ONLY -- nothing was written.';
    PRINT 'Re-run with  -v Apply=1  to backfill the rows listed above.';
END
ELSE
BEGIN
    BEGIN TRANSACTION;

    UPDATE r
    SET r.SubmittedAt = a.RecoveredSubmittedAt
    FROM dbo.Requests AS r
    CROSS APPLY (
        SELECT MIN(ae.OccurredAtUtc) AS RecoveredSubmittedAt
        FROM dbo.AuditEvents AS ae
        WHERE ae.RequestId = r.RequestId
          AND ae.FromState = 'DRAFT'
    ) AS a
    WHERE r.SubmittedAt IS NULL
      AND a.RecoveredSubmittedAt IS NOT NULL;

    DECLARE @Filled int = @@ROWCOUNT;

    COMMIT TRANSACTION;

    PRINT '';
    PRINT 'Backfilled ' + CAST(@Filled AS varchar(10)) + ' request(s) from the audit trail.';

    -- Prove it, rather than asserting it.
    SELECT
        'Remaining after backfill' AS Finding,
        COUNT(*) AS OpenRequestsStillMissingSubmittedAt
    FROM dbo.Requests
    WHERE SubmittedAt IS NULL
      AND ClosedAt IS NULL
      AND CurrentState <> 'DRAFT';
END
