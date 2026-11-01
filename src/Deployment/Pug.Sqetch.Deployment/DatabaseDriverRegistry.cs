using System.Diagnostics.CodeAnalysis;
using Pug.Sqetch.DatabaseDriver;

namespace Pug.Sqetch.Deployment;

/// <summary>
/// Resolves database driver factories by driver name. Ships empty; the host registers the
/// drivers it wants to offer.
/// </summary>
public class DatabaseDriverRegistry : IDatabaseDriverRegistry
{
	private readonly Dictionary<string, Func<IDatabaseDriverFactory>> _factories = new ( StringComparer.OrdinalIgnoreCase );

	public IEnumerable<string> Names => _factories.Keys;

	public void Register( string name, Func<IDatabaseDriverFactory> factory )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( name );
		ArgumentNullException.ThrowIfNull( factory );

		_factories[name] = factory;
	}

	public bool TryCreate( string name, [NotNullWhen( true )] out IDatabaseDriverFactory? factory )
	{
		if( _factories.TryGetValue( name, out Func<IDatabaseDriverFactory>? create ) )
		{
			factory = create();
			return true;
		}

		factory = null;
		return false;
	}

	public IDatabaseDriverFactory Create( string name )
		=> TryCreate( name, out IDatabaseDriverFactory? factory )
			? factory
			: throw new UnknownDatabaseDriverException( name, Names );
}
