using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using Hearthpulse.Core.Battlegrounds.Effects.Secrets;

namespace BGSim.Server.Api;

/// <summary>Input errors with the path where each was found; at most <see cref="MaxErrors"/> are kept.</summary>
internal sealed class InputErrors
{
    public const int MaxErrors = 20;

    private readonly List<ErrorItem> _items = [];

    public bool Any => _items.Count > 0;

    public IReadOnlyList<ErrorItem> Items => _items;

    public void Add(string path, string message)
    {
        if (_items.Count < MaxErrors)
        {
            _items.Add(new ErrorItem(path, message));
        }
    }
}

/// <summary>A checked request in the simulator's terms.</summary>
internal sealed record Battle(SimSide Player, SimSide Opponent, bool? PlayerFirst, int FirstAttackerEntityId);

/// <summary>
/// Checks a request and turns it into the simulator's <see cref="SimSide"/>s. A pool minion's missing printed data (tribes,
/// tier, base stats, base keywords, mechanics) comes from the generated pool table, doubled for a golden card; what the
/// client sends wins. The "not simulated" flag follows the tracker's rule (CombatRelevance.IsUnsimulated), so the answer
/// says honestly when it is an approximation. Nothing is guessed about the opponent beyond the card tables.
/// </summary>
internal static class BoardMapper
{
    public const int MaxBoard = 7;
    public const int MaxHand = 10;
    public const int MaxExtras = 16;
    public const int MaxSecrets = 8;
    public const int MaxEnchantments = 32;
    public const int MaxNested = 7;
    public const int MaxDeaths = 32;
    public const int MaxTags = 128;
    public const int MaxExtraTagCards = 16;
    public const int MaxEstimates = 16;
    public const int MaxNames = 32;
    public const int MaxIdLength = 64;
    public const int MaxBasisLength = 200;

    /// <summary>Largest Attack, Health or script number accepted: far above real boards, far below int overflow.</summary>
    public const int MaxStat = 10_000_000;

    private const Keyword KeptWithoutCardData = Keyword.Taunt | Keyword.Poisonous | Keyword.Windfury | Keyword.MegaWindfury;

    /// <summary>The effect marker tags (CombatRelevance.EffectMechanics) and those an enchantment can grant.</summary>
    private static readonly string[] EffectMarkers = ["DEATHRATTLE", "TRIGGER_VISUAL", "AURA", "AVENGE", "OVERKILL", "BACON_RALLY"];

    private static readonly string[] GrantableMarkers = ["DEATHRATTLE", "BACON_RALLY", "AVENGE"];

    private static readonly Dictionary<string, SimMinion> Pool = BuildPool();

    public static int PoolSize => Pool.Count;

    public static Battle? Map(BattleInput input, InputErrors errors)
    {
        var lobby = input.LobbyTribes is { } tribes ? Tribes(tribes, "lobbyTribes", errors) : Tribe.None;
        bool? playerFirst = null;
        if (input.FirstAttacker is { } first)
        {
            if (string.Equals(first, "player", StringComparison.OrdinalIgnoreCase))
            {
                playerFirst = true;
            }
            else if (string.Equals(first, "opponent", StringComparison.OrdinalIgnoreCase))
            {
                playerFirst = false;
            }
            else
            {
                errors.Add("firstAttacker", "must be \"player\", \"opponent\" or absent");
            }
        }

        var firstEntity = Range(input.FirstAttackerEntityId, "firstAttackerEntityId", 0, MaxStat, errors);
        var player = Side(input.Player, "player", isOpponent: false, lobby, errors, teammateAllowed: true);
        var opponent = Side(input.Opponent, "opponent", isOpponent: true, lobby, errors, teammateAllowed: true);
        return errors.Any || player is null || opponent is null ? null : new Battle(player, opponent, playerFirst, firstEntity);
    }

    private static Dictionary<string, SimMinion> BuildPool()
    {
        var pool = new Dictionary<string, SimMinion>(StringComparer.Ordinal);
        foreach (var minion in MinionPool.Minions)
        {
            pool.TryAdd(minion.CardId, minion);
        }

        return pool;
    }

    private static SimSide? Side(SideDto? dto, string path, bool isOpponent, Tribe lobby, InputErrors errors, bool teammateAllowed)
    {
        if (dto is null)
        {
            errors.Add(path, "is required");
            return null;
        }

        var heroPower = OptionalId(dto.HeroPower, $"{path}.heroPower", errors);
        var extras = Ids(dto.Extras, $"{path}.extras", MaxExtras, errors);
        // A hero power is a player-level card like a trinket (the tracker puts it among the extras): its effect runs from there.
        if (heroPower.Length > 0)
        {
            extras.Insert(0, heroPower);
        }

        SimSide? teammate = null;
        if (dto.Teammate is { } mate)
        {
            if (teammateAllowed)
            {
                teammate = Side(mate, $"{path}.teammate", isOpponent, lobby, errors, teammateAllowed: false);
            }
            else
            {
                errors.Add($"{path}.teammate", "a teammate has no teammate of its own");
            }
        }

        return new SimSide(Minions(dto.Board, $"{path}.board", MaxBoard, isOpponent, MinionKind.Board, errors))
        {
            HeroCardId = OptionalId(dto.Hero, $"{path}.hero", errors),
            HeroPowerCardId = heroPower,
            Extras = extras.Distinct(StringComparer.Ordinal).ToArray(),
            Hand = Minions(dto.Hand, $"{path}.hand", MaxHand, isOpponent, MinionKind.Hand, errors),
            Tags = Tags(dto.Tags, $"{path}.tags", errors),
            ExtraTags = ExtraTags(dto.ExtraTags, $"{path}.extraTags", errors),
            RemovedByExtras = Removed(dto.RemovedByExtras, $"{path}.removedByExtras", isOpponent, errors),
            TavernTier = Range(dto.TavernTier, $"{path}.tavernTier", 0, 7, errors),
            Health = Range(dto.Health, $"{path}.health", 0, 1000, errors),
            Turn = Range(dto.Turn, $"{path}.turn", 0, 200, errors),
            PlayerId = Range(dto.PlayerId, $"{path}.playerId", 0, 64, errors),
            Secrets = Ids(dto.Secrets, $"{path}.secrets", MaxSecrets, errors).ToArray(),
            PlayerEnchantments = Enchantments(dto.Enchantments, $"{path}.enchantments", errors),
            DeathsBeforeCapture = Minions(dto.DeathsBeforeCombat, $"{path}.deathsBeforeCombat", MaxDeaths, isOpponent, MinionKind.Gone, errors),
            LobbyTribes = lobby,
            Estimates = Estimates(dto.Estimates, $"{path}.estimates", errors),
            DeityAwakening = Deity(dto.DeityAwakening, $"{path}.deityAwakening", errors),
            Teammate = teammate,
        };
    }

    /// <summary>Where a minion stands, which decides its checks: on the board, in hand, or gone (dead or removed before the fight).</summary>
    private enum MinionKind
    {
        Board,
        Hand,
        Gone,
        Nested,
    }

    private static SimMinion[] Minions(List<MinionDto>? list, string path, int max, bool isOpponent, MinionKind kind, InputErrors errors)
    {
        if (list is null || list.Count == 0)
        {
            return [];
        }

        if (list.Count > max)
        {
            errors.Add(path, $"at most {max} minions");
            return [];
        }

        var minions = new List<SimMinion>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var itemPath = $"{path}[{i}]";
            if (list[i] is not { } dto)
            {
                errors.Add(itemPath, "must be an object");
                continue;
            }

            if (Minion(dto, itemPath, isOpponent, kind, errors) is { } minion)
            {
                minions.Add(minion);
            }
        }

        return minions.ToArray();
    }

    private static SimMinion? Minion(MinionDto dto, string path, bool isOpponent, MinionKind kind, InputErrors errors)
    {
        var id = RequiredId(dto.CardId, $"{path}.cardId", errors);
        // A minion that died or was removed before the fight may show its Health at that moment (0 or less).
        var minHealth = kind is MinionKind.Gone or MinionKind.Nested ? -MaxStat : 1;
        var attack = Required(dto.Attack, $"{path}.attack", 0, MaxStat, errors);
        var health = Required(dto.Health, $"{path}.health", minHealth, MaxStat, errors);
        var maxHealth = Range(dto.MaxHealth, $"{path}.maxHealth", 0, MaxStat, errors);
        if (dto.MaxHealth is not null && maxHealth > 0 && maxHealth < health)
        {
            errors.Add($"{path}.maxHealth", "must be at least health");
        }

        var (cardId, goldenId) = id.Length > 0 ? CombatRelevance.Normalize(id) : (id, false);
        var golden = goldenId || dto.Golden == true;
        var printed = Pool.GetValueOrDefault(cardId);
        var scale = golden ? 2 : 1;

        var baseKeywords = dto.BaseKeywords is { } given ? Traits.KeywordsFromMechanics(Names(given, $"{path}.baseKeywords", errors)) : printed?.BaseKeywords;
        var printedKeywords = baseKeywords ?? Keyword.None;
        bool Has(bool? explicitValue, Keyword keyword) => explicitValue ?? (printedKeywords & keyword) != 0;
        var keywords = (Has(dto.Taunt, Keyword.Taunt) ? Keyword.Taunt : 0)
            | (Has(dto.DivineShield, Keyword.DivineShield) ? Keyword.DivineShield : 0)
            | (Has(dto.Poisonous, Keyword.Poisonous) ? Keyword.Poisonous : 0)
            | (Has(dto.Venomous, Keyword.Venomous) ? Keyword.Venomous : 0)
            | (Has(dto.Reborn, Keyword.Reborn) ? Keyword.Reborn : 0)
            | (Has(dto.Windfury, Keyword.Windfury) ? Keyword.Windfury : 0)
            | (Has(dto.MegaWindfury, Keyword.MegaWindfury) ? Keyword.MegaWindfury : 0)
            | (Has(dto.Stealth, Keyword.Stealth) ? Keyword.Stealth : 0);
        // The tracker's rule without card data: a reborn copy keeps the lasting keywords the minion shows.
        baseKeywords ??= keywords & KeptWithoutCardData;

        var markers = dto.EffectMarkers is { } markerNames
            ? Names(markerNames, $"{path}.effectMarkers", errors).Where(m => EffectMarkers.Contains(m, StringComparer.Ordinal)).ToArray()
            : null;
        var printedMechanics = dto.Mechanics is { } mechanicNames
            ? Traits.MechanicsFrom(Names(mechanicNames, $"{path}.mechanics", errors))
            : printed?.Mechanics;
        var mechanics = (printedMechanics ?? CardMechanic.None) | Traits.MechanicsFrom(markers?.Where(m => GrantableMarkers.Contains(m, StringComparer.Ordinal)));

        var enchantments = Enchantments(dto.Enchantments, $"{path}.enchantments", errors);
        var enchantmentIds = enchantments.Select(e => e.CardId).ToArray();

        var unsimulated = false;
        if (kind != MinionKind.Hand && cardId.Length > 0)
        {
            var row = CombatRelevance.Find(cardId);
            // A card no table knows: its effect markers when the client sent them, else assume it may act (reported as "unknown").
            var relevant = row is null ? markers is null || markers.Length > 0 : row.Relevance == Relevance.Relevant;
            // A deathrattle, Avenge or Rally the card does not print came from an enchantment (Dark Gift...): known only with
            // both the markers and the printed mechanics, as in the tracker.
            IReadOnlyList<string> granted = markers is not null && printedMechanics is { } printedFlags
                ? CombatRelevance.GrantedMechanics(markers.Contains, MechanicNames(printedFlags))
                : [];
            unsimulated = CombatRelevance.IsUnsimulated(cardId, golden, relevant, granted, enchantmentIds, isOpponent, EffectRegistry.Default);
        }

        var nested = kind is MinionKind.Board or MinionKind.Hand;
        SimMinion[] Children(List<MinionDto>? list, string name, MinionKind childKind)
        {
            if (list is null || list.Count == 0)
            {
                return [];
            }

            if (!nested)
            {
                errors.Add($"{path}.{name}", "not allowed on a minion inside another list");
                return [];
            }

            return Minions(list, $"{path}.{name}", MaxNested, isOpponent, childKind, errors);
        }

        var remembered = Children(dto.Remembered, "remembered", MinionKind.Nested);
        var crew = Children(dto.Crew, "crew", MinionKind.Nested);
        var tier = Range(dto.Tier, $"{path}.tier", 0, 7, errors);
        var creator = OptionalId(dto.CreatorCardId, $"{path}.creatorCardId", errors);
        return new SimMinion(
            attack,
            health,
            (keywords & Keyword.Taunt) != 0,
            (keywords & Keyword.DivineShield) != 0,
            (keywords & Keyword.Poisonous) != 0,
            (keywords & Keyword.Venomous) != 0,
            (keywords & Keyword.Reborn) != 0,
            Traits.AttacksPerTurn(keywords),
            (keywords & Keyword.Stealth) != 0,
            Range(dto.BaseAttack, $"{path}.baseAttack", 0, MaxStat, errors, printed is not null ? printed.RebornAttack * scale : attack),
            unsimulated)
        {
            CardId = cardId,
            Golden = golden,
            EntityId = Range(dto.EntityId, $"{path}.entityId", 0, MaxStat, errors),
            Tribes = dto.Tribes is { } tribes ? Tribes(tribes, $"{path}.tribes", errors) : printed?.Tribes ?? Tribe.None,
            MaxHealth = maxHealth,
            BaseHealth = Range(dto.BaseHealth, $"{path}.baseHealth", 0, MaxStat, errors, printed is not null ? printed.BaseHealth * scale : 0),
            BaseKeywords = baseKeywords.Value,
            Mechanics = mechanics,
            TechLevel = dto.Tier is not null ? tier : printed?.TechLevel ?? 0,
            ScriptData1 = Range(dto.ScriptData1, $"{path}.scriptData1", -MaxStat, MaxStat, errors),
            ScriptData2 = Range(dto.ScriptData2, $"{path}.scriptData2", -MaxStat, MaxStat, errors),
            ScriptData3 = Range(dto.ScriptData3, $"{path}.scriptData3", -MaxStat, MaxStat, errors),
            ScriptData4 = Range(dto.ScriptData4, $"{path}.scriptData4", -MaxStat, MaxStat, errors),
            CreatorCardId = creator,
            Enchantments = enchantmentIds,
            EnchantmentValues = enchantments,
            Remembered = remembered,
            Crew = crew,
            BloodGemAttack = Range(dto.BloodGemAttack, $"{path}.bloodGemAttack", 0, MaxStat, errors),
            BloodGemHealth = Range(dto.BloodGemHealth, $"{path}.bloodGemHealth", 0, MaxStat, errors),
            FriendlyDeathsBeforeCapture = Range(dto.FriendlyDeathsBeforeCombat, $"{path}.friendlyDeathsBeforeCombat", 0, 1000, errors),
            SummonedInCombat = dto.SummonedInCombat ?? false,
        };
    }

    private static string[] MechanicNames(CardMechanic mechanics)
    {
        var names = new List<string>(3);
        if ((mechanics & CardMechanic.Deathrattle) != 0)
        {
            names.Add("DEATHRATTLE");
        }

        if ((mechanics & CardMechanic.Rally) != 0)
        {
            names.Add("BACON_RALLY");
        }

        if ((mechanics & CardMechanic.Avenge) != 0)
        {
            names.Add("AVENGE");
        }

        return names.ToArray();
    }

    private static SimEnchantment[] Enchantments(List<EnchantmentDto>? list, string path, InputErrors errors)
    {
        if (list is null || list.Count == 0)
        {
            return [];
        }

        if (list.Count > MaxEnchantments)
        {
            errors.Add(path, $"at most {MaxEnchantments} enchantments");
            return [];
        }

        var result = new SimEnchantment[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            var itemPath = $"{path}[{i}]";
            if (list[i] is not { } dto)
            {
                errors.Add(itemPath, "must be an object");
                result[i] = new SimEnchantment(string.Empty);
                continue;
            }

            result[i] = new SimEnchantment(RequiredId(dto.CardId, $"{itemPath}.cardId", errors))
            {
                ScriptData1 = Range(dto.ScriptData1, $"{itemPath}.scriptData1", -MaxStat, MaxStat, errors),
                ScriptData2 = Range(dto.ScriptData2, $"{itemPath}.scriptData2", -MaxStat, MaxStat, errors),
                Attack = Range(dto.Attack, $"{itemPath}.attack", -MaxStat, MaxStat, errors),
                Health = Range(dto.Health, $"{itemPath}.health", -MaxStat, MaxStat, errors),
                CreatorEntityId = Range(dto.CreatorEntityId, $"{itemPath}.creatorEntityId", 0, MaxStat, errors),
            };
        }

        return result;
    }

    private static Dictionary<string, int> Tags(Dictionary<string, int>? tags, string path, InputErrors errors)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (tags is null)
        {
            return result;
        }

        if (tags.Count > MaxTags)
        {
            errors.Add(path, $"at most {MaxTags} tags");
            return result;
        }

        foreach (var (name, value) in tags)
        {
            if (!IsId(name))
            {
                errors.Add(path, "tag names are 1-64 letters, digits or '_'");
                continue;
            }

            result[name] = Range(value, $"{path}.{name}", -MaxStat, MaxStat, errors);
        }

        return result;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, int>> ExtraTags(Dictionary<string, Dictionary<string, int>>? cards, string path,
                                                                                    InputErrors errors)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        if (cards is null)
        {
            return result;
        }

        if (cards.Count > MaxExtraTagCards)
        {
            errors.Add(path, $"at most {MaxExtraTagCards} cards");
            return result;
        }

        foreach (var (cardId, tags) in cards)
        {
            if (!IsId(cardId))
            {
                errors.Add(path, "card ids are 1-64 letters, digits or '_'");
                continue;
            }

            result[cardId] = Tags(tags, $"{path}.{cardId}", errors);
        }

        return result;
    }

    private static Dictionary<string, IReadOnlyList<SimMinion>> Removed(Dictionary<string, List<MinionDto>>? cards, string path, bool isOpponent,
                                                                         InputErrors errors)
    {
        var result = new Dictionary<string, IReadOnlyList<SimMinion>>(StringComparer.Ordinal);
        if (cards is null)
        {
            return result;
        }

        if (cards.Count > MaxExtras)
        {
            errors.Add(path, $"at most {MaxExtras} cards");
            return result;
        }

        foreach (var (cardId, minions) in cards)
        {
            if (!IsId(cardId))
            {
                errors.Add(path, "card ids are 1-64 letters, digits or '_'");
                continue;
            }

            result[cardId] = Minions(minions, $"{path}.{cardId}", MaxNested, isOpponent, MinionKind.Gone, errors);
        }

        return result;
    }

    private static SimEstimate[] Estimates(List<EstimateDto>? list, string path, InputErrors errors)
    {
        if (list is null || list.Count == 0)
        {
            return [];
        }

        if (list.Count > MaxEstimates)
        {
            errors.Add(path, $"at most {MaxEstimates} estimates");
            return [];
        }

        var result = new SimEstimate[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            var itemPath = $"{path}[{i}]";
            var dto = list[i];
            if (dto is null)
            {
                errors.Add(itemPath, "must be an object");
                result[i] = new SimEstimate(string.Empty, 0, 0, string.Empty);
                continue;
            }

            var basis = dto.Basis ?? string.Empty;
            if (basis.Length > MaxBasisLength || basis.Any(char.IsControl))
            {
                errors.Add($"{itemPath}.basis", $"at most {MaxBasisLength} characters, no control characters");
            }

            result[i] = new SimEstimate(RequiredId(dto.Input, $"{itemPath}.input", errors),
                Range(dto.Value1, $"{itemPath}.value1", -MaxStat, MaxStat, errors),
                Range(dto.Value2, $"{itemPath}.value2", -MaxStat, MaxStat, errors),
                basis)
            {
                Tags = Names(dto.Tags ?? [], $"{itemPath}.tags", errors),
            };
        }

        return result;
    }

    private static DeityAwakening? Deity(DeityDto? dto, string path, InputErrors errors) =>
        dto is null ? null : new DeityAwakening(Range(dto.Progress, $"{path}.progress", 0, 1000, errors), Range(dto.Total, $"{path}.total", 1, 1000, errors));

    private static Tribe Tribes(List<string> names, string path, InputErrors errors)
    {
        if (names.Count > MaxNames)
        {
            errors.Add(path, $"at most {MaxNames} names");
            return Tribe.None;
        }

        var tribes = Tribe.None;
        foreach (var name in names)
        {
            var tribe = IsId(name) ? Traits.ParseTribe(name) : Tribe.None;
            if (tribe == Tribe.None)
            {
                errors.Add(path, "tribes are BEAST, DEMON, DRAGON, ELEMENTAL, MECHANICAL, MURLOC, NAGA, PIRATE, QUILBOAR, UNDEAD, ABERRATION or ALL");
                return Tribe.None;
            }

            tribes |= tribe;
        }

        return tribes;
    }

    /// <summary>
    /// A list of HearthstoneJSON-style names (keywords, mechanics, markers, tags), upper-cased. Names the simulator does not
    /// read are kept and ignored, so a card's whole "mechanics" list from HearthstoneJSON can be sent as it is.
    /// </summary>
    private static string[] Names(List<string> names, string path, InputErrors errors)
    {
        if (names.Count > MaxNames)
        {
            errors.Add(path, $"at most {MaxNames} names");
            return [];
        }

        var result = new string[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            if (!IsId(names[i]))
            {
                errors.Add($"{path}[{i}]", "names are 1-64 letters, digits or '_'");
                result[i] = string.Empty;
                continue;
            }

            result[i] = names[i].ToUpperInvariant();
        }

        return result;
    }

    private static List<string> Ids(List<string>? ids, string path, int max, InputErrors errors)
    {
        if (ids is null || ids.Count == 0)
        {
            return [];
        }

        if (ids.Count > max)
        {
            errors.Add(path, $"at most {max} cards");
            return [];
        }

        var result = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            result.Add(RequiredId(ids[i], $"{path}[{i}]", errors));
        }

        return result;
    }

    private static string RequiredId(string? id, string path, InputErrors errors)
    {
        if (string.IsNullOrEmpty(id))
        {
            errors.Add(path, "is required");
            return string.Empty;
        }

        return OptionalId(id, path, errors);
    }

    private static string OptionalId(string? id, string path, InputErrors errors)
    {
        if (string.IsNullOrEmpty(id))
        {
            return string.Empty;
        }

        if (!IsId(id))
        {
            errors.Add(path, "card ids are 1-64 letters, digits or '_'");
            return string.Empty;
        }

        return id;
    }

    /// <summary>1-64 ASCII letters, digits or '_': every Hearthstone card id and tag name.</summary>
    internal static bool IsId(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxIdLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static int Required(int? value, string path, int min, int max, InputErrors errors)
    {
        if (value is null)
        {
            errors.Add(path, "is required");
            return 0;
        }

        return Range(value, path, min, max, errors);
    }

    private static int Range(int? value, string path, int min, int max, InputErrors errors, int fallback = 0)
    {
        if (value is not { } number)
        {
            return fallback;
        }

        if (number < min || number > max)
        {
            errors.Add(path, $"must be from {min} to {max}");
            return fallback;
        }

        return number;
    }
}
