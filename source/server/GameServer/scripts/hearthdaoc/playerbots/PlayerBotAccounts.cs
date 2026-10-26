using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.Events;
using DOL.GS.GameEvents;
using DOL.GS.PacketHandler.Client.v168;
using DOL.GS.Realm;
using DOL.GS.ServerProperties;

namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC #137 spike: one hdbot- account and one level-1 base-class character per slot, made as
// CharacterCreateRequestHandler.CreateCharacter (packets/Client/168/CharacterCreateRequestHandler.cs:391-558) makes them,
// without the packet. Runs on a game-loop thread (a NpcService posted action from PlayerBotSpawner), like the real
// handler. A second start finds both rows and creates nothing. A level-matched bot (levelmatch/DESIGN.md 2.4) is made
// the same way with its spot's race and class, then raised to its build after the CharacterCreated handlers.
public static class PlayerBotAccounts
{
    public static DbAccount EnsureAccount(PlayerBotSlot slot)
    {
        DbAccount account = DOLDB<DbAccount>.SelectObject(DB.Column("Name").IsEqualTo(slot.AccountName));
        if (account == null)
        {
            account = new DbAccount // fields as LoginRequestHandler.cs:344-358, minus LastLoginIP
            {
                Name = slot.AccountName,                                                    // PK (DbAccount.cs:51)
                Password = LoginRequestHandler.CryptPassword(Guid.NewGuid().ToString("N")), // :479-499; never used
                Realm = (int)slot.Realm,              // WorldInit's slot math reads Account.Realm (WorldInitRequestHandler.cs:159-160)
                PrivLevel = (int)ePrivLevel.Player,   // player rules: GameObject.cs:839, GameLiving.cs:3083
                Language = Properties.SERV_LANGUAGE,  // the trainer, quest and XP messages translate with it
                LastClientVersion = ((int)HeadlessClient.BotVersion).ToString(), // :351, as a 1.127 client's login writes it
                CreationDate = DateTime.Now,
                LastLogin = DateTime.Now,
                Notes = "HearthDAoC player bot (#137)",
                // LastLoginIP stays null: same-IP creation limits (LoginRequestHandler.cs:296-342) never count bots.
            };
            GameServer.Database.AddObject(account);
        }
        else if (account.Realm != (int)slot.Realm)
        {
            account.Realm = (int)slot.Realm;
            GameServer.Database.SaveObject(account);
        }
        if (account.Characters == null) // WorldInit loads only when Characters != null (WorldInitRequestHandler.cs:19)
            GameServer.Database.FillObjectRelations(account);
        return account;
    }

    // eventClient: the bot's own HeadlessClient (Version 1127). The CharacterCreated handlers read only its Version
    // (StartupLocations.cs:77, :91, :107; the other five handlers do not touch the client). kit: a level-matched bot's
    // plan, null for a roster bot.
    public static DbCoreCharacter EnsureCharacter(DbAccount account, PlayerBotSlot slot, GameClient eventClient,
        PlayerBotKitPlan kit, out bool created, out string failure)
    {
        created = false;
        failure = null;
        PlayerBotTemplate t = slot.Template;
        int accountSlot = PlayerBotRoster.AccountSlot(t.Realm);
        foreach (DbCoreCharacter existing in account.Characters ?? [])
        {
            if (existing == null || existing.AccountSlot != accountSlot) continue;
            if (existing.Region == 0) { failure = "no-start"; return null; }
            return existing;                                                       // restart path: no creation, no event
        }
        if (NameTaken(slot.CharacterName)) { failure = "name-taken"; return null; }

        DbCoreCharacter ch = NewRow(account.Name, slot, kit, out ICharacterClass charClass);
        if (ch == null) { failure = "bad-build"; return null; }

        // A player account may not create a disabled class or race (:480-510): the same lists, compared the same way.
        if (Util.SplitCSV(Properties.DISABLED_CLASSES).Contains(ch.Class.ToString()) ||
            Util.SplitCSV(Properties.DISABLED_RACES).Contains(ch.Race.ToString()))
        { failure = "disabled"; return null; }
        if (!CharacterCreateRequestHandler.IsCharacterValid(ch)) { failure = "invalid"; return null; } // public static, :951
        if (StartupLocations.GetAllStartupLocationForCharacter(ch, eventClient.Version).Count == 0)   // public static, :142
        { failure = "no-start"; return null; }                                                       // before any row is written

        GameServer.Database.AddObject(ch);                                                                   // :538
        GameEventMgr.Notify(DatabaseEvent.CharacterCreated, null, new CharacterEventArgs(ch, eventClient));  // :542
        if (kit != null)                                       // after StartAsBaseClass (StartAsBaseClass.cs:36-40), before the save
            PlayerBotKit.ApplyToRow(ch, kit, charClass, PlayerBotKit.OpenSpecs((int)kit.Build.Class, kit.Build.Level));
        GameServer.Database.SaveObject(ch);                                                                  // :545
        // The Shrouded Isles start question (on by default in the deploy, compose.yml:24) is never asked: the answer
        // "no" is stored now, in the row shape SiStartChoiceScript writes (:163-182; key/value SiStartChoice.cs:32-33).
        GameServer.Database.AddObject(new DbCoreCharacterXCustomParam(ch.ObjectId, SiStartChoice.AnswerKey, SiStartChoice.AnswerNo));
        account.Characters = null;
        GameServer.Database.FillObjectRelations(account);                                                    // :548-549
        if (ch.Region == 0) { failure = "no-start"; return null; }      // never AddToWorld at region 0 (GuildMgr.cs:41 NRE)
        created = true;
        return ch;
    }

    /// <summary>The level-1 row CreateCharacter makes (:394-464): a roster slot's template race and base class, male;
    /// or a build's race and class (its class gives the stats' primary, secondary and tertiary and the spec multiplier),
    /// its stats at level 1 and the gender its constraints give. The build's class object comes back for ApplyToRow.
    /// Null when that class is unknown, a build of level 5 or more is not an advanced class, or the race has no
    /// starting stats.</summary>
    public static DbCoreCharacter NewRow(string accountName, PlayerBotSlot slot, PlayerBotKitPlan kit, out ICharacterClass charClass)
    {
        PlayerBotTemplate t = slot.Template;
        eRace race = kit?.Build.Race ?? t.Race;
        eCharacterClass cls = kit?.Build.Class ?? t.BaseClass;
        charClass = kit == null ? null : ScriptMgr.FindCharacterClass((int)cls);
        if (kit != null && (charClass == null || kit.Build.Level >= 5 && !charClass.HasAdvancedFromBaseClass() ||
                            !GlobalConstants.STARTING_STATS_DICT.ContainsKey(race)))
            return null;
        Dictionary<eStat, int> s = kit == null ? PlayerBotRoster.StartingStats(race)
            : PlayerBotKit.Stats(race, 1, charClass.PrimaryStat, charClass.SecondaryStat, charClass.TertiaryStat);
        int gender = PlayerBotKit.RowGender(race, cls);

        var ch = new DbCoreCharacter { AccountName = accountName, Name = slot.CharacterName };   // :394-398
        ch.Gender = gender;            // the row stores 0 male, 1 female (:304, :1009; GamePlayer.cs:2093); eGender.Male is 1
        ch.Race = (int)race;
        ch.CreationModel = (int)PlayerBotKit.RaceOf(race).GetModel(gender == 0 ? eGender.Male : eGender.Female); // PlayerRace.cs:65
        ch.CurrentModel = ch.CreationModel;
        ch.Region = 0;                                                   // StartupLocations sets it (StartupLocations.cs:95-100)
        ch.Level = 1;                  // IsCharacterValid wants 1 (:964); a build is raised after the event (ApplyToRow)
        ch.Realm = (int)t.Realm;
        ch.Class = (int)cls;           // a base class: StartAsBaseClass has nothing to revert; a build's advanced class it
                                       // reverts in the event, and ApplyToRow restores
        ch.AccountSlot = PlayerBotRoster.AccountSlot(t.Realm);           // :439
        PlayerBotKit.SetStats(ch, s);
        ch.CreationDate = DateTime.Now;                                  // :460-464 defaults
        ch.Endurance = 100; ch.MaxEndurance = 100; ch.Concentration = 100; ch.MaxSpeed = GamePlayer.PLAYER_BASE_SPEED;
        return ch;
    }

    /// <summary>The account's row for this character, the one the login reads (GameClient.cs:114). After a creation
    /// EnsureCharacter's own object is not it: the account's characters were read again (:95-96).</summary>
    public static DbCoreCharacter LoginRow(DbAccount account, DbCoreCharacter ch) =>
        account.Characters?.FirstOrDefault(c => c != null && c.AccountSlot == ch.AccountSlot) ?? ch;

    public static bool NameTaken(string name)
    {
        if (DOLDB<DbCoreCharacter>.SelectObject(DB.Column("Name").IsEqualTo(name)) != null) return true; // DuplicateNameCheckRequestHandler.cs:16
        if (GameServer.Instance.PlayerManager.InvalidNames[name]) return true;                             // :21
        try { return DOLDB<OfflineWorldBotRecord>.SelectObject(DB.Column("Name").IsEqualTo(name)) != null; } // AutonomousBotEconomy.cs:913-916
        catch (Exception) { return false; }                                                                 // (est.) a world without that table
    }
}
