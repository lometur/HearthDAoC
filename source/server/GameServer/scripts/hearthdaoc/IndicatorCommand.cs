using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Events;
using DOL.GS.Commands;
using DOL.GS.PacketHandler;
using DOL.GS.Quests;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: /indicator, the GM's probe for the quest indicators on the targeted NPC. Everything it sends goes to
// the GM's own client: "effect" sends the quest effect with a raw byte, "create" makes the NPC's create packet carry
// a chosen indicator (IndicatorOverrides, checked first in GameNPC.GetQuestIndicator) and re-creates the NPC,
// "refresh" re-sends the real indicator both ways, and "show" says what the real indicator is and why.
// QuestIndicatorProbe makes every decision; this class gathers the facts and sends the packets.
[CmdAttribute(
    "&indicator",
    ePrivLevel.GM,
    "HearthDAoC: test the quest indicators of your target NPC (only your client sees them)",
    "/indicator - its real indicator for you, and why",
    "/indicator effect <0-255> - send the quest effect packet with this raw byte",
    "/indicator create <none|available|finish|lesson|lore|pending> - re-create it with this indicator in its create packet",
    "/indicator refresh - re-create it and send the effect, both with its real indicator",
    "/indicator clear - drop your create overrides and re-create it")]
public sealed class IndicatorCommandHandler : AbstractCommandHandler, ICommandHandler
{
    public void OnCommand(GameClient client, string[] args)
    {
        IndicatorRequest request = QuestIndicatorProbe.Parse(args);
        if (request.Action == IndicatorAction.Syntax)
        {
            DisplaySyntax(client);
            return;
        }
        if (request.Error != null)
        {
            Say(client, request.Error);
            return;
        }
        GamePlayer gm = client.Player;
        GameNPC npc = gm.TargetObject as GameNPC;
        if (request.Action == IndicatorAction.Clear)
        {
            Clear(client, npc);
            return;
        }
        if (npc == null)
        {
            Say(client, "Target an NPC first.");
            return;
        }
        switch (request.Action)
        {
            case IndicatorAction.Show:
                Show(client, npc);
                break;
            case IndicatorAction.Effect:
                gm.Out.SendNPCsQuestEffect(npc, (eQuestIndicator)request.Effect);
                Say(client, $"Sent {npc.Name}'s quest effect with byte {request.Effect} (0x{request.Effect:X2}), to you only. " +
                    "/indicator refresh puts the real one back.");
                break;
            case IndicatorAction.Create:
                Create(client, npc, request.Indicator);
                break;
            case IndicatorAction.Refresh:
                Refresh(client, npc);
                break;
        }
    }

    private static void Say(GameClient client, string text) =>
        client.Out.SendMessage(text, eChatType.CT_System, eChatLoc.CL_SystemWindow);

    // The NPC disappears from this client and comes back with a new create packet, as GameNPC.MoveInRegion does for
    // everyone: ClientService sends the create, the equipment, the GM's target again, and notes the NPC as created.
    private static void Recreate(GamePlayer gm, GameNPC npc)
    {
        gm.Out.SendObjectRemove(npc);
        ClientService.CreateObjectForPlayer(gm, npc);
    }

    private static void Create(GameClient client, GameNPC npc, eQuestIndicator indicator)
    {
        GamePlayer gm = client.Player;
        IndicatorOverrides.Store.Set(gm, npc, indicator);
        Recreate(gm, npc);
        string name = QuestIndicatorProbe.Name(indicator);
        Say(client, $"Re-created {npc.Name} for you only, its create packet saying {name} " +
            $"({QuestIndicatorProbe.CreateFlag(indicator)}). Kept until /indicator clear or refresh, your logout, or its leaving the world.");
        // A class that decides its own indicator without asking GameNPC's never sees the override.
        eQuestIndicator sent = npc.GetQuestIndicator(gm);
        if (sent != indicator)
            Say(client, $"But its class, {npc.GetType().Name}, overrides GetQuestIndicator and gave " +
                $"{QuestIndicatorProbe.Name(sent)}: the create packet carried that.");
    }

    private static void Refresh(GameClient client, GameNPC npc)
    {
        GamePlayer gm = client.Player;
        bool dropped = IndicatorOverrides.Store.Remove(gm, npc);
        eQuestIndicator real = IndicatorOverrides.Real(npc, gm);
        Recreate(gm, npc);
        gm.Out.SendNPCsQuestEffect(npc, real);
        Say(client, $"Re-sent {npc.Name}'s real indicator, {QuestIndicatorProbe.Name(real)}, to you only: the create packet " +
            $"({QuestIndicatorProbe.CreateFlag(real)}), then the quest effect (byte {(byte)real})." +
            (dropped ? " Your /indicator create value on it is dropped." : string.Empty));
    }

    private static void Clear(GameClient client, GameNPC target)
    {
        GamePlayer gm = client.Player;
        List<GameNPC> npcs = IndicatorOverrides.Store.RemoveGm(gm).OfType<GameNPC>().ToList();
        int dropped = npcs.Count;
        if (target != null && !npcs.Contains(target))
            npcs.Add(target);
        // Only NPCs still in the world near the GM are re-created; the others show afresh when next in view.
        List<GameNPC> shown = npcs.Where(n => n.ObjectState == GameObject.eObjectState.Active
            && n.CurrentRegionID == gm.CurrentRegionID && n.IsWithinRadius(gm, WorldMgr.VISIBILITY_DISTANCE)).ToList();
        foreach (GameNPC npc in shown)
            Recreate(gm, npc);
        Say(client, (dropped == 0 ? "You had no /indicator create values."
                : $"Dropped your /indicator create values on {(dropped == 1 ? "1 NPC" : $"{dropped} NPCs")}.") +
            (shown.Count == 0 ? string.Empty : " Re-created for you: " + string.Join(", ", shown.Select(n => n.Name)) + "."));
    }

    private static void Show(GameClient client, GameNPC npc)
    {
        GamePlayer gm = client.Player;
        eQuestIndicator? forced = IndicatorOverrides.Store.TryGet(gm, npc, out eQuestIndicator value) ? value : null;
        IndicatorFacts facts = Gather(npc, gm, forced);
        Say(client, $"{npc.Name} ({npc.GetType().Name}) for {gm.Name}, level {gm.Level} {gm.CharacterClass.Name}:");
        foreach (string line in QuestIndicatorProbe.Explain(facts))
            Say(client, line);
    }

    private static IndicatorFacts Gather(GameNPC npc, GamePlayer player, eQuestIndicator? forced)
    {
        List<AbstractQuest> finished = player.GetFinishedQuests();
        var finishedNames = new List<string>();
        var finishedIds = new HashSet<int>();
        foreach (DataQuest quest in finished.OfType<DataQuest>())
        {
            finishedNames.Add(quest.Name);
            finishedIds.Add(quest.ID);
        }
        List<AbstractQuest> active = player.QuestList.Keys.ToList();
        var activeIds = new HashSet<int>(active.OfType<DataQuest>().Select(q => q.ID));

        var dataQuests = new List<GivenDataQuest>();
        foreach (DataQuest quest in npc.DataQuestList.ToList())
        {
            DOL.Database.DbDataQuest row = quest.DBDataQuest;
            IReadOnlySet<int> classes = EpicChain.ParseClasses(row.AllowedClasses);
            bool doneMax = finished.OfType<DataQuest>().Any(q => q.ID == quest.ID
                && (q.IsDoingQuest() || (q.Count >= quest.MaxQuestCount && quest.MaxQuestCount >= 0)));
            dataQuests.Add(new GivenDataQuest(quest.ID, quest.Name, quest.StartType.ToString(), quest.ShowIndicator,
                quest.CheckQuestQualification(player), row.MinLevel, row.MaxLevel,
                classes.Count == 0 || classes.Contains(player.CharacterClass.ID), activeIds.Contains(quest.ID), doneMax,
                QuestDependencies.AreMet(EpicChain.ParseDependencies(row.QuestDependency), finishedNames, finishedIds, activeIds),
                row.QuestDependency));
        }

        var scripted = new List<GivenScriptedQuest>();
        foreach (AbstractQuest quest in npc.QuestListToGive.OfType<AbstractQuest>().ToList())
        {
            Type type = quest.GetType();
            scripted.Add(new GivenScriptedQuest(quest.Name, quest.CheckQuestQualification(player),
                player.HasFinishedQuest(type), player.IsDoingQuest(type) != null, quest.MaxQuestCount));
        }

        // GameNPC.CanFinishOneQuest's test, for every step type.
        var steps = new List<StepAtNpc>();
        var rewards = new List<string>();
        foreach (AbstractQuest quest in active)
        {
            if (quest is DataQuest dataQuest && QuestNames.Same(dataQuest.TargetName, npc.Name)
                && (dataQuest.TargetRegion == 0 || dataQuest.TargetRegion == npc.CurrentRegionID))
                steps.Add(new StepAtNpc(dataQuest.ID, dataQuest.Name, dataQuest.Step, dataQuest.StepType));
            if (quest is RewardQuest rewardQuest && rewardQuest.QuestGiver == npc && rewardQuest.Goals.All(g => g.IsAchieved))
                rewards.Add(rewardQuest.Name);
        }

        Type declaring = npc.GetType().GetMethod(nameof(GameNPC.GetQuestIndicator), new[] { typeof(GamePlayer) })?.DeclaringType;
        string overriding = declaring == null || declaring == typeof(GameNPC) ? null : declaring.Name;
        return new IndicatorFacts(player.Level, dataQuests, scripted, steps, rewards, overriding,
            IndicatorOverrides.Real(npc, player), forced);
    }
}

// HearthDAoC: the /indicator create values. GameNPC.GetQuestIndicator asks TryGet first (one marked block), so only
// the GM who set a value ever gets it. A value lasts until /indicator clear or refresh, the GM's logout, or the NPC
// leaving the world (death included).
public static class IndicatorOverrides
{
    public static readonly IndicatorOverrideStore Store = new();

    // Set while Real asks for the indicator without the GM's value.
    [ThreadStatic]
    private static bool t_real;

    public static bool TryGet(GamePlayer player, GameNPC npc, out eQuestIndicator indicator)
    {
        indicator = eQuestIndicator.None;
        return Store.Count > 0 && !t_real && Store.TryGet(player, npc, out indicator);
    }

    // What the NPC shows the player without any /indicator create value.
    public static eQuestIndicator Real(GameNPC npc, GamePlayer player)
    {
        t_real = true;
        try
        {
            return npc.GetQuestIndicator(player);
        }
        finally
        {
            t_real = false;
        }
    }

    [ScriptLoadedEvent]
    public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.AddHandler(GamePlayerEvent.Quit, OnPlayerQuit);
        GameEventMgr.AddHandler(GameObjectEvent.RemoveFromWorld, OnRemoveFromWorld);
    }

    [ScriptUnloadedEvent]
    public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.RemoveHandler(GamePlayerEvent.Quit, OnPlayerQuit);
        GameEventMgr.RemoveHandler(GameObjectEvent.RemoveFromWorld, OnRemoveFromWorld);
        Store.Clear();
    }

    private static void OnPlayerQuit(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is GamePlayer player && Store.Count > 0)
            Store.RemoveGm(player);
    }

    // Fires for every object leaving the world, so an empty store returns at once.
    private static void OnRemoveFromWorld(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is GameNPC npc && Store.Count > 0)
            Store.RemoveNpc(npc);
    }
}
