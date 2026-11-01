using System.Diagnostics.CodeAnalysis;

namespace Pug.Sqetch.DatabaseDriver;

/// <summary>
/// Resolves database driver factories by driver name. Ships empty; the host registers the
/// drivers it wants to offer.
/// </summary>
public interface IDatabaseDriverRegistry
{
	IEnumerable<string> Names { get; }

	void Register( string name, Func<IDatabaseDriverFactory> factory );

	bool TryCreate( string name, [NotNullWhen( true )] out IDatabaseDriverFactory? factory );

	/// <summary>Throws <see cref="UnknownDatabaseDriverException"/> for an unknown name.</summary>
	IDatabaseDriverFactory Create( string name );
}
