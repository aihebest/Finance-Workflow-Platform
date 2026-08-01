using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// docs/03-Data-Model-ERD.md calls out "datetime2" for every timestamp
/// column, but the domain model uses <see cref="DateTimeOffset"/> throughout.
/// SQL Server's datetime2 has no offset component, so every value is
/// normalised to its UTC instant on the way in and read back with a zero
/// offset -- the recorded instant is unchanged, only the display offset is
/// lost, which is what choosing datetime2 over datetimeoffset means.
/// </summary>
internal static class DateTimeOffsetConverters
{
    public static readonly ValueConverter<DateTimeOffset, DateTime> ToUtcDateTime2 = new(
        v => v.UtcDateTime,
        v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));

    public static readonly ValueConverter<DateTimeOffset?, DateTime?> ToNullableUtcDateTime2 = new(
        v => v.HasValue ? v.Value.UtcDateTime : null,
        v => v.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)) : null);
}
