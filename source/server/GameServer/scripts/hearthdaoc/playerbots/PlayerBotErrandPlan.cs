namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC (#137 prototype, branch proto/player-bots): what the errand bot does next. Every decision is made here
// from what the bot sees (ErrandView), with no server reads, so UT_PlayerBotErrands drives it; PlayerBotErrands builds
// the view, carries the action out through the calls a client's packets reach, and logs PLAYER_BOT_ERRAND lines.
// The errands: quest 20206 "Immediate Resolution" (Albion Fighter, levels 1-5), then at level 5 the promotion by the
// trainer's own whisper code, then the gift sword in the right hand.

public enum ErrandTarget { None, Lorik, Olorustos, Aron, Trainer }

public enum ErrandActionKind
{
    Wait,          // stand: dead, or the server's whisper gap
    DeclineDialog, // a custom dialog is pending (the fork's SI start question): answer it "no"
    Walk,          // the driver walks to Target until within Range (3D)
    Whisper,       // bot.Whisper(target, Text)
    Accept,        // raise GamePlayerEvent.AcceptQuest, as the client's Accept button does
    Interact,      // target.Interact(bot)
    Give,          // hand item Text to target, as the client's drag onto an NPC does
    Equip,         // the gift from the backpack to the right hand
    Level,         // nothing to do until level 5: the driver fights as usual
    Done,
    Fail,          // Text says why
}

public readonly record struct ErrandAction(ErrandActionKind Kind, ErrandTarget Target = ErrandTarget.None,
    string Text = null, int Range = 0);

/// <summary>An NPC as the clean 0.37b world has it (Mob table, region 1).</summary>
public sealed record ErrandNpc(string Name, string MobId, int X, int Y, int Z);

/// <summary>A trainer that promotes a Fighter with the whisper "slashing" and gives slash_sword_item.</summary>
public sealed record ErrandTrainer(ErrandNpc Npc, int ClassId, string ClassName);

/// <summary>What the bot sees. QuestStep: 0 not taken, 1 or 2 on that step, -1 finished. Distances are 3D, as the
/// server's range checks are (Point3D.IsWithinRadius); int.MaxValue when the NPC was not found.</summary>
public readonly record struct ErrandView(
    bool Alive, int Level, int ClassId, int QuestStep, bool AcceptDue, bool HasNote, bool GiftInBackpack,
    bool GiftEquipped, bool LevelHeld, int ToLorik, int ToOlorustos, int ToAron, int ToTrainer, long MsSinceWhisper,
    bool DialogPending);

public static class PlayerBotErrandPlan
{
    public const int QuestId = 20206;
    public const string AcceptText = "interested";      // DataQuest 20206 AcceptText, compared with == (DataQuest.cs:2517)
    public const string NoteId = "cq_wax_sealed_note";  // StepItemTemplates "|cq_wax_sealed_note"
    public const string PromoteText = "slashing";       // ArmsmanTrainer.cs:105, MercenaryTrainer.cs:96
    public const string GiftId = "slash_sword_item";    // ArmsmanTrainer.WEAPON_ID1 (:38), MercenaryTrainer.WEAPON_ID1 (:36)
    public const int FighterClassId = 14;               // eCharacterClass.Fighter
    public const ushort Region = 1;

    public static readonly ErrandNpc Lorik = new("Master Lorik", "84c52e78-0043-496f-a23a-986975a9c3c8", 493382, 592627, 1948);
    public static readonly ErrandNpc Olorustos = new("Olorustos", "5a9b08ca-7514-40b5-898e-edd112e5e2c1", 496777, 587597, 2292);
    public static readonly ErrandNpc Aron = new("Guardian Aron", "562c908c-3374-4af6-8e1c-2f0cb89be3db", 490657, 593521, 1667);
    public static readonly ErrandTrainer Falron = new(
        new ErrandNpc("Captain Falron", "f7405d78-cf8d-4a21-9af3-9690110331c8", 521919, 615779, 1818), 2, "Armsman");
    public static readonly ErrandTrainer Astyp = new(
        new ErrandNpc("Master Astyp", "449c43f1-9c66-4ac5-90b2-5aceecb1b11f", 493746, 592969, 1805), 11, "Mercenary");

    // A little inside the server's ranges (WorldMgr.cs:32 whisper 512, :56 interact 192, :52 give 128).
    public const int WhisperRange = 400, InteractRange = 150, GiveRange = 100;
    // GameLiving.WhisperReceive drops a player's whisper under 1,500 ms after the last one (GameLiving.cs:3083).
    public const long WhisperGapMs = 1600;

    public static ErrandAction Decide(in ErrandView v, ErrandTrainer trainer)
    {
        if (!v.Alive)
            return new ErrandAction(ErrandActionKind.Wait);
        if (v.DialogPending)
            return new ErrandAction(ErrandActionKind.DeclineDialog);

        if (v.QuestStep >= 0) // 20206 qualifies a Fighter of level 1-5 only (DataQuest.cs:1031-1040)
        {
            if (v.ClassId != FighterClassId || v.Level > 5)
                return Fail("20206 needs a Fighter of level 1-5");
            if (v.QuestStep == 0)
                return v.AcceptDue
                    ? new ErrandAction(ErrandActionKind.Accept, ErrandTarget.Lorik)
                    : Whisper(ErrandTarget.Lorik, v.ToLorik, v.MsSinceWhisper, AcceptText);
            if (v.QuestStep == 1 || !v.HasNote) // step 1; or step 2 with the note lost: Olorustos hands another
                return Near(ErrandActionKind.Interact, ErrandTarget.Olorustos, v.ToOlorustos, InteractRange);
            return Near(ErrandActionKind.Give, ErrandTarget.Aron, v.ToAron, GiveRange, NoteId);
        }

        if (v.ClassId == FighterClassId)
            return v.Level < 5
                ? new ErrandAction(ErrandActionKind.Level)
                : Whisper(ErrandTarget.Trainer, v.ToTrainer, v.MsSinceWhisper, PromoteText);

        if (v.ClassId != trainer.ClassId)
            return Fail($"class {v.ClassId} after 20206, expected Fighter or {trainer.ClassName}");
        if (v.GiftInBackpack && !v.GiftEquipped)
            return new ErrandAction(ErrandActionKind.Equip, Text: GiftId);
        if (v.LevelHeld) // each trainer interact releases one held level (GameTrainer.cs:142)
            return Near(ErrandActionKind.Interact, ErrandTarget.Trainer, v.ToTrainer, InteractRange);
        return new ErrandAction(ErrandActionKind.Done);
    }

    /// <summary>The NPC an action faces (#137 lever: PlayerBotErrands wakes it after the action and logs a talk line),
    /// or None. The accept answers Lorik's offer; the note goes to Aron.</summary>
    public static ErrandTarget TalkTarget(in ErrandAction action) => action.Kind switch
    {
        ErrandActionKind.Whisper or ErrandActionKind.Interact => action.Target,
        ErrandActionKind.Accept => ErrandTarget.Lorik,
        ErrandActionKind.Give => ErrandTarget.Aron,
        _ => ErrandTarget.None,
    };

    /// <summary>The talk line: the NPC an action faced and whether its brain ran before (PlayerBotWake.BrainState).
    /// Under the lever, a PASS after "asleep" shows the quest needed no awake brain. It holds neither " FAIL" nor
    /// "after-login": summarize.py counts the one, promo.sh stops on both.</summary>
    public static string TalkLine(string npc, ErrandActionKind kind, string brainBefore, bool lever) =>
        $"talk npc=\"{npc}\" action={kind} brainBefore={brainBefore} lever={PlayerBotWake.Describe(lever)}";

    private static ErrandAction Whisper(ErrandTarget target, int distance, long msSinceWhisper, string text)
    {
        if (distance > WhisperRange)
            return new ErrandAction(ErrandActionKind.Walk, target, Range: WhisperRange);
        return msSinceWhisper < WhisperGapMs
            ? new ErrandAction(ErrandActionKind.Wait)
            : new ErrandAction(ErrandActionKind.Whisper, target, text);
    }

    private static ErrandAction Near(ErrandActionKind kind, ErrandTarget target, int distance, int range, string text = null)
        => distance > range ? new ErrandAction(ErrandActionKind.Walk, target, Range: range) : new ErrandAction(kind, target, text);

    private static ErrandAction Fail(string why) => new ErrandAction(ErrandActionKind.Fail, Text: why);
}
