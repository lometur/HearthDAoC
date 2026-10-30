using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.GS.Quests.Hibernia;

namespace DOL.GS.HearthDAoC.PlayerBots;

public readonly record struct PlayerBotWeapon(eObjectType Type, eInventorySlot Slot, eWeaponDamageType Damage = 0);

public sealed record PlayerBotKitResult(int ArmourLevel, int Armour, int Weapons, bool Shield, int Missing, string Weapon,
    eActiveWeaponSlot Active);

// HearthDAoC #137 level-matched run (levelmatch/DESIGN.md 2.3): gear by the GameBot's rules (BotEquipment.SetMeleeWeapon,
// SetShield and SetArmor, BotEquipment.cs:192-370; GameBot.SetWeapons and SetArmor), from one catalog read at start and
// kept for the run (DbItemTemplate is pre-cached anyway, and a bot that logs in late must still find it: REVIEW-FIXES 21),
// put on at the bot's first entered world. Not reused: the methods themselves (they take an IGamePlayer, query the
// database per call and pick with Util.Random), jewelry (the era rule: no ROGs), ranged weapons (the driver does not
// shoot) and instruments (no songs).
public static class PlayerBotKitGear
{
    public const int Window = 6;                                                  // BotEquipment.cs:202, :291, :319
    public static readonly eInventorySlot[] ArmourSlots =
        [eInventorySlot.HeadArmor, eInventorySlot.HandsArmor, eInventorySlot.FeetArmor, eInventorySlot.TorsoArmor,
         eInventorySlot.LegsArmor, eInventorySlot.ArmsArmor];                     // Item_Type 21, 22, 23, 25, 27, 28

    // BotEquipment.AddItem (BotEquipment.cs:512-522) puts these classes' shields in the backpack, never the left hand.
    public static readonly eCharacterClass[] NoShieldClasses =
        [eCharacterClass.Infiltrator, eCharacterClass.Mercenary, eCharacterClass.Nightshade, eCharacterClass.Ranger,
         eCharacterClass.Blademaster, eCharacterClass.Shadowblade, eCharacterClass.Berserker, eCharacterClass.Savage];

    private static volatile ILookup<(int Realm, int Type), DbItemTemplate> _catalog;

    /// <summary>On GameServerStarted: every pickable realm item up to the highest build level that is armour, a shield
    /// or a melee weapon, without the excluded looks. Returns the count kept.</summary>
    public static int Load(byte maxLevel)
    {
        int[] types = Enumerable.Range((int)eObjectType._FirstArmor, eObjectType._LastArmor - eObjectType._FirstArmor + 1)
            .Concat(Enumerable.Range((int)eObjectType._FirstWeapon, eObjectType._LastWeapon - eObjectType._FirstWeapon + 1)
                .Where(t => BotWeaponStats.IsMeleeWeapon((eObjectType)t)))
            .Append((int)eObjectType.Shield).ToArray();
        IList<DbItemTemplate> rows = GameServer.Database.SelectObjects<DbItemTemplate>(
            DB.Column("Level").IsLessOrEqualTo((int)maxLevel)
                .And(DB.Column("IsPickable").IsEqualTo(1))
                .And(DB.Column("Realm").IsIn(new[] { 1, 2, 3 }))                    // CoreDatabase/WhereClause.cs:425
                .And(DB.Column("Object_Type").IsIn(types)));
        DbItemTemplate[] kept = rows.Where(t => !BotCrossRealmGear.IsExcluded(t) && !SluaghbinderEpicArmor.IsSetItem(t) &&
            BotWeaponStats.HasFunctionalMeleeStats(t)).ToArray();                   // BotEquipment.cs:70-72, :219
        _catalog = kept.ToLookup(t => (t.Realm, t.Object_Type));
        return kept.Length;
    }

    /// <summary>The GameBot's pick (one at random among realm items of level N-6..N; GameBot.EquipBot caps the window
    /// at the bot's level, GameBot.cs:3882-3883) made repeatable: a stable hash of the bot's seed and the slot over the
    /// candidates in Id_nb order, so a leg and its rerun give each bot the same item.</summary>
    public static DbItemTemplate Pick(IEnumerable<DbItemTemplate> candidates, int level, long seed, int slot)
    {
        DbItemTemplate[] fit = candidates.Where(t => t.Level >= Math.Max(1, level - Window) && t.Level <= level)
            .OrderBy(t => t.Id_nb, StringComparer.Ordinal).ToArray();
        return fit.Length == 0 ? null : fit[(int)(Mix(seed, slot) % (ulong)fit.Length)];
    }

    private static ulong Mix(long seed, int slot)                                // the mixer of BotSpec.cs:124-129
    {
        unchecked
        {
            ulong value = (ulong)seed + 0x9E3779B97F4A7C15UL + (ulong)slot * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    /// <summary>BotEquipment.SetMeleeWeapon's hand rule (BotEquipment.cs:223-235); the right hand also drops two-hand
    /// and left-only items, as the companion rule does (:89-91), but for a hand-to-hand claw: every one is marked
    /// left-hand capable (Hand 2), the inventory takes it in the main hand (GamePlayerInventory.cs:783-797), and a
    /// GameBot Savage wears one there (SavageBotWeaponPolicy, Savage.cs:157-163). #167 E (CY1): Midgard has right-hand
    /// fist weapons at a few levels only (5, 30, 40, 42, 45, 49-51), so 4 Hand-to-Hand Savages at 28-39 kept the
    /// starter axe (missing=1) with no axe spec and no style: 12 deaths in 1.9 bot-hours over st-deathsD2 and D2b. At
    /// the levels with right-hand fist weapons the right hand now picks among those and the claws.</summary>
    public static bool FitsHand(DbItemTemplate t, eInventorySlot slot, eWeaponDamageType damage) => slot switch
    {
        eInventorySlot.RightHandWeapon => t.Item_Type is Slot.RIGHTHAND or Slot.LEFTHAND && t.Hand != 1 &&
            (t.Hand != 2 || t.Object_Type == (int)eObjectType.HandToHand),
        eInventorySlot.LeftHandWeapon => t.Item_Type == Slot.LEFTHAND,
        eInventorySlot.TwoHandWeapon => t.Item_Type == Slot.TWOHAND && (damage == 0 || t.Type_Damage == (int)damage),
        _ => false,
    };

    /// <summary>A class may use it: AllowedClasses (the rule's own check skips a GamePlayer, AbstractServerRules.cs:664-668,
    /// since it is no IGamePlayer) and LevelRequirement.</summary>
    public static bool FitsClass(DbItemTemplate t, int classId, int level) =>
        t.LevelRequirement <= level &&
        (string.IsNullOrWhiteSpace(t.AllowedClasses) || Util.SplitCSV(t.AllowedClasses, true).Contains(classId.ToString()));

    /// <summary>GameBot.SetWeapons (GameBot.cs:3964-4044) without the Sluaghbinder, instrument and Savage special cases;
    /// a slot named twice keeps its first weapon.</summary>
    public static IReadOnlyList<PlayerBotWeapon> WeaponPlan(BotSpec spec, eCharacterClass cls, eClassType classType)
    {
        eObjectType w1 = spec.WeaponOneType, w2 = spec.WeaponTwoType;
        var R = eInventorySlot.RightHandWeapon; var L = eInventorySlot.LeftHandWeapon; var T = eInventorySlot.TwoHandWeapon;
        PlayerBotWeapon[] plan = spec.SpecType switch
        {
            eSpecType.DualWield or eSpecType.DualWieldAndShield => [new(w1, R), new(w1, L)],
            eSpecType.LeftAxe => [new(w1, R), new(w1, T), new(w2, L)],
            eSpecType.OneHandAndShield => [new(w1, R)],
            eSpecType.OneHandHybrid or eSpecType.TwoHandHybrid => [new(w1, R), new(w2, T, spec.DamageType)],
            eSpecType.TwoHanded when cls != eCharacterClass.Valewalker => [new(w1, R), new(w2, T, spec.DamageType)],
            eSpecType.Mid or eSpecType.PacHealer or eSpecType.AugHealer or eSpecType.MendHealer or eSpecType.MendShaman or
                eSpecType.AugShaman or eSpecType.SubtShaman => [new(w1, R), new(w1, T)],
            eSpecType.Instrument => [new(w1, R)],
            _ when classType == eClassType.ListCaster || cls is eCharacterClass.Friar or eCharacterClass.Valewalker or
                eCharacterClass.Hunter => [new(w1, T)],
            _ => [new(w1, R)],
        };
        return plan.Where(w => w.Type != 0).GroupBy(w => w.Slot).Select(g => g.First()).ToArray();
    }

    /// <summary>GameBot.EquipBot's shield (GameBot.cs:3897-3905, :4468-4471): the Shield ability's size exactly
    /// (BotEquipment.SetShield, :274-305, has no fallback), in a left hand the weapon plan leaves free, for a class whose
    /// shield is not stowed (NoShieldClasses). 0: no shield.</summary>
    public static int ShieldSize(eCharacterClass cls, int shieldAbility, IEnumerable<PlayerBotWeapon> plan) =>
        shieldAbility > 0 && !NoShieldClasses.Contains(cls) && plan.All(w => w.Slot != eInventorySlot.LeftHandWeapon)
            ? shieldAbility : 0;

    /// <summary>GameBot.SetArmor (GameBot.cs:4489-4505): the best armour ability level, Hibernia's names at 3 and 4.</summary>
    public static eObjectType ArmourType(eRealm realm, int armourLevel) => armourLevel switch
    {
        1 => eObjectType.Cloth,
        2 => eObjectType.Leather,
        3 => realm == eRealm.Hibernia ? eObjectType.Reinforced : eObjectType.Studded,
        4 => realm == eRealm.Hibernia ? eObjectType.Scale : eObjectType.Chain,
        5 => eObjectType.Plate,
        _ => 0,
    };

    /// <summary>GameBot.SetWeapons' last step (GameBot.cs:4044-4050, :4080-4087): two hands for a planned two-hander from
    /// its unlock level, a list caster, a Friar or a Valewalker; else the standard slot.</summary>
    public static eActiveWeaponSlot ActiveSlot(BotSpec spec, eCharacterClass cls, eClassType classType, int level,
        bool hasRight, bool hasTwoHand)
    {
        bool twoHands = GameBot.ShouldUsePlannedTwoHandedPrimary(cls, level, spec.Is2H, spec.WeaponTwoType) ||
            classType == eClassType.ListCaster || cls is eCharacterClass.Friar or eCharacterClass.Valewalker;
        return hasTwoHand && (twoHands || !hasRight) ? eActiveWeaponSlot.TwoHanded : eActiveWeaponSlot.Standard;
    }

    /// <summary>On the game loop at the bot's first entered world (inventory loaded). A slot with no candidate, or whose
    /// item the inventory refuses, keeps what it has (the starter item, if any) and counts as missing (REVIEW-FIXES 23).
    /// Then the skill caches (G1) and full health, power and endurance.</summary>
    public static PlayerBotKitResult Equip(GamePlayer player, PlayerBotKitPlan kit, long seed)
    {
        ILookup<(int, int), DbItemTemplate> catalog = _catalog;
        int level = player.Level, realm = (int)player.Realm, classId = player.CharacterClass.ID, missing = 0, weapons = 0, armour = 0;
        eClassType classType = player.CharacterClass.ClassType;
        bool Usable(DbItemTemplate t) => FitsClass(t, classId, level) && GameServer.ServerRules.CheckAbilityToUseItem(player, t);
        IEnumerable<DbItemTemplate> Of(eObjectType type) => catalog?[(realm, (int)type)] ?? [];
        bool Put(eInventorySlot slot, DbItemTemplate t)
        {
            if (t == null) return false;
            DbInventoryItem old = player.Inventory.GetItem(slot);
            if (old != null) player.Inventory.RemoveItem(old);
            if (player.Inventory.AddItem(slot, GameInventoryItem.Create(t))) return true;
            if (old != null) player.Inventory.AddItem(slot, old);                     // back off the deletion list
            return false;
        }

        IReadOnlyList<PlayerBotWeapon> plan = WeaponPlan(kit.Spec, kit.Build.Class, classType);
        foreach (PlayerBotWeapon w in plan)
        {
            if (Put(w.Slot, Pick(Of(w.Type).Where(x => FitsHand(x, w.Slot, w.Damage) && Usable(x)), level, seed, (int)w.Slot))) weapons++;
            else missing++;
        }

        bool shield = false;
        int size = ShieldSize(kit.Build.Class, player.GetAbilityLevel(Abilities.Shield), plan);
        if (size > 0)
        {
            shield = Put(eInventorySlot.LeftHandWeapon,
                Pick(Of(eObjectType.Shield).Where(x => x.Type_Damage == size && Usable(x)), level, seed, Slot.SHIELD));
            if (!shield) missing++;
        }

        int armourLevel = player.BestArmorLevel;                                       // GamePlayer.cs:3152
        eObjectType armourType = ArmourType(player.Realm, armourLevel);
        foreach (eInventorySlot slot in ArmourSlots)
        {
            DbItemTemplate t = armourType == 0 ? null :
                Pick(Of(armourType).Where(x => x.Item_Type == (int)slot && Usable(x)), level, seed, (int)slot);
            if (Put(slot, t)) armour++;
            else missing++;
        }

        bool hasRight = player.Inventory.GetItem(eInventorySlot.RightHandWeapon) != null;
        bool hasTwo = player.Inventory.GetItem(eInventorySlot.TwoHandWeapon) != null;
        eActiveWeaponSlot active = ActiveSlot(kit.Spec, kit.Build.Class, classType, level, hasRight, hasTwo);
        player.SwitchWeapon(active);
        player.Out.SendUpdatePlayerSkills(true);                                       // the caches (G1)
        player.Health = player.MaxHealth;
        player.Mana = player.MaxMana;
        player.Endurance = player.MaxEndurance;
        string weapon = player.ActiveWeapon is DbInventoryItem a ? ((eObjectType)a.Object_Type).ToString() : "-";
        return new(armourLevel, armour, weapons, shield, missing, weapon, active);
    }
}
