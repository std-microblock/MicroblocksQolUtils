using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

internal enum PlayerLiveState {
    Alive,          // player present and playable
    Dying,          // death registered; dead body animating / death cutscene
    Respawning,     // post-death reload / load: respawn wipe-in / StIntroRespawn, until playable
    EnteringLevel,  // entering a level / room (chapter start, transition, rebuild), until playable
}

internal readonly record struct PlayerLiveSnapshot(
    PlayerLiveState State,
    bool EnteredDying,
    bool LeftDying,
    bool EnteredRespawning,
    bool EnteredAlive,
    bool PlayerAlive,
    bool PlayerDead,
    bool PlayerControllable,
    bool InCutscene,
    PlayerDeadBody? DeadBody);

internal static class PlayerLifecycle {
    private static PlayerLiveState state = PlayerLiveState.Alive;

    private static bool playerAlive, playerDead, playerControllable, inCutscene;
    private static PlayerDeadBody? deadBody;

    internal static PlayerLiveState State => state;
    internal static bool InDeathSequence => state == PlayerLiveState.Dying;
    internal static PlayerLiveSnapshot Latest { get; private set; }
    internal static ulong DeathEpoch { get; private set; }

    internal static PlayerLiveSnapshot NotifyDeath() {
        DeathEpoch++;
        return Enter(PlayerLiveState.Dying);
    }

    internal static PlayerLiveSnapshot Rewind() =>
        Enter(state == PlayerLiveState.Dying ? PlayerLiveState.Alive : state);

    internal static PlayerLiveSnapshot ResetRun() {
        playerAlive = playerDead = playerControllable = inCutscene = false;
        deadBody = null;
        return Enter(PlayerLiveState.EnteringLevel);
    }

    private static PlayerLiveSnapshot Enter(PlayerLiveState to) {
        PlayerLiveState before = state;
        state = to;
        return Build(before);
    }

    internal static PlayerLiveSnapshot Advance(Level? level) {
        Player? playerNow = level?.Tracker.GetEntity<Player>();
        PlayerDeadBody? deadBodyNow = level?.Entities.FindFirst<PlayerDeadBody>();
        bool aliveNow = playerNow is { Dead: false };
        bool deadNow = playerNow is { Dead: true };
        bool cutsceneNow = level is not null && (level.InCutscene || level.SkippingCutscene);
        bool controllableNow = level is not null && IsControllable(level, playerNow);

        PlayerLiveState before = state;
        if (state == PlayerLiveState.Dying && (level is null || DeathSequenceSettled(level, deadBodyNow)))
            state = PlayerLiveState.Respawning;
        else if (state is PlayerLiveState.Respawning or PlayerLiveState.EnteringLevel && controllableNow)
            state = PlayerLiveState.Alive;

        deadBody = deadBodyNow;
        playerAlive = aliveNow;
        playerDead = deadNow;
        playerControllable = controllableNow;
        inCutscene = cutsceneNow;
        return Build(before);
    }

    private static PlayerLiveSnapshot Build(PlayerLiveState before) {
        PlayerLiveSnapshot snapshot = new(
            State: state,
            EnteredDying: before != PlayerLiveState.Dying && state == PlayerLiveState.Dying,
            LeftDying: before == PlayerLiveState.Dying && state != PlayerLiveState.Dying,
            EnteredRespawning: before != PlayerLiveState.Respawning && state == PlayerLiveState.Respawning,
            EnteredAlive: before != PlayerLiveState.Alive && state == PlayerLiveState.Alive,
            PlayerAlive: playerAlive,
            PlayerDead: playerDead,
            PlayerControllable: playerControllable,
            InCutscene: inCutscene,
            DeadBody: deadBody);
        Latest = snapshot;
        return snapshot;
    }

    internal static bool IsControllable(Level level, Player? candidate) =>
        candidate is { Dead: false }
        && !level.Transitioning
        && candidate.StateMachine.State != Player.StIntroRespawn;

    internal static bool IsAlive(Player? candidate) => candidate is { Dead: false };

    private static bool DeathSequenceSettled(Level level, PlayerDeadBody? deadBody) {
        if (deadBody is null && level.Tracker.GetEntity<Player>() is { Dead: false }) return true;
        if (level.InCutscene || level.SkippingCutscene) return false;
        if (deadBody is not null || level.Transitioning) return false;
        if (level.Wipe is ScreenWipe wipe) return wipe.WipeIn;
        return false;
    }
}
