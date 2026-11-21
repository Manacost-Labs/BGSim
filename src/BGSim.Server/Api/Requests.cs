namespace BGSim.Server.Api;

// The public request contract (docs/server.md). Kept apart from the simulator's own records so that those can change
// without breaking clients. Every member is optional in JSON; BoardMapper says which are required and checks ranges.

/// <summary>What /v1/simulate and /v1/fight both read: the two sides and who attacks first.</summary>
public abstract class BattleInput
{
    public SideDto? Player { get; set; }

    public SideDto? Opponent { get; set; }

    /// <summary>"player", "opponent", or null/absent: the game's rule (more minions first, a coin flip when equal).</summary>
    public string? FirstAttacker { get; set; }

    /// <summary>Entity id of the minion that attacks first, when known; attack order starts from it.</summary>
    public int? FirstAttackerEntityId { get; set; }

    /// <summary>The lobby's tribes (random summons draw from them); absent = every tribe.</summary>
    public List<string>? LobbyTribes { get; set; }
}

public sealed class SimulateRequest : BattleInput
{
    public OptionsDto? Options { get; set; }
}

public sealed class FightRequest : BattleInput
{
    /// <summary>Seed of the one fight; absent = the boards' own seed (the same boards give the same fight).</summary>
    public int? Seed { get; set; }
}

public sealed class OptionsDto
{
    public int? Runs { get; set; }

    public int? TimeBudgetMs { get; set; }

    /// <summary>Stop once the 95 % interval of win, tie and loss is at most this wide either side; 0 = run all.</summary>
    public double? TargetHalfWidth { get; set; }

    /// <summary>The lobby's combat damage cap; 0 = none.</summary>
    public int? DamageCap { get; set; }

    public bool? CountDamage { get; set; }
}

public sealed class SideDto
{
    public string? Hero { get; set; }

    public string? HeroPower { get; set; }

    /// <summary>Hero health plus armor when the fight starts; 0/absent = unknown (no lethal estimate).</summary>
    public int? Health { get; set; }

    public int? TavernTier { get; set; }

    public int? Turn { get; set; }

    /// <summary>Lobby PLAYER_ID of the hero: the lower one entered play first (death order).</summary>
    public int? PlayerId { get; set; }

    public List<MinionDto>? Board { get; set; }

    /// <summary>Player-level cards: trinkets, quest rewards, the anomaly, the Deity, objectives.</summary>
    public List<string>? Extras { get; set; }

    /// <summary>Numeric player tags by the name Power.log prints (Blood Gem values, tavern spell bonuses...).</summary>
    public Dictionary<string, int>? Tags { get; set; }

    /// <summary>Numeric tags of player-level cards by card id (a hero power's growing value...).</summary>
    public Dictionary<string, Dictionary<string, int>>? ExtraTags { get; set; }

    public List<MinionDto>? Hand { get; set; }

    public List<string>? Secrets { get; set; }

    /// <summary>Enchantments on the player entity (Beetle Army bonus, pending summons...).</summary>
    public List<EnchantmentDto>? Enchantments { get; set; }

    /// <summary>This side's minions that died during Start of Combat, before the fight, in death order.</summary>
    public List<MinionDto>? DeathsBeforeCombat { get; set; }

    /// <summary>Minions a player-level card removed at Start of Combat, by that card's id.</summary>
    public Dictionary<string, List<MinionDto>>? RemovedByExtras { get; set; }

    public DeityDto? DeityAwakening { get; set; }

    /// <summary>Inputs the client estimated instead of reading (values of the opponent the player cannot see).</summary>
    public List<EstimateDto>? Estimates { get; set; }

    /// <summary>Duos: the teammate's board that enters once this one is empty. A teammate has no teammate.</summary>
    public SideDto? Teammate { get; set; }
}

public sealed class MinionDto
{
    public string? CardId { get; set; }

    public bool? Golden { get; set; }

    public int? Attack { get; set; }

    public int? Health { get; set; }

    public int? MaxHealth { get; set; }

    // Current keywords; absent = as printed on the card (the pool's data), present = exactly this.
    public bool? Taunt { get; set; }

    public bool? DivineShield { get; set; }

    public bool? Poisonous { get; set; }

    public bool? Venomous { get; set; }

    public bool? Reborn { get; set; }

    public bool? Windfury { get; set; }

    public bool? MegaWindfury { get; set; }

    public bool? Stealth { get; set; }

    /// <summary>HearthstoneJSON race names (BEAST, MECHANICAL, ALL...); absent = from the pool.</summary>
    public List<string>? Tribes { get; set; }

    /// <summary>Tavern tier of the card; absent = from the pool.</summary>
    public int? Tier { get; set; }

    /// <summary>Printed Attack (golden-aware) a Reborn copy comes back with; absent = from the pool.</summary>
    public int? BaseAttack { get; set; }

    /// <summary>Printed Health (golden-aware); absent = from the pool.</summary>
    public int? BaseHealth { get; set; }

    /// <summary>Printed keywords as HearthstoneJSON mechanics (TAUNT, DIVINE_SHIELD...); absent = from the pool.</summary>
    public List<string>? BaseKeywords { get; set; }

    /// <summary>Printed mechanics (DEATHRATTLE, BACON_RALLY, AVENGE, BATTLECRY, START_OF_COMBAT, MAGNETIC); absent = from the pool.</summary>
    public List<string>? Mechanics { get; set; }

    /// <summary>
    /// Effect marker tags the minion carries in the game (DEATHRATTLE, TRIGGER_VISUAL, AURA, AVENGE, OVERKILL, BACON_RALLY):
    /// for a card no table knows, and for an effect an enchantment granted. Absent = not known.
    /// </summary>
    public List<string>? EffectMarkers { get; set; }

    public List<EnchantmentDto>? Enchantments { get; set; }

    public int? ScriptData1 { get; set; }

    public int? ScriptData2 { get; set; }

    public int? ScriptData3 { get; set; }

    public int? ScriptData4 { get; set; }

    public int? EntityId { get; set; }

    public string? CreatorCardId { get; set; }

    public int? BloodGemAttack { get; set; }

    public int? BloodGemHealth { get; set; }

    public int? FriendlyDeathsBeforeCombat { get; set; }

    public bool? SummonedInCombat { get; set; }

    /// <summary>Minions this one's Start of Combat removed from the board before the fight.</summary>
    public List<MinionDto>? Remembered { get; set; }

    /// <summary>Timewarped Nellie's Ship: the Pirates of its crew.</summary>
    public List<MinionDto>? Crew { get; set; }
}

public sealed class EnchantmentDto
{
    public string? CardId { get; set; }

    public int ScriptData1 { get; set; }

    public int ScriptData2 { get; set; }

    public int Attack { get; set; }

    public int Health { get; set; }

    public int CreatorEntityId { get; set; }
}

public sealed class DeityDto
{
    public int Progress { get; set; }

    public int Total { get; set; }
}

public sealed class EstimateDto
{
    public string? Input { get; set; }

    public int Value1 { get; set; }

    public int Value2 { get; set; }

    public string? Basis { get; set; }

    public List<string>? Tags { get; set; }
}
