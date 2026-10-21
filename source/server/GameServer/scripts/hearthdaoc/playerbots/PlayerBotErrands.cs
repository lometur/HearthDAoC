using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.Quests;
using DOL.Logging;

namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC (#137 prototype): carries out PlayerBotErrandPlan for one player bot, through the public calls a client's
// packets reach (no upstream edit): whisper, the Accept button's event, interact, the item drag onto an NPC, the
// inventory move. PlayerBotErrandTask calls Think from the driver's tick (the bot's own ECSGameTimer, so the CPU meter
// charges it to playerbot / Timer, BotCpuMeter.cs:657) and walks the bot when Think says Walk. The spawner decides which
// slot runs errands (PlayerBotSlot.IsQuestFighter). Lines: "PLAYER_BOT_ERRAND bot=<name> ...".
public sealed class PlayerBotErrands
{
    private static readonly Logger Log = LoggerManager.Create(typeof(PlayerBotErrands));

    private const int MaxAttempts = 5;                      // the same action at the same quest step, class and level
    private const long WalkLimitMs = 15 * 60 * 1000;        // toward one target
    private const int DeathsBeforeFallback = 2;             // on the way to Captain Falron
    private const long BlockedLineMs = 30_000;              // at most one "blocked" line per 30 s
    private const long QuestFixedXp = 10, QuestCopper = 42; // DataQuest 20206: RewardXP "0|10", RewardMoney "0|42"

    private readonly GamePlayer _bot;
    private readonly Dictionary<string, GameNPC> _npcs = new();
    private readonly Dictionary<(ErrandActionKind, ErrandTarget, int, int, int), int> _attempts = new();
    private ErrandTrainer _trainer;
    private bool _acceptDue;
    private long _lastWhisperAt = -60_000;
    private ErrandTarget _walkTarget;
    private long _walkSince, _deathTick, _nextBlockedLine;
    private int _trainerWalkDeaths;

    public bool Finished { get; private set; }

    public PlayerBotErrands(GamePlayer bot)
    {
        if (bot?.Client is not IPlayerBotClient)
            throw new ArgumentException("errands are for player bots (a client that is an IPlayerBotClient)", nameof(bot));
        _bot = bot;
        _deathTick = bot.DeathTick;
        _trainer = bot.CharacterClass.ID == PlayerBotErrandPlan.Astyp.ClassId ? PlayerBotErrandPlan.Astyp : PlayerBotErrandPlan.Falron;
        Line($"start race={bot.Race} class={bot.CharacterClass.ID} level={bot.Level} region={bot.CurrentRegionID} " +
             $"at=({bot.X},{bot.Y},{bot.Z}) trainer=\"{_trainer.Npc.Name}\"");
    }

    /// <summary>One think, on the game loop. Walk: the driver walks to Npc(Target) until within Range. Level, Done and
    /// Fail: the driver plays as usual. Anything else: the bot stands this think (it defends, but seeks no fight).</summary>
    public ErrandAction Think(long now)
    {
        if (Finished)
            return new ErrandAction(ErrandActionKind.Done);

        ErrandView view = See(now);
        if (CountDeath(view))
            return new ErrandAction(ErrandActionKind.Wait); // the view is Falron's: the next See looks Master Astyp up
        ErrandAction action = Guard(PlayerBotErrandPlan.Decide(view, _trainer), view, now);

        switch (action.Kind)
        {
            case ErrandActionKind.DeclineDialog: DeclinePendingDialog(_bot); break;
            case ErrandActionKind.Whisper: Whisper(action, now); break;
            case ErrandActionKind.Accept: Accept(); break;
            case ErrandActionKind.Interact: Interact(action); break;
            case ErrandActionKind.Give: GiveNote(); break;
            case ErrandActionKind.Equip: EquipGift(); break;
            case ErrandActionKind.Done:
                Finished = true;
                Line($"done class={_bot.CharacterClass.ID} dbClass={_bot.DBCharacter.Class} level={_bot.Level} " +
                     $"rightHand={RightHand() ?? "none"}");
                break;
            case ErrandActionKind.Fail:
                Finished = true;
                Line($"FAIL {action.Text} (class={view.ClassId} level={view.Level} quest20206Step={view.QuestStep})");
                break;
        }

        return action;
    }

    /// <summary>The walk to an NPC was blocked (PlayerBotPathMover.LastBlock says why): one line per 30 s, with both
    /// positions (run 1 failed "Olorustos not reached in 15 min" with no trace of where the bot stood).</summary>
    public void WalkBlocked(GameNPC npc, string why, long now, Func<string> probe = null)
    {
        if (npc == null || now < _nextBlockedLine) return;
        _nextBlockedLine = now + BlockedLineMs;
        Line($"blocked to=\"{npc.Name}\" at=({_bot.X},{_bot.Y},{_bot.Z}) npc=({npc.X},{npc.Y},{npc.Z}) " +
             $"distance={_bot.GetDistanceTo(npc)} zone={_bot.CurrentZone?.ID} why=\"{why}\" {probe?.Invoke()}");
    }

    /// <summary>The NPC an action is about (for the driver's Walk), or null.</summary>
    public GameNPC Npc(ErrandTarget target)
    {
        ErrandNpc spot = Spot(target);
        return spot != null && _npcs.TryGetValue(spot.Name, out GameNPC npc) ? npc : null;
    }

    /// <summary>For every player bot, each think: a pending custom dialog answered "no", as the client's button does
    /// (DialogResponseHandler.cs:25-44). The fork's Shrouded Isles start question (SiStartChoiceScript.cs:80-107) asks
    /// each new level-1 character of a classic race 2 s after it enters the world; "no" is saved (:130-132), so it is
    /// asked once. A "yes" would move the bot to Caer Gothwaite (region 51).</summary>
    public static void DeclinePendingDialog(GamePlayer bot)
    {
        if (bot.Client is HeadlessClient headless)
        {
            // The client's own path: a "no" DialogResponse packet, handled at the bot's next ClientService.BeginTick
            // (DialogResponseHandler.cs:25-44). Only one in flight: the callback stays set until the handler runs.
            if (headless.PendingAnswers == 0)
            {
                headless.AnswerCustomDialog(false);
                Log.Info($"PLAYER_BOT_ERRAND bot={bot.Name} dialog declined");
            }
            return;
        }

        CustomDialogResponse callback;
        lock (bot) // the lock SendCustomDialog sets it under (PacketLib168.cs:1562) and the handler reads it under (:32)
        {
            callback = bot.CustomDialogCallback;
            bot.CustomDialogCallback = null;
        }

        if (callback == null)
            return;

        callback(bot, 0x00);
        Log.Info($"PLAYER_BOT_ERRAND bot={bot.Name} dialog declined");
    }

    /// <summary>After the errand bot has logged out and back in: what the database gave back (F3, F4).</summary>
    public static void CheckAfterLogin(GamePlayer bot)
    {
        bool finished = QuestStep(bot) == -1;
        DbCharacterXDataQuest row = DataQuest.GetCharacterQuest(bot, PlayerBotErrandPlan.QuestId, false);
        string rightHand = bot.Inventory.GetItem(eInventorySlot.RightHandWeapon)?.Id_nb;
        int classId = bot.CharacterClass.ID;
        bool promoted = classId == PlayerBotErrandPlan.Falron.ClassId || classId == PlayerBotErrandPlan.Astyp.ClassId;
        bool pass = finished && row is { Step: 0, Count: 1 } && promoted && bot.DBCharacter.Class == classId
            && rightHand == PlayerBotErrandPlan.GiftId;
        Log.Info($"PLAYER_BOT_ERRAND bot={bot.Name} after-login {(pass ? "PASS" : "FAIL")} quest20206Finished={finished} " +
                 $"row={(row == null ? "none" : $"{row.Step}/{row.Count}")} class={classId} level={bot.Level} " +
                 $"rightHand={rightHand ?? "none"}");
    }

    private ErrandNpc Spot(ErrandTarget target) => target switch
    {
        ErrandTarget.Lorik => PlayerBotErrandPlan.Lorik,
        ErrandTarget.Olorustos => PlayerBotErrandPlan.Olorustos,
        ErrandTarget.Aron => PlayerBotErrandPlan.Aron,
        ErrandTarget.Trainer => _trainer.Npc,
        _ => null,
    };

    private ErrandView See(long now)
    {
        bool promoted = _bot.CharacterClass.ID != PlayerBotErrandPlan.FighterClassId;
        return new ErrandView(
            _bot.IsAlive, _bot.Level, _bot.CharacterClass.ID, QuestStep(_bot), _acceptDue,
            Backpack(PlayerBotErrandPlan.NoteId) != null, Backpack(PlayerBotErrandPlan.GiftId) != null,
            RightHand() == PlayerBotErrandPlan.GiftId,
            promoted && _bot.Level < GamePlayer.MAX_LEVEL && _bot.Experience >= _bot.ExperienceForNextLevel,
            DistanceTo(PlayerBotErrandPlan.Lorik), DistanceTo(PlayerBotErrandPlan.Olorustos),
            DistanceTo(PlayerBotErrandPlan.Aron), DistanceTo(_trainer.Npc),
            now - _lastWhisperAt, _bot.CustomDialogCallback != null);
    }

    private static int QuestStep(GamePlayer bot)
    {
        if (bot.GetFinishedQuests().OfType<DataQuest>().Any(q => q.ID == PlayerBotErrandPlan.QuestId))
            return -1;
        DataQuest active = bot.QuestList.Keys.OfType<DataQuest>().FirstOrDefault(q => q.ID == PlayerBotErrandPlan.QuestId);
        return active?.Step ?? 0;
    }

    private int DistanceTo(ErrandNpc spot)
    {
        // A missing NPC is looked for once; a found one again only if it left the world.
        if (!_npcs.TryGetValue(spot.Name, out GameNPC npc) || npc is { ObjectState: not GameObject.eObjectState.Active })
            _npcs[spot.Name] = npc = Find(spot);
        return npc == null ? int.MaxValue : _bot.GetDistanceTo(npc); // GameObject.cs:115: int.MaxValue in another region
    }

    private static GameNPC Find(ErrandNpc spot)
    {
        // Not WorldMgr.GetNPCsByNameFromRegion: it casts a GameObject[] to GameNPC[] when the region is missing (WorldMgr.cs:767, :807).
        GameNPC[] named = WorldMgr.GetNPCsFromRegion(PlayerBotErrandPlan.Region).Where(n => n.Name == spot.Name).ToArray();
        GameNPC npc = named.FirstOrDefault(n => n.InternalID == spot.MobId)
            ?? named.OrderBy(n => (long)(n.X - spot.X) * (n.X - spot.X) + (long)(n.Y - spot.Y) * (n.Y - spot.Y)).FirstOrDefault();
        Log.Info(npc == null
            ? $"PLAYER_BOT_ERRAND npc \"{spot.Name}\" NOT FOUND in region {PlayerBotErrandPlan.Region}"
            : $"PLAYER_BOT_ERRAND npc \"{spot.Name}\" at ({npc.X},{npc.Y},{npc.Z}) mob={npc.InternalID}" +
              (npc.InternalID == spot.MobId ? "" : " (not the clean world's Mob_ID)"));
        return npc;
    }

    private ErrandAction Guard(ErrandAction action, in ErrandView view, long now)
    {
        if (action.Kind == ErrandActionKind.Walk)
        {
            bool toFalron = action.Target == ErrandTarget.Trainer && _trainer == PlayerBotErrandPlan.Falron;
            if (Npc(action.Target) == null)
                return toFalron ? FallBack("no Captain Falron in the world")
                    : new ErrandAction(ErrandActionKind.Fail, Text: $"no {Spot(action.Target)?.Name} in region 1");
            if (action.Target != _walkTarget)
                (_walkTarget, _walkSince) = (action.Target, now);
            else if (now - _walkSince > WalkLimitMs)
                return toFalron ? FallBack($"Captain Falron not reached in {WalkLimitMs / 60_000} min")
                    : new ErrandAction(ErrandActionKind.Fail, Text: $"{Spot(action.Target).Name} not reached in {WalkLimitMs / 60_000} min");
            return action;
        }

        _walkTarget = ErrandTarget.None;
        if (action.Kind is not (ErrandActionKind.Whisper or ErrandActionKind.Accept or ErrandActionKind.Interact
            or ErrandActionKind.Give or ErrandActionKind.Equip))
            return action;

        var key = (action.Kind, action.Target, view.QuestStep, view.ClassId, view.Level);
        int attempts = _attempts.GetValueOrDefault(key) + 1;
        _attempts[key] = attempts;
        return attempts > MaxAttempts
            ? new ErrandAction(ErrandActionKind.Fail, Text: $"no progress after {MaxAttempts} x {action.Kind} {action.Target}")
            : action;
    }

    // Think never sees the bot dead: the driver handles a death before its task (PlayerBotDriver.Think steps 6 and 10).
    // So a death shows as a new DeathTick (set in ProcessDeath, GamePlayer.cs:5953) at the first think after the release.
    // True when it fell back to Master Astyp: Npc(Trainer) reads only NPCs a See has looked up, so this think's view and
    // lookups are still Falron's.
    private bool CountDeath(in ErrandView view)
    {
        if (_bot.DeathTick == _deathTick)
            return false;
        _deathTick = _bot.DeathTick;
        Line($"died level={view.Level} walkingTo={_walkTarget}");
        if (_walkTarget != ErrandTarget.Trainer || _trainer != PlayerBotErrandPlan.Falron
            || ++_trainerWalkDeaths < DeathsBeforeFallback)
            return false;
        FallBack($"died {_trainerWalkDeaths} times on the way to Captain Falron");
        return true;
    }

    // The Mercenary trainer at Campacorentin Station, 499 units from Master Lorik: the same keyword, gift and code path.
    private ErrandAction FallBack(string why)
    {
        _trainer = PlayerBotErrandPlan.Astyp;
        _walkTarget = ErrandTarget.None;
        Line($"fallback trainer=\"{_trainer.Npc.Name}\" ({_trainer.ClassName}): {why}");
        return new ErrandAction(ErrandActionKind.Wait);
    }

    private void Whisper(ErrandAction action, long now)
    {
        GameNPC npc = Npc(action.Target);
        int classBefore = _bot.CharacterClass.ID;
        if (action.Target == ErrandTarget.Trainer)
            MakeRoomForGift();
        // GamePlayer.Whisper (GamePlayer.cs:7633-7648) -> GameLiving.Whisper (GameLiving.cs:3108-3126) -> the NPC's
        // WhisperReceive: Lorik's data quests (GameLiving.cs:3091-3094), or the trainer's switch (ArmsmanTrainer.cs:96-110).
        bool ok = _bot.Whisper(npc, action.Text);
        _lastWhisperAt = now;
        if (action.Target == ErrandTarget.Lorik)
        {
            _acceptDue = ok; // the client would now show "Do you accept?" (DataQuestOffers.cs:58-64): answer next think
            DataQuest offered = npc.DataQuestList.FirstOrDefault(q => q.ID == PlayerBotErrandPlan.QuestId);
            Line($"whisper npc=\"{npc.Name}\" text={action.Text} ok={ok} giverHas20206={offered != null} " +
                 $"qualifies={offered?.CheckQuestQualification(_bot)}");
        }
        else
            Line($"promotion trainer=\"{npc.Name}\" text={action.Text} ok={ok} class={classBefore}->{_bot.CharacterClass.ID} " +
                 $"dbClass={_bot.DBCharacter.Class} gift={Backpack(PlayerBotErrandPlan.GiftId) != null}");
    }

    private void Accept()
    {
        _acceptDue = false;
        // DialogResponseHandler.cs:102-114: the Accept button raises exactly this. DataQuestOffers.OnAcceptQuest
        // (DataQuestOffers.cs:110-126) matches the pending offer (same NPC object, id 0xFFF0, under 5 minutes) and
        // StartOffered (DataQuest.cs:2552-2608) creates the CharacterXDataQuest row at step 1.
        _bot.Notify(GamePlayerEvent.AcceptQuest, _bot,
            new QuestEventArgs(Npc(ErrandTarget.Lorik), _bot, DataQuestOffers.OfferQuestId));
        Line($"accept quest={PlayerBotErrandPlan.QuestId} step={QuestStep(_bot)}");
    }

    private void Interact(ErrandAction action)
    {
        GameNPC npc = Npc(action.Target);
        int levelBefore = _bot.Level;
        // GameObject.Interact (GameObject.cs:837-855): range 192 at PrivLevel 1, then the Interact and InteractWith events.
        // Olorustos: DataQuest.OnPlayerInteract (:2616-2674), step 1 -> 2 and the note (:1675-1688); a lost note again
        // (ClassicQuests.cs:637-648). A trainer of the bot's class: one held level (GameTrainer.cs:142).
        bool ok = npc.Interact(_bot);
        Line($"interact npc=\"{npc.Name}\" ok={ok} quest20206Step={QuestStep(_bot)} " +
             $"note={Backpack(PlayerBotErrandPlan.NoteId) != null} level={levelBefore}->{_bot.Level}");
    }

    private void GiveNote()
    {
        GameNPC aron = Npc(ErrandTarget.Aron);
        DbInventoryItem note = Backpack(PlayerBotErrandPlan.NoteId);
        if (note == null || !aron.IsWithinRadius(_bot, WorldMgr.GIVE_ITEM_DISTANCE)) // PlayerMoveItemRequestHandler.cs:182-199
            return;

        long xpBefore = _bot.Experience, moneyBefore = _bot.GetCurrentMoney();
        // Stage 2's XP is a share of the current level (0.37b classic-quests.json XpShares [0.0, 0.2]), never under the
        // row's 10 (ClassicQuests.cs:560-567, paid at DataQuest.cs:3189): 10 at level 1, then 40, 120, 290, 810.
        long xpExpected = ClassicQuests.ScaleXP(PlayerBotErrandPlan.QuestId, _bot, 1, QuestFixedXp);

        // PlayerMoveItemRequestHandler.cs:201-224, the client dragging the note onto Aron.
        // DataQuest.OnPlayerGiveItem's DeliverFinish (DataQuest.cs:2735-2747) finishes the quest and takes the note.
        _bot.Notify(GamePlayerEvent.GiveItem, _bot, new GiveItemEventArgs(_bot, aron, note));
        if (note.OwnerID != null)
            aron.ReceiveItem(_bot, note);

        bool finished = QuestStep(_bot) == -1;
        bool active = _bot.QuestList.Keys.OfType<DataQuest>().Any(q => q.ID == PlayerBotErrandPlan.QuestId);
        DbCharacterXDataQuest row = DataQuest.GetCharacterQuest(_bot, PlayerBotErrandPlan.QuestId, false); // DataQuest.cs:992
        long xp = _bot.Experience - xpBefore, copper = _bot.GetCurrentMoney() - moneyBefore;
        bool noteGone = _bot.Inventory.GetFirstItemByID(PlayerBotErrandPlan.NoteId, eInventorySlot.Min_Inv, eInventorySlot.Max_Inv) == null;
        bool pass = finished && !active && row is { Step: 0, Count: 1 } && xp == xpExpected && copper == QuestCopper && noteGone;
        Line($"quest={PlayerBotErrandPlan.QuestId} {(pass ? "PASS" : "FAIL")} finished={finished} active={active} " +
             $"row={(row == null ? "none" : $"{row.Step}/{row.Count}")} xp=+{xp} expectedXp={xpExpected} " +
             $"copper=+{copper} noteGone={noteGone} level={_bot.Level}");
    }

    private void EquipGift()
    {
        DbInventoryItem gift = Backpack(PlayerBotErrandPlan.GiftId);
        // GamePlayerInventory.MoveItem (GamePlayerInventory.cs:431; right-hand checks :783-798), the call the client's
        // move packet makes (PlayerMoveItemRequestHandler.cs:78). The practice sword goes to the gift's backpack slot.
        bool ok = gift != null && _bot.Inventory.MoveItem((eInventorySlot)gift.SlotPosition, eInventorySlot.RightHandWeapon, 1);
        Line($"equip item={PlayerBotErrandPlan.GiftId} ok={ok} rightHand={RightHand() ?? "none"}");
    }

    // The trainer promotes first and then hands the gift to the first free backpack slot; a full backpack loses it
    // (ArmsmanTrainer.cs:107-108, GamePlayer.ReceiveItem returns false). The driver loots every kill and never sells, so
    // one item goes first, the way the client's destroy request takes it (DestroyItemRequestHandler.cs:11-38).
    private void MakeRoomForGift()
    {
        if (_bot.Inventory.IsSlotsFree(1, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack))
            return;
        DbInventoryItem item = _bot.Inventory.GetItemRange(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack)
            .Where(i => !i.IsIndestructible && i.Id_nb != PlayerBotErrandPlan.NoteId)
            .OrderByDescending(i => i.SlotPosition)
            .FirstOrDefault();
        bool ok = item != null && _bot.Inventory.RemoveItem(item);
        Line($"destroy item={item?.Id_nb ?? "none"} ok={ok} (backpack full; the gift needs a slot)");
    }

    private DbInventoryItem Backpack(string id)
        => _bot.Inventory.GetFirstItemByID(id, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);

    private string RightHand() => _bot.Inventory.GetItem(eInventorySlot.RightHandWeapon)?.Id_nb;

    private void Line(string text) => Log.Info($"PLAYER_BOT_ERRAND bot={_bot.Name} {text}");
}
