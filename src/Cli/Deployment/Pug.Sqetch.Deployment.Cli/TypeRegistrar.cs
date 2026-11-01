using System.Reflection;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Deployment.Cli;

/// <summary>
/// Minimal dependency container for the command tree: the app registers its component
/// registries here (and tests register fakes), Spectre adds its own services, and command
/// constructors are activated by resolving their parameters. Services can be registered
/// more than once — the last registration wins, and <c>IEnumerable&lt;T&gt;</c> resolves
/// to all of them (empty when none, never null), as Spectre's registrar contract requires.
/// </summary>
public sealed class TypeRegistrar : ITypeRegistrar
{
	private readonly Dictionary<Type, List<Func<object?>>> _registrations = new ();
	private readonly Resolver _resolver;

	public TypeRegistrar() => _resolver = new Resolver( this );

	public void Register( Type service, Type implementation )
		=> Registrations( service ).Add( () => _resolver.Activate( implementation ) );

	public void RegisterInstance( Type service, object implementation )
		=> Registrations( service ).Add( () => implementation );

	public void RegisterLazy( Type service, Func<object> factory )
	{
		Lazy<object> lazy = new ( factory );

		Registrations( service ).Add( () => lazy.Value );
	}

	public ITypeResolver Build() => _resolver;

	private List<Func<object?>> Registrations( Type service )
	{
		if( !_registrations.TryGetValue( service, out List<Func<object?>>? registered ) )
			_registrations[service] = registered = new List<Func<object?>>();

		return registered;
	}

	private sealed class Resolver( TypeRegistrar registrar ) : ITypeResolver
	{
		public object? Resolve( Type? type )
		{
			if( type is null )
				return null;

			if( registrar._registrations.TryGetValue( type, out List<Func<object?>>? registered ) && registered.Count > 0 )
				return registered[^1]();

			if( type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>) )
				return ResolveAll( type.GenericTypeArguments[0] );

			if( type.IsAbstract || type.IsInterface )
				return null;

			return Activate( type );
		}

		public object? Activate( Type type )
		{
			ConstructorInfo? constructor = type.GetConstructors().MaxBy( x => x.GetParameters().Length );

			return constructor is null
				? Activator.CreateInstance( type )
				: constructor.Invoke(
					constructor.GetParameters()
								.Select( parameter => Resolve( parameter.ParameterType ) )
								.ToArray() );
		}

		private Array ResolveAll( Type item )
		{
			object?[] instances =
				registrar._registrations.TryGetValue( item, out List<Func<object?>>? registered )
					? registered.Select( create => create() ).ToArray()
					: [];

			Array array = Array.CreateInstance( item, instances.Length );

			for( int index = 0; index < instances.Length; index++ )
				array.SetValue( instances[index], index );

			return array;
		}
	}
}
