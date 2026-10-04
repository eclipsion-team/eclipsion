using Content.Shared._Crescent.Audio;
using Content.Shared.GameTicking.Components;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Player;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// Plays <see cref="GreatHuntRuleComponent.GraceMusic"/> to both sides through the preparation phase. Each player is
/// sent the track once, as soon as they are in the round on one of the <see cref="GreatHuntRuleComponent.AnnouncedFactions"/>,
/// and the client keeps it looping over its biome, ship and combat music until the walls fall and it is told to stop.
/// </summary>
public sealed partial class GreatHuntRuleSystem
{
    [Dependency] private readonly IPlayerManager _players = default!;

    /// <summary>How often the server looks for players who joined, respawned or reconnected during preparation.</summary>
    private static readonly TimeSpan GraceMusicCheckInterval = TimeSpan.FromSeconds(1);

    private void InitializeMusic()
    {
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    /// <summary>A reconnecting client starts with no music, so forget it was sent the track and let the next check resend it.</summary>
    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus != SessionStatus.Disconnected)
            return;

        var query = QueryActiveRules();
        while (query.MoveNext(out _, out _, out var hunt, out _))
        {
            hunt.GraceMusicListeners.Remove(args.Session.UserId);
        }
    }

    protected override void Ended(EntityUid uid, GreatHuntRuleComponent hunt, GameRuleComponent gameRule,
        GameRuleEndedEvent args)
    {
        base.Ended(uid, hunt, gameRule, args);
        StopGraceMusic(hunt);
    }

    /// <summary>Sends the preparation theme to every player on either side who does not have it yet.</summary>
    private void UpdateGraceMusic(GreatHuntRuleComponent hunt, TimeSpan now)
    {
        if (hunt.GraceMusic is not { } music || now < hunt.NextGraceMusicCheck)
            return;

        hunt.NextGraceMusicCheck = now + GraceMusicCheckInterval;

        foreach (var session in _players.Sessions)
        {
            // Back in the lobby the client drops the track, so it has to be sent again once they are in a body.
            if (session.Status != SessionStatus.InGame || session.AttachedEntity == null)
            {
                hunt.GraceMusicListeners.Remove(session.UserId);
                continue;
            }

            if (hunt.GraceMusicListeners.Contains(session.UserId) ||
                GetSessionFaction(session) is not { } faction ||
                !hunt.AnnouncedFactions.Contains(faction))
            {
                continue;
            }

            hunt.GraceMusicListeners.Add(session.UserId);
            RaiseNetworkEvent(new AmbientMusicOverrideEvent(music), session);
        }
    }

    /// <summary>Hands everyone who was playing the preparation theme back to their usual music.</summary>
    private void StopGraceMusic(GreatHuntRuleComponent hunt)
    {
        if (hunt.GraceMusicListeners.Count == 0)
            return;

        var filter = Filter.Empty();
        foreach (var userId in hunt.GraceMusicListeners)
        {
            if (_players.TryGetSessionById(userId, out var session))
                filter.AddPlayer(session);
        }

        hunt.GraceMusicListeners.Clear();
        RaiseNetworkEvent(new AmbientMusicOverrideEvent(null), filter);
    }
}
