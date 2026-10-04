using Content.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Audio;

/// <summary>
/// Sent to a client to force an ambient music track regardless of biome, vessel or combat mode, such as a game mode's
/// preparation theme. The track loops on the ambient slider until another one of these arrives with a null
/// <see cref="Music"/>, which hands the music back to the biome/vessel logic.
/// </summary>
[Serializable, NetSerializable]
public sealed class AmbientMusicOverrideEvent : EntityEventArgs
{
    public ProtoId<AmbientMusicPrototype>? Music;

    public AmbientMusicOverrideEvent(ProtoId<AmbientMusicPrototype>? music)
    {
        Music = music;
    }
}
