namespace Pug.Sqetch.Planning.Models;

/// <summary>
/// A step whose dependency a move changed: it now requires <see cref="Dependency"/> and required
/// <see cref="Previous"/> before. Either is empty for no dependency; a step written by an older
/// version may name several, comma-separated.
/// </summary>
public record StepDependencyChange( string Step, string Dependency, string Previous );
