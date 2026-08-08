namespace Desicon.Workflow.Infrastructure.Workflow;

/// <summary>
/// An action the caller is authorised to take on a request, and whether they
/// can take it yet.
/// </summary>
/// <param name="Action">The transition's action name, e.g. VERIFY.</param>
/// <param name="IsEnabled">
/// False means the authority is theirs but the request is not in a state the
/// guard accepts -- usually because data the action itself captures has not
/// been entered. It does NOT mean "not permitted", and a screen that treats
/// the two the same makes several capture steps unreachable: the field that
/// supplies the missing value would render only once the value was present.
/// </param>
/// <param name="BlockedReason">
/// The definition's own guardMessage, written for a person and naming the
/// condition to fix. Null when IsEnabled.
/// </param>
public sealed record AvailableAction(string Action, bool IsEnabled, string? BlockedReason);
