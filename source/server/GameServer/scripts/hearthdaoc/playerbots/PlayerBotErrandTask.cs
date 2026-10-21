using System;
using System.Numerics;

namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC #137 spike: runs PlayerBotErrands (quest 20206, then promotion) as the driver's task. Walk: the driver's
// mover walks to the NPC; while a monster hits the bot the driver fights that one (and seeks no other), and a bot low
// after a fight rests before it walks on. Level: the driver grinds. Done or Fail: the task ends (finished(true) only for
// Done; the spawner then logs the bot out and back in, and checks F3 and F4). Anything else (whisper, accept, interact,
// give, equip, wait): the bot stands this think.
public sealed class PlayerBotErrandTask : IPlayerBotTask
{
    private readonly PlayerBotErrands _errands;
    private readonly Action<bool> _finished;
    private bool _reported;

    public PlayerBotErrandTask(PlayerBotErrands errands, Action<bool> finished)
    {
        _errands = errands ?? throw new ArgumentNullException(nameof(errands));
        _finished = finished;
    }

    public bool Finished => _reported;

    public bool Tick(PlayerBotState state, long now)
    {
        if (_reported) return false;
        GamePlayer player = state.Player;
        if (player.IsAlive && !Talking(player) && PlayerBotDriver.UnderAttack(state, now))
            return false;                                             // defend first: the driver fights this think
        ErrandAction action = _errands.Think(now);
        switch (action.Kind)
        {
            case ErrandActionKind.Walk:
                if (PlayerBotDriver.WantsRest(player))
                {
                    if (!player.InCombat) return false;               // the driver sits (its Think step 11) until full
                    state.Mover.Stop(now);                            // in combat up to 10 s after the last swing
                    return true;                                      // (GameLiving.cs:31): stand, seek no fight
                }
                GameNPC npc = _errands.Npc(action.Target);
                if (npc == null) { state.Mover.Stop(now); return true; }
                state.Mover.StepToward(new Vector3(npc.X, npc.Y, npc.Z), Math.Max(16, action.Range - 24), now);
                return true;
            case ErrandActionKind.Level:
                return false;
            case ErrandActionKind.Done:
            case ErrandActionKind.Fail:
                _reported = true;
                try { _finished?.Invoke(action.Kind == ErrandActionKind.Done); } catch (Exception) { /* the spawner logs its own */ }
                return false;
            default:
                state.Mover.Stop(now);
                if (state.Player.IsSitting) state.Player.Sit(false);
                return true;
        }
    }

    // Under attack the errands still answer a pending dialog; nothing else.
    private static bool Talking(GamePlayer player) => player.CustomDialogCallback != null;
}
