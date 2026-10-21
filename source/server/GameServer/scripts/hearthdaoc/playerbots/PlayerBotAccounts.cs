using System;
using System.Collections.Generic;
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
// handler. A second start finds both rows and creates nothing.
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
    // (StartupLocations.cs:77, :91, :107; the other five handlers do not touch the client).
    public static DbCoreCharacter EnsureCharacter(DbAccount account, PlayerBotSlot slot, GameClient eventClient,
        out bool created, out string failure)
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

        Dictionary<eStat, int> s = PlayerBotRoster.StartingStats(t.Race);
        var ch = new DbCoreCharacter { AccountName = account.Name, Name = slot.CharacterName };   // :394-398
        ch.Gender = 0;                 // male: the row stores 0 (:304, :1009; GamePlayer.cs:2093); eGender.Male is 1
        ch.Race = (int)t.Race;
        ch.CreationModel = (int)RaceOf(t.Race).GetModel(eGender.Male);  // gameobjects/PlayerRace.cs:65
        ch.CurrentModel = ch.CreationModel;
        ch.Region = 0;                                                   // StartupLocations sets it (StartupLocations.cs:95-100)
        ch.Level = 1;
        ch.Realm = (int)t.Realm;
        ch.Class = (int)t.BaseClass;   // created as the base class: StartAsBaseClass has nothing to revert
        ch.AccountSlot = accountSlot;  // :439
        ch.Strength = s[eStat.STR]; ch.Dexterity = s[eStat.DEX]; ch.Constitution = s[eStat.CON]; ch.Quickness = s[eStat.QUI];
        ch.Intelligence = s[eStat.INT]; ch.Piety = s[eStat.PIE]; ch.Empathy = s[eStat.EMP]; ch.Charisma = s[eStat.CHR];
        ch.CreationDate = DateTime.Now;                                  // :460-464 defaults
        ch.Endurance = 100; ch.MaxEndurance = 100; ch.Concentration = 100; ch.MaxSpeed = GamePlayer.PLAYER_BASE_SPEED;

        // A player account may not create a disabled class or race (:480-510): the same lists, compared the same way.
        if (Util.SplitCSV(Properties.DISABLED_CLASSES).Contains(ch.Class.ToString()) ||
            Util.SplitCSV(Properties.DISABLED_RACES).Contains(ch.Race.ToString()))
        { failure = "disabled"; return null; }
        if (!CharacterCreateRequestHandler.IsCharacterValid(ch)) { failure = "invalid"; return null; } // public static, :951
        if (StartupLocations.GetAllStartupLocationForCharacter(ch, eventClient.Version).Count == 0)   // public static, :142
        { failure = "no-start"; return null; }                                                       // before any row is written

        GameServer.Database.AddObject(ch);                                                                   // :538
        GameEventMgr.Notify(DatabaseEvent.CharacterCreated, null, new CharacterEventArgs(ch, eventClient));  // :542
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

    public static bool NameTaken(string name)
    {
        if (DOLDB<DbCoreCharacter>.SelectObject(DB.Column("Name").IsEqualTo(name)) != null) return true; // DuplicateNameCheckRequestHandler.cs:16
        if (GameServer.Instance.PlayerManager.InvalidNames[name]) return true;                             // :21
        try { return DOLDB<OfflineWorldBotRecord>.SelectObject(DB.Column("Name").IsEqualTo(name)) != null; } // AutonomousBotEconomy.cs:913-916
        catch (Exception) { return false; }                                                                 // (est.) a world without that table
    }

    private static PlayerRace RaceOf(eRace race) => race switch
    {
        eRace.Saracen => PlayerRace.Saracen,
        eRace.Norseman => PlayerRace.Norseman,
        eRace.Celt => PlayerRace.Celt,
        _ => throw new ArgumentOutOfRangeException(nameof(race)),
    };
}
