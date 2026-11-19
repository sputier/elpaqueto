using System;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ElPaqueto.Core;

/// <summary>
/// Stores a date in UTC and reads it back as UTC. Dates are UTC by convention in ElPaqueto; SQLite and SQL Server
/// return them with an unspecified kind, which would otherwise be taken for local time and shifted by the server's
/// UTC offset. A date of unspecified kind is written as it is, as UTC, which also keeps PostgreSQL from refusing it.
/// </summary>
public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public static readonly UtcDateTimeConverter Instance = new UtcDateTimeConverter();

    public UtcDateTimeConverter()
        : base(
            v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}
