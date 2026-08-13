using Multiplayer.Common;

namespace Tests;

[TestFixture]
public class PauseDomainRulesTest
{
    private const int OwnerFaction = 100;
    private const int OtherFaction = 200;
    private const int NoFaction = PauseDomainRules.NoFaction;

    private static bool IsInDomain(int mapParentFactionId, int[]? humanlikePawnFactionIds = null, int owner = OwnerFaction)
        => PauseDomainRules.IsMapInPauseDomain(mapParentFactionId, humanlikePawnFactionIds, owner);

    // ---- Clause 1: the owner's own colony ----

    [Test]
    public void OwnerOwnedMap_IsInDomain()
    {
        Assert.That(IsInDomain(OwnerFaction), Is.True,
            "A map whose parent faction is the owner is the owner's own colony and must pause");
    }

    [Test]
    public void OtherFactionMap_WithNoOwnerPawns_IsNotInDomain()
    {
        Assert.That(IsInDomain(OtherFaction, [OtherFaction]), Is.False,
            "Another faction's colony must keep running when the owner is blocked");
    }

    // ---- Clause 2: shared or neutral maps the owner occupies ----

    [Test]
    public void SharedMap_WithOwnerHumanlikePawn_IsInDomain()
    {
        Assert.That(IsInDomain(OtherFaction, [OtherFaction, OwnerFaction]), Is.True,
            "A map shared with the owner pauses in full: the tick scheduler is map-granular");
    }

    [Test]
    public void NeutralMap_WithOwnerHumanlikePawn_IsInDomain()
    {
        Assert.That(IsInDomain(NoFaction, [OwnerFaction]), Is.True,
            "An unowned map the owner occupies is still in the owner's domain");
    }

    [Test]
    public void NeutralMap_WithNoPawns_IsNotInDomain()
    {
        Assert.That(IsInDomain(NoFaction, []), Is.False,
            "An unowned, unoccupied map has no reason to pause");
    }

    // ---- The humanlike filter is the caller's job, but the rule must honour an empty list ----

    [Test]
    public void SharedMap_WithoutOwnerPawns_IsNotInDomain()
    {
        Assert.That(IsInDomain(OtherFaction, [OtherFaction, 300]), Is.False,
            "Other factions occupying a map does not put it in the owner's domain");
    }

    // ---- Degenerate inputs ----

    [Test]
    public void NullPawnList_IsHandled()
    {
        Assert.That(IsInDomain(OtherFaction, null), Is.False,
            "A null pawn list must not throw; it means no occupation was observed");
    }

    [Test]
    public void NoOwnerFaction_PausesNothing()
    {
        Assert.That(
            PauseDomainRules.IsMapInPauseDomain(NoFaction, [NoFaction], NoFaction), Is.False,
            "An ownerless session must pause nothing; matching NoFaction against NoFaction would freeze every map");
    }

    [Test]
    public void NoOwnerFaction_DoesNotMatchOwnedMap()
    {
        Assert.That(
            PauseDomainRules.IsMapInPauseDomain(OwnerFaction, [OwnerFaction], NoFaction), Is.False,
            "An ownerless session must pause nothing even when real factions are present");
    }
}
