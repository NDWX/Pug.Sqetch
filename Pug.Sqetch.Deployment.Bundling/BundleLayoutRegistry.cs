using System.Diagnostics.CodeAnalysis;

namespace Pug.Sqetch.Deployment.Bundling;

/// <summary>
/// Resolves bundle layouts by name. Custom layouts can be plugged in by registering a
/// factory and passing the registry to the bundle writer.
/// </summary>
public class BundleLayoutRegistry : IBundleLayoutRegistry
{
	private readonly Dictionary<string, Func<IBundleLayout>> _factories = new ( StringComparer.OrdinalIgnoreCase );

	public BundleLayoutRegistry()
	{
	}

	public IEnumerable<string> Names => _factories.Keys;

	public void Register( string name, Func<IBundleLayout> factory )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( name );
		ArgumentNullException.ThrowIfNull( factory );

		_factories[name] = factory;
	}

	public bool TryCreate( string name, [NotNullWhen( true )] out IBundleLayout? layout )
	{
		if( _factories.TryGetValue( name, out Func<IBundleLayout>? factory ) )
		{
			layout = factory();
			return true;
		}

		layout = null;
		return false;
	}

	public IBundleLayout Create( string name )
		=> TryCreate( name, out IBundleLayout? layout )
			? layout
			: throw new UnknownBundleLayoutException( name, Names );
}