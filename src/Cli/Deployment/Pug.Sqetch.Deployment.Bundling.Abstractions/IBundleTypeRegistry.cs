using System.Diagnostics.CodeAnalysis;

namespace Pug.Sqetch.Deployment.Bundling;

public interface IBundleTypeRegistry
{
	IEnumerable<string> Names { get; }
	void Register( string name, Func<IBundleType> factory );
	bool TryCreate( string name, [NotNullWhen( true )] out IBundleType? type );
	IBundleType Create( string name );
}