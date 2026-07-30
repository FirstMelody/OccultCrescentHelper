using System;
using BOCCHI.ActionHelpers;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;

namespace BOCCHI.Modules.NorthernRoutes;

public sealed class NorthernReturnState
{
    private static readonly TimeSpan RetryInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TransferGracePeriod =
        TimeSpan.FromSeconds(2);

    private bool existingCastFinished;
    private bool returnRequested;
    private bool returnCastObserved;
    private bool betweenAreasObserved;
    private DateTime returnCastEndedAt = DateTime.MinValue;
    private DateTime nextReturnAttemptAt = DateTime.MinValue;
    private readonly NorthernRoutePlanner planner;
    private readonly int cancellationGeneration;

    public bool Completed { get; private set; }
    public bool SkippedNearSource { get; private set; }
    public bool Canceled { get; private set; }

    public NorthernReturnState(NorthernRoutePlanner planner)
    {
        this.planner = planner;
        cancellationGeneration = planner.BeginReturn();
    }

    public bool Update()
    {
        if (Completed)
        {
            return true;
        }

        if (planner.IsReturnCanceled(cancellationGeneration))
        {
            Canceled = true;
            Complete();
            return true;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas])
        {
            if (!betweenAreasObserved)
            {
                DebugLog.Debug(
                    "Northern Return entered BetweenAreas; waiting for load"
                );
            }

            betweenAreasObserved = true;
            return false;
        }

        if (betweenAreasObserved)
        {
            DebugLog.Debug(
                "Northern Return completed zone transfer"
            );
            Complete();
            return true;
        }

        if (!returnRequested
            && planner.IsNearSourceCrystal(
                Svc.ClientState.TerritoryType
            ))
        {
            SkippedNearSource = true;
            Complete();
            return true;
        }

        if (!existingCastFinished)
        {
            if (Svc.Condition[ConditionFlag.Casting])
            {
                return false;
            }

            existingCastFinished = true;
        }

        if (returnCastObserved)
        {
            if (Svc.Condition[ConditionFlag.Casting])
            {
                return false;
            }

            if (returnCastEndedAt == DateTime.MinValue)
            {
                returnCastEndedAt = DateTime.UtcNow;
                return false;
            }

            if (DateTime.UtcNow - returnCastEndedAt < TransferGracePeriod)
            {
                return false;
            }

            DebugLog.Debug(
                "Northern Return cast ended without zone transfer; retrying"
            );
            returnRequested = false;
            returnCastObserved = false;
            returnCastEndedAt = DateTime.MinValue;
        }

        if (returnRequested
            && Svc.Condition[ConditionFlag.Casting])
        {
            DebugLog.Debug(
                "Northern Return cast started; blocking pathfinding"
            );
            returnCastObserved = true;
            return false;
        }

        if (DateTime.UtcNow >= nextReturnAttemptAt)
        {
            Actions.Return.Cast();
            returnRequested = true;
            nextReturnAttemptAt =
                DateTime.UtcNow + RetryInterval;
        }

        return false;
    }

    private void Complete()
    {
        Completed = true;
        planner.EndReturn();
    }
}
