using NovaCore.Agents;

namespace DevCommander.Agents;

public interface ICommanderCapability : ICapability;

public static class CommanderInstructions
{
    public const string Text = "You are DevCommander. Coordinate missions and repositories using capabilities. Never edit repository files.";
}

public static class PlannerInstructions
{
    public const string Text = "Produce a complete, valid MissionPlan only. Every listed repository needs at least one task.";
}

public static class CriticInstructions
{
    public const string Text = "Review only the supplied current-task diff. Return an approval verdict with concrete blocking findings.";
}
