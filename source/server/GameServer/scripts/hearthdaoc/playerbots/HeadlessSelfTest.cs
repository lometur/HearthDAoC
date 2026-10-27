using System;
using System.Threading;
using DOL.Logging;

namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC player bots (#137): one bot checks the client piece's paths in the live game, once, about 10 s after it enters the
// world: registration, a LoS answer, a custom dialog answered through the real handler, a region change out and back
// (re-entry), and the heartbeat. It logs "PLAYER_BOT selftest <step> ok|FAIL <account> <detail>" and then a "done"
// line, then calls onDone (the spawner starts that bot's driver there). It runs on a timer its player owns, so the
// meter charges it to playerbot. It never throws: a timer that throws kicks its owner (TimerService.cs:64-67).
// The spawner runs it on the first slot that is not the quest fighter (hdbot-alb-01 must start its quest unmoved).
public sealed class HeadlessSelfTest : ILosCheckListener
{
    private static readonly Logger Log = LoggerManager.Create(typeof(HeadlessSelfTest));
    private const int StartDelayMs = 10000, PollMs = 250, LosWaitMs = 3000, DialogWaitMs = 2000, RegionWaitMs = 10000;
    private const ushort NpcRadius = 3000;

    private enum Phase { Registered, Los, LosWait, Dialog, DialogWait, RegionOut, RegionOutWait, RegionBackWait, Heartbeat }

    private readonly HeadlessClient _client;
    private readonly GamePlayer _player;
    private readonly Action<HeadlessClient> _onDone;
    private Phase _phase;
    private long _deadline;
    private int _los;          // (int)LosCheckResponse, written by the LoS listener
    private int _dialog = -1;  // the custom dialog's answer byte, written by its callback
    private int _entries;
    private ushort _homeRegion, _homeHeading, _awayRegion;
    private int _homeX, _homeY, _homeZ;
    private int _ok, _fail;

    private HeadlessSelfTest(HeadlessClient client, Action<HeadlessClient> onDone)
    {
        _client = client;
        _player = client.Player;
        _onDone = onDone;
    }

    // The spawner calls this once, for its self-test slot, from EnteredWorld(first: true).
    public static void Start(HeadlessClient client, Action<HeadlessClient> onDone = null)
    {
        if (client?.Player == null)
            return;
        HeadlessSelfTest test = new(client, onDone);
        _ = new ECSGameTimer(client.Player, test.Tick, StartDelayMs);
    }

    void ILosCheckListener.HandleLosCheckResponse(GamePlayer player, LosCheckResponse response, ushort targetId) =>
        Volatile.Write(ref _los, (int)response);

    private int Tick(ECSGameTimer timer)
    {
        try
        {
            if (_client.ClientState is GameClient.eClientState.Disconnected || _client.Step is HeadlessStep.LoggingOut)
            {
                Report("aborted", false, $"step={_client.Step}");
                return Done(callBack: false);
            }
            return Step(GameLoop.GameLoopTime);
        }
        catch (Exception e)
        {
            Log.Error($"PLAYER_BOT selftest {_phase} FAIL exception", e);
            _fail++;
            return Done(callBack: true);
        }
    }

    private int Step(long now)
    {
        switch (_phase)
        {
            case Phase.Registered:
                Report("registered", ClientService.Instance.GetClientBySessionId(_client.SessionID) == _client
                    && _client.ServiceObjectId.IsSet && _client.IsPlaying, $"session={_client.SessionID}");
                _phase = Phase.Los;
                return PollMs;
            case Phase.Los:
            {
                GameNPC npc = Nearest();
                if (npc == null || !_player.Out.SendLosCheckRequest(npc, _player, this))
                {
                    Report("los", false, npc == null ? $"no NPC within {NpcRadius}" : "not started");
                    _phase = Phase.Dialog;
                    return PollMs;
                }
                _deadline = now + LosWaitMs;
                _phase = Phase.LosWait;
                return PollMs;
            }
            case Phase.LosWait:
            {
                LosCheckResponse answer = (LosCheckResponse)Volatile.Read(ref _los);
                bool answered = answer is LosCheckResponse.True or LosCheckResponse.False;
                if (!answered && now <= _deadline)
                    return PollMs;
                Report("los", answered, $"answer={answer}");
                _phase = Phase.Dialog;
                return PollMs;
            }
            case Phase.Dialog:
                _player.Out.SendCustomDialog("PLAYER_BOT selftest", (_, response) => Volatile.Write(ref _dialog, response));
                _client.AnswerCustomDialog(true);
                _deadline = now + DialogWaitMs;
                _phase = Phase.DialogWait;
                return PollMs;
            case Phase.DialogWait:
            {
                int answer = Volatile.Read(ref _dialog);
                if (answer < 0 && now <= _deadline)
                    return PollMs;
                Report("dialog", answer == 0x01, $"answer={answer}");
                _phase = Phase.RegionOut;
                return PollMs;
            }
            case Phase.RegionOut:
            {
                AutonomousStuckWatchdog.CapitalLocation capital = AutonomousStuckWatchdog.SafeCapitalFor(_player.Realm);
                if (capital.RegionId == 0 || capital.RegionId == _player.CurrentRegionID)
                {
                    Report("region", true, "skipped: already in the capital's region");
                    _phase = Phase.Heartbeat;
                    return PollMs;
                }
                (_homeRegion, _homeX, _homeY, _homeZ, _homeHeading) = (_player.CurrentRegionID, _player.X, _player.Y, _player.Z, _player.Heading);
                _awayRegion = capital.RegionId;
                _entries = _client.Entries;
                _player.MoveTo(capital.RegionId, capital.X, capital.Y, capital.Z, capital.Heading);
                _deadline = now + RegionWaitMs;
                _phase = Phase.RegionOutWait;
                return PollMs;
            }
            case Phase.RegionOutWait:
                if (!Reentered(_awayRegion) && now <= _deadline)
                    return PollMs;
                Report("region-out", Reentered(_awayRegion), $"region={_player.CurrentRegionID} step={_client.Step}");
                _entries = _client.Entries;
                _player.MoveTo(_homeRegion, _homeX, _homeY, _homeZ, _homeHeading);
                _deadline = now + RegionWaitMs;
                _phase = Phase.RegionBackWait;
                return PollMs;
            case Phase.RegionBackWait:
                if (!Reentered(_homeRegion) && now <= _deadline)
                    return PollMs;
                Report("region-back", Reentered(_homeRegion), $"region={_player.CurrentRegionID} step={_client.Step}");
                _phase = Phase.Heartbeat;
                return PollMs;
            default:
                Report("heartbeat", _client.Heartbeats > 0, $"heartbeats={_client.Heartbeats} beginTicks={_client.BeginTicks}");
                return Done(callBack: true);
        }
    }

    private bool Reentered(ushort region) =>
        _client.Entries > _entries && _client.Step is HeadlessStep.InWorld && _player.CurrentRegionID == region;

    private GameNPC Nearest()
    {
        GameNPC best = null;
        int bestDistance = int.MaxValue;
        foreach (GameNPC npc in _player.GetNPCsInRadius(NpcRadius))
        {
            int distance = _player.GetDistanceTo(npc);
            if (npc.ObjectID != 0 && distance < bestDistance)
            {
                best = npc;
                bestDistance = distance;
            }
        }
        return best;
    }

    private void Report(string step, bool ok, string detail)
    {
        if (ok) _ok++; else _fail++;
        Log.Info($"PLAYER_BOT selftest {step} {(ok ? "ok" : "FAIL")} {_client.Account?.Name} {detail}");
    }

    private int Done(bool callBack)
    {
        Log.Info($"PLAYER_BOT selftest done {_client.Account?.Name} ok={_ok} fail={_fail}");
        if (callBack && _onDone != null)
        {
            try { _onDone(_client); }
            catch (Exception e) { Log.Error("PLAYER_BOT selftest onDone failed", e); }
        }
        return 0;
    }
}
