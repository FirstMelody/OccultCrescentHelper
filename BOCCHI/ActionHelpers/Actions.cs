using FFXIVClientStructs.FFXIV.Client.Game;

namespace BOCCHI.ActionHelpers;

public static partial class Actions
{
    public static Action Sprint { get; private set; } = new(ActionType.GeneralAction, 4);

    public static Action Return { get; private set; } = new(ActionType.GeneralAction, 8);

    public static class Tank
    {
        public static Action ShieldLob { get; private set; } =
            new(ActionType.Action, 24);

        public static Action Tomahawk { get; private set; } =
            new(ActionType.Action, 46);

        public static Action Unmend { get; private set; } =
            new(ActionType.Action, 3624);

        public static Action LightningShot { get; private set; } =
            new(ActionType.Action, 16143);
    }
}
