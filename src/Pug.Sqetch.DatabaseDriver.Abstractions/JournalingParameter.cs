namespace Pug.Sqetch.DatabaseDriver;

/// <summary>
/// One named value bound into a journaling statement or query.
/// </summary>
/// <param name="Name">
/// The bare parameter name, with no provider prefix — the maintainer writes '@release' in the SQL,
/// and adding or translating the prefix (or a positional placeholder) is the driver's job. Sqetch
/// never scans SQL for placeholders, so a driver for a provider that rejects unreferenced
/// parameters must do its own scan.
/// </param>
/// <param name="Value">
/// The value bound to <paramref name="Name"/>, boxed as <see cref="object"/> the way
/// <see cref="System.Data.IDataParameter.Value"/> is, so a driver can assign it straight across.
/// Never null. Today it is a non-null <see cref="string"/> for every parameter except
/// 'utcTimestamp', which is a <see cref="DateTime"/> with <see cref="DateTimeKind.Utc"/> — a
/// timestamp is passed as one rather than as text because no provider implicitly casts text to a
/// timestamp column, and formatting it would make every maintainer cast it back in SQL.
/// </param>
public readonly record struct JournalingParameter( string Name, object Value );
