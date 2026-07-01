namespace SmartReviewSystem.Services.DevOps;

internal static class OrchestratorPhaseProgressHelper
{
    public class PhaseStage
    {
        public int StageNumber { get; set; }
        public string StageName { get; set; } = string.Empty;
        public string PhaseReached { get; set; } = string.Empty;
        public int Progress { get; set; }
    }

    /// <summary>
    /// Master ordered list of all orchestrator phases in sequence.
    /// This defines the workflow order without progress values.
    /// </summary>
    private static readonly List<string> OrderedPhases = new()
    {
        "Cloning Repos",
        "Generating Mockup",
        "Awaiting Mockup Review",
        "Generating Plan",
        "Awaiting Plan Review",
        "Backend Planning",
        "Backend Implementing",
        "Backend QA",
        "UI Planning",
        "UI Implementing",
        "UI QA",
        "Inserting Messages",
        "QA Validation",
        "Full QA",
        "Awaiting QA Verification",
        "Creating PRs",
        "Awaiting PR Review",
        "Knowledge Update",
        "Complete",
        "Error"
    };

    /// <summary>
    /// Milestone phases with their associated progress percentages.
    /// Only major checkpoints are defined here; other phases inherit from the last known milestone.
    /// </summary>
    private static readonly Dictionary<string, int> PhaseProgressMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Cloning Repos", 2 },
        { "Generating Mockup", 5 },
        { "Awaiting Mockup Review", 8 },
        { "Generating Plan", 12 },
        { "Awaiting Plan Review", 15 },
        { "Backend Planning", 20 },
        { "Backend Implementing", 35 },
        { "Backend QA", 45 },
        { "UI Planning", 55 },
        { "UI Implementing", 65 },
        { "UI QA", 75 },
        { "Inserting Messages", 80 },
        { "QA Validation", 85 },
        { "Full QA", 90 },
        { "Awaiting QA Verification", 94 },
        { "Creating PRs", 96 },
        { "Awaiting PR Review", 98 },
        { "Knowledge Update", 99 },
        { "Complete", 100 },
        { "Error", 0 }
    };

    private static readonly List<PhaseStage> PhaseStages = new()
    {
        new PhaseStage { StageNumber = 1, StageName = "Implementation Started", PhaseReached = "Cloning Repos", Progress = 2 },
        new PhaseStage { StageNumber = 2, StageName = "Repos Cloned", PhaseReached = "Generating Mockup", Progress = 5 },
        new PhaseStage { StageNumber = 3, StageName = "Mockup Generated", PhaseReached = "Awaiting Mockup Review", Progress = 8 },
        new PhaseStage { StageNumber = 4, StageName = "Mockup Review Complete", PhaseReached = "Generating Plan", Progress = 12 },
        new PhaseStage { StageNumber = 5, StageName = "Plan Generated", PhaseReached = "Awaiting Plan Review", Progress = 15 },
        new PhaseStage { StageNumber = 6, StageName = "Plan Review Complete", PhaseReached = "Backend Planning", Progress = 20 },
        new PhaseStage { StageNumber = 7, StageName = "Backend Plan Created", PhaseReached = "Backend Implementing", Progress = 35 },
        new PhaseStage { StageNumber = 8, StageName = "Backend Implementation Complete", PhaseReached = "Backend QA", Progress = 45 },
        new PhaseStage { StageNumber = 9, StageName = "Backend QA Complete", PhaseReached = "UI Planning", Progress = 55 },
        new PhaseStage { StageNumber = 10, StageName = "UI Plan Created", PhaseReached = "UI Implementing", Progress = 65 },
        new PhaseStage { StageNumber = 11, StageName = "UI Implementation Complete", PhaseReached = "UI QA", Progress = 75 },
        new PhaseStage { StageNumber = 12, StageName = "UI QA Complete", PhaseReached = "Inserting Messages", Progress = 80 },
        new PhaseStage { StageNumber = 13, StageName = "Messages Inserted", PhaseReached = "QA Validation", Progress = 85 },
        new PhaseStage { StageNumber = 14, StageName = "QA Validation Complete", PhaseReached = "Full QA", Progress = 90 },
        new PhaseStage { StageNumber = 15, StageName = "Full QA Complete", PhaseReached = "Awaiting QA Verification", Progress = 94 },
        new PhaseStage { StageNumber = 16, StageName = "QA Verification Complete", PhaseReached = "Creating PRs", Progress = 96 },
        new PhaseStage { StageNumber = 17, StageName = "PRs Created", PhaseReached = "Awaiting PR Review", Progress = 98 },
        new PhaseStage { StageNumber = 18, StageName = "Knowledge Update", PhaseReached = "Knowledge Update", Progress = 99 },
        new PhaseStage { StageNumber = 19, StageName = "Implementation Complete", PhaseReached = "Complete", Progress = 100 },
        new PhaseStage { StageNumber = 20, StageName = "Error Encountered", PhaseReached = "Error", Progress = 0 }
    };

    /// <summary>
    /// Gets the progress percentage for a given orchestrator phase.
    /// If the phase is an exact milestone, returns its progress.
    /// Otherwise, returns the progress of the last known milestone before this phase in the workflow.
    /// For "Error" phase, returns the progress of the last milestone before the error occurred.
    /// </summary>
    /// <param name="phase">The orchestrator phase name.</param>
    /// <returns>The progress percentage (0-100), or 0 if the phase is not recognized or comes before the first milestone.</returns>
    public static int GetProgressPercentage(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            return 0;
        }

        // Check if this is an exact milestone phase
        if (PhaseProgressMap.TryGetValue(phase, out var progress))
        {
            return progress;
        }

        // Find the current phase position in the ordered list
        var currentIndex = OrderedPhases.FindIndex(p =>
            p.Equals(phase, StringComparison.OrdinalIgnoreCase));

        if (currentIndex == -1)
        {
            return 0;  // Phase not recognized
        }

        // Walk backwards until a milestone is found
        for (int i = currentIndex - 1; i >= 0; i--)
        {
            if (PhaseProgressMap.TryGetValue(OrderedPhases[i], out progress))
            {
                return progress;
            }
        }

        return 0;  // No milestone found before this phase
    }


    /// <summary>
    /// Gets the stage information for a given orchestrator phase.
    /// </summary>
    /// <param name="phase">The orchestrator phase name.</param>
    /// <returns>The PhaseStage information, or null if the phase is not recognized.</returns>
    public static PhaseStage? GetPhaseStageInfo(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            return null;
        }

        return PhaseStages.FirstOrDefault(s => s.PhaseReached.Equals(phase, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets all recognized orchestrator phases in order.
    /// </summary>
    public static IReadOnlyList<string> GetAllOrderedPhases() => OrderedPhases.AsReadOnly();

    /// <summary>
    /// Determines if the given phase is an error phase.
    /// </summary>
    /// <param name="phase">The orchestrator phase name.</param>
    /// <returns>True if the phase is "Error"; otherwise, false.</returns>
    public static bool IsErrorPhase(string? phase)
    {
        return !string.IsNullOrWhiteSpace(phase) && phase.Equals("Error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets all milestone phases and their progress percentages.
    /// </summary>
    public static IReadOnlyDictionary<string, int> GetAllMilestones() => new Dictionary<string, int>(PhaseProgressMap);

    /// <summary>
    /// Gets all phase stages.
    /// </summary>
    public static IReadOnlyList<PhaseStage> GetAllPhaseStages() => PhaseStages.AsReadOnly();

    /// <summary>
    /// Gets all recognized orchestrator phases and their progress percentages (for backward compatibility).
    /// </summary>
    [Obsolete("Use GetAllMilestones() instead. This method does not include non-milestone phases.")]
    public static IReadOnlyDictionary<string, int> GetAllPhases() => new Dictionary<string, int>(PhaseProgressMap);
}
