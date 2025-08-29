using System.Collections.Generic;

namespace Explore.Build
{
	public readonly record struct CreateDir(string RelPath);
	public readonly record struct RenameDir(string OldRelPath, string NewRelPath);
	public readonly record struct MoveItem(string SourceFullPath, string DestFullPath, string Reason); // "classified"|"fallback"
	public readonly record struct PlanError(string SourceFullPath, string Reason);
	public readonly record struct BuildPlanStats(int CreateCount, int RenameCount, int MoveCount, int UnresolvedCount, int ErrorCount);

	public sealed record BuildPlan(
	  IReadOnlyList<CreateDir> CreateDirs,
	  IReadOnlyList<RenameDir> RenameDirs,
	  IReadOnlyList<MoveItem> Moves,
	  IReadOnlyList<string> Unresolved,
	  IReadOnlyList<PlanError> Errors,
	  BuildPlanStats Stats
	);

	public readonly record struct ApplyProgress(string Phase, int Done, int Total, string? Current, int Errors);
	// Phase: "CreateDirs" -> "RenameDirs" -> "MoveItems" -> "UpdateDb" -> "Done"
}
