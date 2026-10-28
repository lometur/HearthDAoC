using System.Collections.Generic;
using System.Linq;
using DOL.GS.PacketHandler;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: forming a guild needs a group of guild_num (8) again, bots included (owner 2026-10-28, #176: "players
// should need 8 again too (but this can include bots)"). Every member of the group counts: players, player bots,
// gamebots and companions. Only the human players are asked: bots can't answer the accept dialog (a player bot leaves
// it pending, or declines it when another dialog replaces it), so they count as accepting. Rule is pure
// (UT_GuildForming); guild.cs asks it and carries the outcome out, and the founding group's bots join the new guild.

public enum GuildFormStep
{
    // Fewer members than guild_num: nothing is formed.
    Refused,
    // The player is the group's only human: the guild forms at once at the registrar, for the fee.
    FormNow,
    // The group has other human players: they are asked, and the guild forms when all of them accept.
    AskOthers,
}

public static class GuildForming
{
    public static GuildFormStep Rule(int memberCount, int humanCount, int guildNum) =>
        memberCount < guildNum ? GuildFormStep.Refused
        : humanCount > 1 ? GuildFormStep.AskOthers
        : GuildFormStep.FormNow;

    // A player bot: a GamePlayer the server plays itself (its client is an IPlayerBotClient).
    public static bool IsPlayerBot(GamePlayer player) => player?.Client is IPlayerBotClient;

    // The group's human players (its player bots left out); the player alone when it has no group.
    public static List<GamePlayer> Humans(GamePlayer player, Group group) =>
        group == null ? [player] : group.GetPlayersInTheGroup().Where(member => !IsPlayerBot(member)).ToList();

    // The name of a bot of the group already in a guild (a player bot, or a gamebot through AutonomousBotGuilds), or
    // null. Like a player in a guild, it stops the forming: every founding member joins the new guild.
    public static string BotInAGuild(Group group)
    {
        if (group == null) return null;
        foreach (GameLiving member in group.GetMembersInTheGroup())
        {
            if (member is GamePlayer player && IsPlayerBot(player) && player.Guild != null) return player.Name;
            if (member is GameBot { IsAutonomousWorldBot: true } bot && AutonomousBotGuilds.GuildOf(bot.DatabaseID) != null)
                return bot.Name;
        }
        return null;
    }

    // After the guild is formed, with the leader in it: the founding group's bots join it. Player bots join as members,
    // as players do (with addPlayerBots false the caller has added every player of the group itself); gamebots as
    // /gc invite takes them (AutonomousBotGuilds.Join, invited by the leader). A bot that can't join is named to the
    // leader. Companions stay out: they already wear their owner's guild name and emblem.
    public static void JoinBots(Guild guild, GamePlayer leader, Group group, bool addPlayerBots)
    {
        if (guild == null || leader == null || group == null) return;
        foreach (GameLiving member in group.GetMembersInTheGroup())
        {
            if (member is GamePlayer player)
            {
                if (addPlayerBots && player != leader && IsPlayerBot(player) && !guild.AddPlayer(player))
                    leader.Out.SendMessage($"{player.Name} could not join the guild.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                continue;
            }
            if (member is not GameBot { IsAutonomousWorldBot: true } bot) continue;
            string refusal = AutonomousBotGuilds.JoinRefusal(leader, bot);
            if (refusal != null || !AutonomousBotGuilds.Join(leader, bot))
                leader.Out.SendMessage(refusal == null ? $"{bot.Name} could not join the guild." : $"{bot.Name} did not join the guild: {refusal}",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }
}
