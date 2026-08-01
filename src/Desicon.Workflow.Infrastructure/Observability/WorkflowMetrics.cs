using System.Diagnostics.Metrics;

namespace Desicon.Workflow.Infrastructure.Observability;

/// <summary>
/// Process-wide meter for signals that need to page someone, not just sit in
/// a table. SecurityEvent rows are already durable (see SecurityEventWriter)
/// -- this is the same fact restated as a counter so an alerting rule can
/// watch its rate rather than someone remembering to query the table.
///
/// Built on System.Diagnostics.Metrics (BCL, no package): any OpenTelemetry
/// or Application Insights exporter wired up at the hosting layer picks this
/// meter up by name. No exporter is configured in this project yet -- that is
/// a deployment-time concern (doc 05 §3's Application Insights pipeline), not
/// a code one.
/// </summary>
public static class WorkflowMetrics
{
    public const string MeterName = "Desicon.Workflow";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Incremented once per denied action (NotAuthorised, GuardFailed
    /// or PolicyViolation) -- every SecurityEvent written, tagged by module and
    /// reason so an alert can distinguish "one user fat-fingering a guard" from
    /// "someone probing for a hole".</summary>
    public static readonly Counter<long> SecurityEventsWritten =
        Meter.CreateCounter<long>(
            "desicon_workflow_security_events_total",
            unit: "{event}",
            description: "Count of SecurityEvent rows written (denied workflow actions).");
}
