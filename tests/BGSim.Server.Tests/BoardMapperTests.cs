using System.Text.Json;
using BGSim.Server.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using Hearthpulse.Core.Battlegrounds.Effects.Secrets;

namespace BGSim.Server.Tests;

/// <summary>Printed data of pool minions filled in from the generated pool table; the client's own fields win.</summary>
public sealed class BoardMapperTests
{
    /// <summary>Risen Rider: Undead, tier 1, Taunt and Reborn printed.</summary>
    private const string RisenRider = "BG25_001";

    private static readonly SimMinion Printed = MinionPool.Minions.Single(m => m.CardId == RisenRider);

    private static SimMinion Map(string minion)
    {
        var request = JsonSerializer.Deserialize($$"""{ "player": { "board": [ {{minion}} ] }, "opponent": { "board": [] } }""",
            ApiJson.Default.SimulateRequest)!;
        var errors = new InputErrors();
        var battle = BoardMapper.Map(request, errors);
        Assert.False(errors.Any, string.Join("; ", errors.Items));
        return Assert.Single(battle!.Player.Minions);
    }

    [Fact]
    public void A_pool_minion_gets_its_printed_data()
    {
        var minion = Map($$"""{ "cardId": "{{RisenRider}}", "attack": 7, "health": 3 }""");

        Assert.Equal(7, minion.Attack);
        Assert.Equal(3, minion.Health);
        Assert.Equal(Printed.Tribes, minion.Tribes);
        Assert.Equal(Tribe.Undead, minion.Tribes & Tribe.Undead);
        Assert.Equal(Printed.TechLevel, minion.TechLevel);
        Assert.Equal(Printed.BaseKeywords, minion.BaseKeywords);
        Assert.Equal(Printed.Mechanics, minion.Mechanics);
        Assert.Equal(Printed.RebornAttack, minion.RebornAttack);
        Assert.Equal(Printed.BaseHealth, minion.BaseHealth);
        // Current keywords default to the printed ones.
        Assert.True(minion.Taunt);
        Assert.True(minion.Reborn);
        Assert.False(minion.Golden);
        Assert.False(minion.HasUnsimulatedEffect);
    }

    [Fact]
    public void A_golden_minion_has_doubled_printed_stats()
    {
        var byFlag = Map($$"""{ "cardId": "{{RisenRider}}", "golden": true, "attack": 4, "health": 2 }""");
        Assert.True(byFlag.Golden);
        Assert.Equal(2 * Printed.RebornAttack, byFlag.RebornAttack);
        Assert.Equal(2 * Printed.BaseHealth, byFlag.BaseHealth);

        // The golden card id is the normal id with Golden set: effects are looked up that way.
        var golden = CombatRelevance.Find(RisenRider)!.GoldenCardId;
        var byId = Map($$"""{ "cardId": "{{golden}}", "attack": 4, "health": 2 }""");
        Assert.Equal(RisenRider, byId.CardId);
        Assert.True(byId.Golden);
        Assert.Equal(2 * Printed.RebornAttack, byId.RebornAttack);
    }

    [Fact]
    public void The_clients_fields_win_over_the_pool()
    {
        var minion = Map($$"""
            { "cardId": "{{RisenRider}}", "attack": 2, "health": 1, "taunt": false, "divineShield": true, "tribes": ["beast", "MECHANICAL"],
              "tier": 3, "baseAttack": 9, "baseHealth": 8, "baseKeywords": ["WINDFURY", "SOMETHING_ELSE"], "mechanics": ["DEATHRATTLE"],
              "maxHealth": 4, "scriptData1": 5, "entityId": 77, "enchantments": [ { "cardId": "BG_X_e", "attack": 2 } ] }
            """);

        Assert.False(minion.Taunt);
        Assert.True(minion.DivineShield);
        // Not given: from the client's printed keywords (Windfury), no longer the pool's (Taunt, Reborn).
        Assert.False(minion.Reborn);
        Assert.Equal(2, minion.AttacksPerTurn);
        Assert.Equal(Tribe.Beast | Tribe.Mech, minion.Tribes);
        Assert.Equal(3, minion.TechLevel);
        Assert.Equal(9, minion.RebornAttack);
        Assert.Equal(8, minion.BaseHealth);
        Assert.Equal(Keyword.Windfury, minion.BaseKeywords);
        Assert.Equal(CardMechanic.Deathrattle, minion.Mechanics);
        Assert.Equal(4, minion.MaxHealth);
        Assert.Equal(5, minion.ScriptData1);
        Assert.Equal(77, minion.EntityId);
        Assert.Equal(["BG_X_e"], minion.Enchantments);
        Assert.Equal(2, Assert.Single(minion.EnchantmentValues).Attack);
    }

    [Fact]
    public void A_card_outside_the_pool_keeps_its_lasting_keywords_for_reborn()
    {
        var minion = Map("""{ "cardId": "ZZ_Token_1", "attack": 4, "health": 4, "taunt": true, "divineShield": true, "effectMarkers": [] }""");
        Assert.Equal(Keyword.Taunt, minion.BaseKeywords);
        Assert.Equal(4, minion.RebornAttack);
        Assert.Equal(0, minion.BaseHealth);
        Assert.False(minion.HasUnsimulatedEffect);
    }

    [Fact]
    public void Effect_markers_flag_an_unknown_card_and_add_granted_mechanics()
    {
        Assert.True(Map("""{ "cardId": "ZZ_Token_1", "attack": 1, "health": 1 }""").HasUnsimulatedEffect);
        Assert.True(Map("""{ "cardId": "ZZ_Token_1", "attack": 1, "health": 1, "effectMarkers": ["DEATHRATTLE"] }""").HasUnsimulatedEffect);
        Assert.False(Map("""{ "cardId": "ZZ_Token_1", "attack": 1, "health": 1, "effectMarkers": ["TAUNT"] }""").HasUnsimulatedEffect);

        // A Rally on Risen Rider came from an enchantment: in its mechanics, and "not simulated" with nothing explaining it.
        var granted = Map($$"""{ "cardId": "{{RisenRider}}", "attack": 2, "health": 1, "effectMarkers": ["BACON_RALLY"] }""");
        Assert.Equal(CardMechanic.Rally, granted.Mechanics & CardMechanic.Rally);
        Assert.True(granted.HasUnsimulatedEffect);
    }

    [Fact]
    public void The_hero_power_is_a_player_level_card()
    {
        var request = JsonSerializer.Deserialize("""
            { "player": { "heroPower": "TB_BaconShop_HP_001", "extras": ["BG_Trinket_1", "TB_BaconShop_HP_001"], "board": [] },
              "opponent": { "board": [], "secrets": ["BG_Secret_1"] }, "lobbyTribes": ["BEAST", "UNDEAD"] }
            """, ApiJson.Default.SimulateRequest)!;
        var errors = new InputErrors();
        var battle = BoardMapper.Map(request, errors)!;

        Assert.Equal("TB_BaconShop_HP_001", battle.Player.HeroPowerCardId);
        Assert.Equal(["TB_BaconShop_HP_001", "BG_Trinket_1"], battle.Player.Extras);
        Assert.Equal(["BG_Secret_1"], battle.Opponent.Secrets);
        Assert.Equal(Tribe.Beast | Tribe.Undead, battle.Opponent.LobbyTribes);
        Assert.Null(battle.PlayerFirst);
    }

    [Fact]
    public void Hand_minions_are_never_flagged()
    {
        var request = JsonSerializer.Deserialize("""
            { "player": { "board": [], "hand": [ { "cardId": "ZZ_Token_1", "attack": 1, "health": 1 } ] }, "opponent": { "board": [] } }
            """, ApiJson.Default.SimulateRequest)!;
        var battle = BoardMapper.Map(request, new InputErrors())!;
        Assert.False(Assert.Single(battle.Player.Hand).HasUnsimulatedEffect);
    }
}
