using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the Shrouded Isles start choice. A new character of a classic race is asked once, on its
// first entry into the world, whether to begin its journey in its realm's Shrouded Isles town instead of
// its home village. This class holds every decision and reads nothing from the running server
// (GameServer, WorldMgr, ServerProperties), so unit tests drive it directly; SiStartChoiceScript feeds it
// the character's state and carries out the outcome.

// Where an accepted choice takes the character: a realm's SI town arrival point, from its Teleport row.
public sealed record SiStartDestination(string Name, ushort Region, int X, int Y, int Z, ushort Heading);

public enum SiStartOutcome
{
    // Response 0x01 from the player: move, bind, save the character, then save "yes".
    Accept,
    // Any other response from the player: save "no", move nothing.
    Decline,
    // The callback ran while our dialog was still the player's pending one: the server replaced it with
    // another dialog. Nothing is saved, so the question comes back at the next login.
    Superseded,
    // The character is dead or no longer qualifies. Nothing is saved.
    NotNow,
}

public static class SiStartChoice
{
    // Per-character custom parameter (DbCoreCharacterXCustomParam) that holds the answer.
    public const string AnswerKey = "hearthdaoc_si_start";
    public const string AnswerYes = "yes", AnswerNo = "no";

    private const byte AcceptResponse = 0x01;

    // Realm -> TeleportID of the SI town's arrival point (WorldMgr.GetTeleportLocation(realm, ":" + id)).
    public static readonly IReadOnlyDictionary<int, string> TeleportIds = new ReadOnlyDictionary<int, string>(
        new Dictionary<int, string>
        {
            [1] = "Caer Gothwaite",
            [2] = "Aegirhamn",
            [3] = "Grove of Domnann",
        });

    // Briton to Lurikeen. Not Inconnu (13), Valkyn (14), Sylvan (15) or races 16-21.
    public static bool IsClassicRace(int race)
    {
        return race >= 1 && race <= 12;
    }

    // The Shrouded Isles regions: Albion's (51), Midgard's (151) and Hibernia's (181).
    public static bool IsSiRegion(int region)
    {
        return region == 51 || region == 151 || region == 181;
    }

    // The two-button question for a realm, or null for any other realm.
    public static string Question(int realm)
    {
        return realm switch
        {
            1 => "Begin your journey in the Shrouded Isles, at Caer Gothwaite? Decline to stay here.",
            2 => "Begin your journey in the Shrouded Isles, at Aegirhamn? Decline to stay here.",
            3 => "Begin your journey in the Shrouded Isles, at the Grove of Domnann? Decline to stay here.",
            _ => null,
        };
    }

    // Asked when the setting is on, the character is level 1, of a classic race, not already in an SI
    // region, has no saved answer, and its realm's destination is known.
    public static bool ShouldAsk(bool enabled, int level, int race, int region, string savedAnswer, bool hasDestination)
    {
        return enabled
            && level == 1
            && IsClassicRace(race)
            && !IsSiRegion(region)
            && string.IsNullOrEmpty(savedAnswer)
            && hasDestination;
    }

    // ShouldAsk, but it reads the saved answer only when everything else already qualifies. The script
    // reads the answer from the database, so most logins cost no query.
    public static bool Qualifies(bool enabled, int level, int race, int region, Func<string> readSavedAnswer,
        bool hasDestination)
    {
        return ShouldAsk(enabled, level, race, region, null, hasDestination)
            && ShouldAsk(enabled, level, race, region, readSavedAnswer(), hasDestination);
    }

    // stillPending: the player's pending dialog callback is still ours when the callback runs.
    // qualifies: ShouldAsk for the character's state when the callback runs.
    public static SiStartOutcome Classify(byte response, bool stillPending, bool qualifies, bool isAlive)
    {
        if (stillPending)
            return SiStartOutcome.Superseded;

        if (!isAlive || !qualifies)
            return SiStartOutcome.NotNow;

        return response == AcceptResponse ? SiStartOutcome.Accept : SiStartOutcome.Decline;
    }

    // Applies an accepted choice in the spec's order; returns true when the answer was written.
    // Move first. If the move fails, nothing is bound, saved or written, the player is told, and the
    // question comes back at the next login. Otherwise bind there, save the character, write "yes", and
    // tell the player where they are. The answer comes last, so a crash before it leaves no answer.
    public static bool ApplyAccept(SiStartDestination destination, Func<SiStartDestination, bool> moveTo,
        Action<SiStartDestination> bind, Action saveCharacter, Action<string> writeAnswer, Action<string> tell)
    {
        if (!moveTo(destination))
        {
            tell($"You could not be moved to {destination.Name}. You stay here; you will be asked again at your next login.");
            return false;
        }

        bind(destination);
        saveCharacter();
        writeAnswer(AnswerYes);
        tell($"Welcome to {destination.Name}, in the Shrouded Isles. You are bound here.");
        return true;
    }
}
