namespace Pug.Sqetch.Deployment;

/// <summary>What a standalone rollback (<see cref="DeploymentEngine.RollBack"/>) undoes.</summary>
public sealed record RollbackTarget
{
	private RollbackTarget( RollbackTargetKind kind, string release, IReadOnlyList<string> plans )
	{
		Kind = kind;
		Release = release;
		Plans = plans;
	}

	public RollbackTargetKind Kind { get; }

	/// <summary>The release left in place, for <see cref="RollbackTargetKind.ToRelease"/>; empty otherwise.</summary>
	public string Release { get; }

	/// <summary>The plans to undo, for <see cref="RollbackTargetKind.Plans"/>; empty otherwise.</summary>
	public IReadOnlyList<string> Plans { get; }

	/// <summary>Everything deployed after <paramref name="release"/>, which stays.</summary>
	public static RollbackTarget ToRelease( string release )
	{
		ArgumentException.ThrowIfNullOrEmpty( release );

		return new RollbackTarget( RollbackTargetKind.ToRelease, release, [ ] );
	}

	/// <summary>Everything deployed, back to the start of the lineage.</summary>
	public static RollbackTarget All { get; } = new( RollbackTargetKind.All, "", [ ] );

	/// <summary>These plans of the release the journal is at.</summary>
	public static RollbackTarget OfPlans( IEnumerable<string> plans )
	{
		ArgumentNullException.ThrowIfNull( plans );

		List<string> names = plans.Distinct( StringComparer.Ordinal ).ToList();

		if( names.Count == 0 )
			throw new ArgumentException( "At least one plan is needed.", nameof(plans) );

		return new RollbackTarget( RollbackTargetKind.Plans, "", names );
	}
}

public enum RollbackTargetKind
{
	ToRelease,
	All,
	Plans
}
