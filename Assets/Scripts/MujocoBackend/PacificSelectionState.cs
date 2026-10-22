public static class PacificSelectionState
{
    public static bool MujocoMode { get; private set; }
    // Legacy build: the OSS physics stays authoritative, but the scene still
    // spawns a full 7v7 roster so the P panel can switch between vehicles.
    public static bool LegacyRoster { get; private set; } = true;
    public static bool Completed { get; private set; }
    public static int SelectedDisplayRobotId { get; private set; } = -1;
    public static int SelectedFaction { get; private set; } = -1;
    public static int SelectedActualRobotId { get; private set; } = -1;

    public static void Reset()
    {
        Completed = false;
        SelectedDisplayRobotId = -1;
        SelectedFaction = -1;
        SelectedActualRobotId = -1;
    }

    public static void SetMujocoMode(bool enabled) => MujocoMode = enabled;

    public static void Complete(int displayRobotId, int faction)
    {
        Completed = true;
        SelectedDisplayRobotId = displayRobotId;
        SelectedFaction = faction;
        SelectedActualRobotId = faction == 1 ? displayRobotId + 20 : displayRobotId;
    }

    public static void CompleteActual(int actualRobotId, int faction)
    {
        Completed = true;
        SelectedActualRobotId = actualRobotId;
        SelectedDisplayRobotId = actualRobotId >= 20 ? actualRobotId - 20 : actualRobotId;
        SelectedFaction = faction;
    }

    public static bool TryGetSelection(out int displayRobotId, out int faction)
    {
        displayRobotId = SelectedDisplayRobotId;
        faction = SelectedFaction;
        return Completed && displayRobotId > 0 && (faction == 0 || faction == 1);
    }

    public static bool Matches(int actualRobotId, int faction)
    {
        return TryGetSelection(out _, out int selectedFaction) &&
               SelectedActualRobotId == actualRobotId && selectedFaction == faction;
    }
}
