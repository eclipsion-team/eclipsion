using Content.Shared.Abilities.Psionics;
using Content.Shared.Popups;
using Robust.Shared.Network;

namespace Content.Shared.Crescent.Psionics;

/// <summary>
/// The shared half of the null field: refusing powers to psions caught inside one. Finding who is inside, and
/// everything the field does to the world, is the server's <c>PsionicNullifierSystem</c>.
/// </summary>
public sealed class PsionicNullifiedSystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PsionicallyNullifiedComponent, OnAttemptPowerUseEvent>(OnAttemptPowerUse);
    }

    private void OnAttemptPowerUse(EntityUid uid, PsionicallyNullifiedComponent component, OnAttemptPowerUseEvent args)
    {
        args.Cancel();

        // The marker is networked, so a predicting client refuses the cast too; only the server speaks up.
        if (_net.IsServer)
            _popup.PopupEntity(Loc.GetString("psionic-nullified-cast-fail"), uid, uid, PopupType.MediumCaution);
    }

    /// <summary>
    /// For anything about to strip insulation it lent out. A null field needs the insulation underneath it, so
    /// this returns true while the wearer has one; when that field comes from gear, the gear takes the
    /// insulation over and strips it itself when it comes off.
    /// </summary>
    public bool ClaimsInsulation(EntityUid wearer)
    {
        if (!TryComp<PsionicNullifierComponent>(wearer, out var nullifier))
            return false;

        if (nullifier.FromClothing && TryComp<PsionicNullifierClothingComponent>(nullifier.Source, out var clothing))
            clothing.GrantedInsulation = true;

        return true;
    }
}
