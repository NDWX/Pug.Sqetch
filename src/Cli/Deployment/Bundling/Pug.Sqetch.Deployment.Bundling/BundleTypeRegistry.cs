using System.Diagnostics.CodeAnalysis;

namespace Pug.Sqetch.Deployment.Bundling;

/// <summary>
/// Resolves bundle types by name. Custom types can be plugged in by registering a factory
/// and passing the registry to the bundle writer.
/// </summary>
public class BundleTypeRegistry : IBundleTypeRegistry
{
	private readonly Dictionary<string, Func<IBundleType>> _factories = new( StringComparer.OrdinalIgnoreCase );

	public BundleTypeRegistry()
	{
	}

	public IEnumerable<string> Names => _factories.Keys;

	public void Register( string name, Func<IBundleType> factory )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( name );
		ArgumentNullException.ThrowIfNull( factory );

		_factories[name] = factory;
	}

	public bool TryCreate( string name, [NotNullWhen( true )] out IBundleType? type )
	{
		if( _factories.TryGetValue( name, out Func<IBundleType>? factory ) )
		{
			type = factory();

			return true;
		}

		type = null;

		return false;
	}

	public IBundleType Create( string name )
		=> TryCreate( name, out IBundleType? type ) ? type : throw new UnknownBundleTypeException( name, Names );
}