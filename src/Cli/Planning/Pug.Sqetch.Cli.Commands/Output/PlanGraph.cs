using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Cli.Commands.Authoring.Output;

/// <summary>A plan's dependency as the graph shows it: the plan it names and that plan's release.</summary>
internal sealed record GraphDependency( string Name, string Release );

internal sealed record GraphPlan( string Name, IReadOnlyList<GraphDependency> Dependencies );

/// <summary>The plans of one release in deployment order; <see cref="Release"/> is empty for the unreleased plans.</summary>
internal sealed record GraphGroup( string Release, IReadOnlyList<GraphPlan> Plans );

/// <summary>
/// The plan dependency graph 'sqetch plan graph' prints: groups of plans in lineage order, and the
/// plans they depend on from outside the selection (<see cref="External"/>), each listed once. Edges
/// run from a dependency to its dependant, the order plans deploy in.
/// </summary>
internal sealed record PlanGraph( IReadOnlyList<GraphGroup> Groups, IReadOnlyList<GraphDependency> External )
{
	/// <summary>
	/// Builds the graph of <paramref name="groups"/>, given in the order the engine returned them.
	/// A dependency outside the selection is looked up through <paramref name="dependenciesOf"/>,
	/// which is asked only for plans that have one.
	/// </summary>
	public static PlanGraph Build(
		IEnumerable<(string Release, IReadOnlyList<ProjectPlan> Plans)> groups,
		Func<string, IEnumerable<ProjectPlan>> dependenciesOf
	)
	{
		List<(string Release, IReadOnlyList<ProjectPlan> Plans)> selection = groups.ToList();

		Dictionary<string, ProjectPlan> selected = new( StringComparer.OrdinalIgnoreCase );

		foreach( ProjectPlan plan in selection.SelectMany( x => x.Plans ) )
			selected[plan.Definition.Name] = plan;

		List<GraphDependency> external = [ ];
		HashSet<string> externalNames = new( StringComparer.OrdinalIgnoreCase );

		List<GraphGroup> graphGroups = [ ];

		foreach( (string release, IReadOnlyList<ProjectPlan> plans) in selection )
		{
			List<GraphPlan> graphPlans = [ ];

			foreach( ProjectPlan plan in plans )
			{
				ICollection<string> declared =( plan.Definition as PlanDefinition )?.Dependencies ?? [ ];

				Dictionary<string, ProjectPlan>? outside = declared.All( selected.ContainsKey ) ?
					null :
					dependenciesOf( plan.Definition.Name )
						.ToDictionary( x => x.Definition.Name, StringComparer.OrdinalIgnoreCase );

				List<GraphDependency> dependencies = [ ];

				foreach( string name in declared )
				{
					if( selected.TryGetValue( name, out ProjectPlan? inside ) )
					{
						dependencies.Add( new GraphDependency( inside.Definition.Name, inside.Release ) );

						continue;
					}

					GraphDependency dependency = outside!.TryGetValue( name, out ProjectPlan? found ) ?
						new GraphDependency( found.Definition.Name, found.Release ) :
						new GraphDependency( name, "" );

					dependencies.Add( dependency );

					if( externalNames.Add( dependency.Name ) )
						external.Add( dependency );
				}

				graphPlans.Add( new GraphPlan( plan.Definition.Name, dependencies ) );
			}

			graphGroups.Add( new GraphGroup( release, graphPlans ) );
		}

		return new PlanGraph( graphGroups, external );
	}

	private static string ReleaseLabel( string release ) => release.Length == 0 ? "(unreleased)" : release;

	private static string DependencyLabel( GraphDependency dependency )
		=> $"{dependency.Name} ({( dependency.Release.Length == 0 ? "unreleased" : dependency.Release )})";

	/// <summary>
	/// One block per group, blank lines between them; each plan on its own line, with every
	/// dependency's release named, so a dependency on an earlier release stands out.
	/// </summary>
	public IReadOnlyList<string> Text()
	{
		List<string> lines = [ ];

		foreach( GraphGroup group in Groups )
		{
			if( lines.Count > 0 )
				lines.Add( "" );

			lines.Add( group.Release.Length == 0 ? "Unreleased" : $"Release {group.Release}" );

			foreach( GraphPlan plan in group.Plans )
				lines.Add(
					plan.Dependencies.Count == 0 ?
						$"  {plan.Name}" :
						$"  {plan.Name}: requires {string.Join( ", ", plan.Dependencies.Select( DependencyLabel ) )}"
				);
		}

		return lines;
	}

	/// <summary>
	/// A Mermaid flowchart. Names may hold characters Mermaid ids cannot (<c>-+()@#.</c>), so nodes
	/// get generated ids and the names go in quoted labels; names hold no <c>"</c> or <c>;</c>, so a
	/// label needs no escaping and no <c>#…;</c> entity can form in one.
	/// </summary>
	public IReadOnlyList<string> Mermaid()
	{
		List<string> lines = [ "flowchart TD" ];
		Dictionary<string, string> ids = new( StringComparer.OrdinalIgnoreCase );

		int release = 0;

		foreach( GraphGroup group in Groups )
		{
			lines.Add(
				group.Release.Length == 0 ?
					$"    subgraph u[\"{ReleaseLabel( group.Release )}\"]" :
					$"    subgraph r{++release}[\"{group.Release}\"]"
			);

			foreach( GraphPlan plan in group.Plans )
			{
				string id = $"p{ids.Count + 1}";

				ids[plan.Name] = id;
				lines.Add( $"        {id}[\"{plan.Name}\"]" );
			}

			lines.Add( "    end" );
		}

		int externalCount = 0;

		foreach( GraphDependency dependency in External )
		{
			string id = $"x{++externalCount}";

			ids[dependency.Name] = id;
			lines.Add( $"    {id}[\"{DependencyLabel( dependency )}\"]:::external" );
		}

		foreach( (string from, string to) in Edges() )
			lines.Add( $"    {ids[from]} --> {ids[to]}" );

		if( External.Count > 0 )
			lines.Add( "    classDef external stroke-dasharray: 5 5" );

		return lines;
	}

	/// <summary>
	/// A Graphviz digraph, one cluster per group. Names hold no <c>"</c> or <c>\</c>, so quoted
	/// they are the node ids as they are.
	/// </summary>
	public IReadOnlyList<string> Dot()
	{
		List<string> lines = [ "digraph plans {" ];

		int cluster = 0;

		foreach( GraphGroup group in Groups )
		{
			lines.Add( $"    subgraph \"cluster_{++cluster}\" {{" );
			lines.Add( $"        label=\"{ReleaseLabel( group.Release )}\";" );

			foreach( GraphPlan plan in group.Plans )
				lines.Add( $"        \"{plan.Name}\";" );

			lines.Add( "    }" );
		}

		foreach( GraphDependency dependency in External )
			lines.Add( $"    \"{dependency.Name}\" [label=\"{DependencyLabel( dependency )}\", style=dashed];" );

		foreach( (string from, string to) in Edges() )
			lines.Add( $"    \"{from}\" -> \"{to}\";" );

		lines.Add( "}" );

		return lines;
	}

	private IEnumerable<(string From, string To)> Edges()
		=> Groups.SelectMany( x => x.Plans )
			.SelectMany( plan => plan.Dependencies.Select( dependency => ( dependency.Name, plan.Name ) ) );
}
