using Multiplayer.Common;

namespace Tests;

[TestFixture]
public class CaravanEncounterRulesTest
{
    private const int Owner = 100;
    private const int Other = 200;
    private const int NoFaction = PauseDomainRules.NoFaction;

    // ---- Choice availability per kind ----

    [Test]
    public void Meeting_OffersTradeAttackMoveOn()
    {
        Assert.That(CaravanEncounterRules.ChoicesFor(EncounterKind.Meeting),
            Is.EquivalentTo(new[]
            {
                EncounterChoiceId.Trade, EncounterChoiceId.Attack, EncounterChoiceId.MoveOn,
            }));
    }

    [Test]
    public void Demand_OffersAcceptReject()
    {
        Assert.That(CaravanEncounterRules.ChoicesFor(EncounterKind.Demand),
            Is.EquivalentTo(new[]
            {
                EncounterChoiceId.AcceptDemand, EncounterChoiceId.RejectDemand,
            }));
    }

    [Test]
    public void Meeting_DoesNotOfferDemandChoices()
    {
        Assert.That(CaravanEncounterRules.IsChoiceAvailable(EncounterKind.Meeting, EncounterChoiceId.AcceptDemand),
            Is.False, "Answering a meeting with a demand outcome must not be possible");
    }

    [Test]
    public void Demand_DoesNotOfferMeetingChoices()
    {
        Assert.That(CaravanEncounterRules.IsChoiceAvailable(EncounterKind.Demand, EncounterChoiceId.Trade),
            Is.False, "Answering a demand with a meeting outcome must not be possible");
    }

    // ---- Timeout defaults ----

    [Test]
    public void AbandonedMeeting_DefaultsToMoveOn()
    {
        Assert.That(CaravanEncounterRules.DefaultChoiceFor(EncounterKind.Meeting),
            Is.EqualTo(EncounterChoiceId.MoveOn));
    }

    [Test]
    public void AbandonedDemand_DefaultsToRejectDemand()
    {
        Assert.That(CaravanEncounterRules.DefaultChoiceFor(EncounterKind.Demand),
            Is.EqualTo(EncounterChoiceId.RejectDemand),
            "A lapsed demand must not silently hand over colony goods on the player's behalf");
    }

    [Test]
    public void EveryDefaultChoiceIsActuallyAvailable()
    {
        foreach (EncounterKind kind in System.Enum.GetValues<EncounterKind>())
        {
            Assert.That(
                CaravanEncounterRules.IsChoiceAvailable(kind, CaravanEncounterRules.DefaultChoiceFor(kind)),
                Is.True, $"The timeout default for {kind} must be a choice that kind actually offers");
        }
    }

    // ---- Which states hold a pause ----

    [Test]
    public void PendingAndTransitioning_AssertPause()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CaravanEncounterRules.AssertsPause(EncounterState.Pending), Is.True);
            Assert.That(CaravanEncounterRules.AssertsPause(EncounterState.Transitioning), Is.True,
                "The handoff window must not contain an unpaused tick");
        });
    }

    [Test]
    public void ResolvedAndInvalidated_ReleasePause()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CaravanEncounterRules.AssertsPause(EncounterState.Resolved), Is.False);
            Assert.That(CaravanEncounterRules.AssertsPause(EncounterState.Invalidated), Is.False,
                "An invalidated encounter must not strand the pause on forever");
        });
    }

    // ---- Pause policy ----

    [Test]
    public void GlobalFallback_PausesWorldAndEveryMap()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CaravanEncounterRules.PolicyPauses(
                EncounterPausePolicy.GlobalFallback, isWorldTickable: true, isMapInOwnerDomain: false), Is.True);
            Assert.That(CaravanEncounterRules.PolicyPauses(
                EncounterPausePolicy.GlobalFallback, isWorldTickable: false, isMapInOwnerDomain: false), Is.True);
        });
    }

    [Test]
    public void OwnerFactionAssets_NeverFreezesTheWorldClock()
    {
        Assert.That(CaravanEncounterRules.PolicyPauses(
                EncounterPausePolicy.OwnerFactionAssets, isWorldTickable: true, isMapInOwnerDomain: false),
            Is.False,
            "Freezing the shared world tickable would stop every faction, defeating ownership scoping");
    }

    [Test]
    public void OwnerFactionAssets_PausesOnlyOwnerDomainMaps()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CaravanEncounterRules.PolicyPauses(
                EncounterPausePolicy.OwnerFactionAssets, isWorldTickable: false, isMapInOwnerDomain: true), Is.True);
            Assert.That(CaravanEncounterRules.PolicyPauses(
                EncounterPausePolicy.OwnerFactionAssets, isWorldTickable: false, isMapInOwnerDomain: false), Is.False);
        });
    }

    // ---- Timeout bounding ----

    [Test]
    public void EncounterInsideTimeout_HasNotTimedOut()
    {
        Assert.That(CaravanEncounterRules.HasTimedOut(createdAtTicks: 1000, nowTicks: 1999, timeoutTicks: 1000),
            Is.False);
    }

    [Test]
    public void EncounterAtExactlyTimeout_HasTimedOut()
    {
        Assert.That(CaravanEncounterRules.HasTimedOut(createdAtTicks: 1000, nowTicks: 2000, timeoutTicks: 1000),
            Is.True, "The bound is inclusive so the fire tick is unambiguous across clients");
    }

    [Test]
    public void NonPositiveTimeout_NeverFires()
    {
        Assert.That(CaravanEncounterRules.HasTimedOut(createdAtTicks: 0, nowTicks: int.MaxValue, timeoutTicks: 0),
            Is.False, "A disabled timeout must not fire immediately");
    }

    [Test]
    public void OnlyGlobalFallbackNeedsATimeout()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CaravanEncounterRules.NeedsTimeout(EncounterPausePolicy.GlobalFallback), Is.True,
                "A global pause held by an absent owner stalls every faction, so it must be bounded");
            Assert.That(CaravanEncounterRules.NeedsTimeout(EncounterPausePolicy.OwnerFactionAssets), Is.False,
                "An ownership-scoped pause only blocks its owner and can wait indefinitely");
        });
    }

    // ---- Capability detection ----

    [Test]
    public void AllCapabilitiesPresent_SelectsOwnerFactionAssets()
    {
        Assert.That(CaravanEncounterRules.SelectPausePolicy(
                asyncTimeEnabled: true, multifactionEnabled: true, worldTickGateAvailable: true),
            Is.EqualTo(EncounterPausePolicy.OwnerFactionAssets));
    }

    [Test]
    public void SynchronizedTime_FallsBackToGlobal()
    {
        Assert.That(CaravanEncounterRules.SelectPausePolicy(
                asyncTimeEnabled: false, multifactionEnabled: true, worldTickGateAvailable: true),
            Is.EqualTo(EncounterPausePolicy.GlobalFallback),
            "With one shared rate an ownership-scoped pause still stops everyone, so claiming isolation would be false");
    }

    [Test]
    public void SingleFaction_FallsBackToGlobal()
    {
        Assert.That(CaravanEncounterRules.SelectPausePolicy(
                asyncTimeEnabled: true, multifactionEnabled: false, worldTickGateAvailable: true),
            Is.EqualTo(EncounterPausePolicy.GlobalFallback),
            "With nobody to isolate from, a global pause is simpler and closer to vanilla");
    }

    [Test]
    public void NoWorldTickGate_FallsBackToGlobal()
    {
        Assert.That(CaravanEncounterRules.SelectPausePolicy(
                asyncTimeEnabled: true, multifactionEnabled: true, worldTickGateAvailable: false),
            Is.EqualTo(EncounterPausePolicy.GlobalFallback),
            "A partial freeze, with the owner's caravans still foraging, is worse than an honest global one");
    }

    [Test]
    public void SynchronizedTimeIsTheDefault_AndAlwaysGetsATimeout()
    {
        // The default server has asyncTime off, so it always lands on GlobalFallback -- and GlobalFallback
        // is exactly the policy that must be timeout-bounded. This keeps those two facts tied together.
        var policy = CaravanEncounterRules.SelectPausePolicy(
            asyncTimeEnabled: false, multifactionEnabled: true, worldTickGateAvailable: true);

        Assert.That(CaravanEncounterRules.NeedsTimeout(policy), Is.True);
    }

    // ---- Choice authority and concurrency ----

    private static EncounterChoiceVerdict Evaluate(
        int commandFaction = Owner,
        int ownerFaction = Owner,
        int expectedRevision = 0,
        int currentRevision = 0,
        EncounterState state = EncounterState.Pending,
        EncounterKind kind = EncounterKind.Meeting,
        EncounterChoiceId choice = EncounterChoiceId.MoveOn)
        => CaravanEncounterRules.EvaluateChoice(
            commandFaction, ownerFaction, expectedRevision, currentRevision, state, kind, choice);

    [Test]
    public void OwnerAtCurrentRevision_IsAccepted()
    {
        Assert.That(Evaluate(), Is.EqualTo(EncounterChoiceVerdict.Accept));
    }

    [Test]
    public void NonOwnerFaction_IsRejected()
    {
        Assert.That(Evaluate(commandFaction: Other), Is.EqualTo(EncounterChoiceVerdict.WrongFaction),
            "A forged choice from another faction must not mutate the session");
    }

    [Test]
    public void OwnerlessSession_AcceptsNothing()
    {
        Assert.That(Evaluate(commandFaction: NoFaction, ownerFaction: NoFaction),
            Is.EqualTo(EncounterChoiceVerdict.WrongFaction),
            "NoFaction must not match NoFaction, or an unowned session would accept anyone's command");
    }

    [Test]
    public void StaleRevision_IsRejected()
    {
        Assert.That(Evaluate(expectedRevision: 0, currentRevision: 1),
            Is.EqualTo(EncounterChoiceVerdict.StaleRevision),
            "The second of two same-faction clicks must be a no-op, not a second outcome");
    }

    [Test]
    public void AlreadyTransitioning_IsRejected()
    {
        Assert.That(Evaluate(state: EncounterState.Transitioning),
            Is.EqualTo(EncounterChoiceVerdict.NotPending));
    }

    [Test]
    public void ChoiceFromTheOtherKind_IsRejected()
    {
        Assert.That(Evaluate(kind: EncounterKind.Demand, choice: EncounterChoiceId.Trade),
            Is.EqualTo(EncounterChoiceVerdict.ChoiceNotAvailable));
    }

    [Test]
    public void FactionIsCheckedBeforeRevision()
    {
        // A non-owner sending a stale revision should be reported as the more serious problem, so an
        // authority failure is never masked as a benign race in the logs.
        Assert.That(Evaluate(commandFaction: Other, expectedRevision: 0, currentRevision: 5),
            Is.EqualTo(EncounterChoiceVerdict.WrongFaction));
    }
}
