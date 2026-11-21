using System.Net;
using System.Text.Json.Nodes;
using Hearthpulse.Core.Battlegrounds.Effects;

namespace BGSim.Server.Tests;

/// <summary>A server with the default limits, shared by the tests of this class.</summary>
public sealed class DefaultServer : IAsyncLifetime
{
    internal RunningServer Server { get; private set; } = null!;

    public async Task InitializeAsync() => Server = await RunningServer.StartAsync();

    public async Task DisposeAsync() => await Server.DisposeAsync();
}

/// <summary>The endpoints with the default limits: answers, determinism and request checks.</summary>
public sealed class ApiTests(DefaultServer fixture) : IClassFixture<DefaultServer>
{
    private RunningServer Server => fixture.Server;

    /// <summary>Wrath Weaver: a pool minion whose text never acts in combat (relevance None).</summary>
    private const string Vanilla = "BGS_004";

    /// <summary>A mid-game board of pool minions with deathrattles, Avenge, Rally and divine shields (tools/BGSim.LoadTest).</summary>
    internal const string MidBoard = """
        {
          "player": { "health": 22, "tavernTier": 5, "board": [
            { "cardId": "BG25_009", "attack": 14, "health": 9 }, { "cardId": "BGS_008", "attack": 11, "health": 12 },
            { "cardId": "BG36_372", "attack": 16, "health": 18 }, { "cardId": "BG34_405", "attack": 9, "health": 11 },
            { "cardId": "BG36_854", "attack": 6, "health": 5, "golden": true }, { "cardId": "BG25_010", "attack": 7, "health": 6 },
            { "cardId": "BG36_207", "attack": 8, "health": 14 } ] },
          "opponent": { "health": 19, "tavernTier": 5, "board": [
            { "cardId": "BG23_318", "attack": 12, "health": 8 }, { "cardId": "BG35_604", "attack": 10, "health": 15 },
            { "cardId": "BG31_809", "attack": 12, "health": 12 }, { "cardId": "BG31_925", "attack": 9, "health": 7 },
            { "cardId": "BG32_324", "attack": 6, "health": 16 }, { "cardId": "BG28_741", "attack": 9, "health": 5 },
            { "cardId": "BG32_880", "attack": 13, "health": 7 } ] },
          "options": { "runs": 500 }
        }
        """;

    private static string OneOnOne(string player, string opponent, string extra = "") => $$"""
        { "player": { "board": [ {{player}} ] }, "opponent": { "board": [ {{opponent}} ] } {{extra}} }
        """;

    [Fact]
    public async Task Health_answers_ok_without_doing_work()
    {
        var response = await Server.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Info_reports_the_card_data_the_effects_and_the_limits()
    {
        var (response, body) = await Server.GetJsonAsync("/v1/info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CombatRelevance.Patch, (string?)body["cardDataPatch"]);
        Assert.Equal(EffectRegistry.Default.NormalCardIds.Count, (int)body["effects"]!);
        Assert.Equal(0, (int)body["effectProblems"]!);
        Assert.Equal("jit", (string?)body["runtime"]);
        Assert.Equal(10_000, (int)body["limits"]!["maxRuns"]!);
        Assert.Equal(65_536, (int)body["limits"]!["maxBodyBytes"]!);
        Assert.False((bool)body["limits"]!["apiKeyRequired"]!);
    }

    [Fact]
    public async Task A_one_one_against_a_one_one_is_always_a_tie()
    {
        var minion = $$"""{ "cardId": "{{Vanilla}}", "attack": 1, "health": 1 }""";
        var (response, body) = await Server.PostJsonAsync("/v1/simulate", OneOnOne(minion, minion));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1.0, (double)body["tie"]!);
        Assert.Equal(0.0, (double)body["win"]!);
        Assert.Equal(0.0, (double)body["loss"]!);
        Assert.True((int)body["runs"]! > 0);
        Assert.False((bool)body["approximate"]!);
        Assert.Empty(body["unsimulatedCards"]!.AsArray());
    }

    [Fact]
    public async Task A_bigger_minion_wins_and_deals_damage()
    {
        var big = $$"""{ "cardId": "{{Vanilla}}", "attack": 5, "health": 5 }""";
        var small = $$"""{ "cardId": "{{Vanilla}}", "attack": 1, "health": 1 }""";
        var (_, body) = await Server.PostJsonAsync("/v1/simulate", OneOnOne(big, small));
        Assert.Equal(1.0, (double)body["win"]!);
        Assert.NotNull(body["damageDealt"]);
        Assert.Null(body["damageTaken"]);
    }

    [Fact]
    public async Task The_odds_of_the_same_request_are_the_same()
    {
        var (first, a) = await Server.PostJsonAsync("/v1/simulate", MidBoard);
        var (_, b) = await Server.PostJsonAsync("/v1/simulate", MidBoard);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // 500 runs is the minimum before a time stop: all of them run on any machine.
        Assert.Equal("runs", (string?)a["stoppedBy"]);
        Assert.Equal(500, (int)a["runs"]!);
        a.AsObject().Remove("elapsedMs");
        b.AsObject().Remove("elapsedMs");
        Assert.Equal(a.ToJsonString(), b.ToJsonString());
    }

    [Fact]
    public async Task A_fight_with_a_seed_is_the_same_fight()
    {
        var request = MidBoard.Replace("\"options\": { \"runs\": 500 }", "\"seed\": 12345", StringComparison.Ordinal);
        var (response, a) = await Server.PostJsonAsync("/v1/fight", request);
        var (_, b) = await Server.PostJsonAsync("/v1/fight", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(12345, (int)a["seed"]!);
        Assert.NotEmpty(a["log"]!.AsArray());
        Assert.Contains((string?)a["outcome"], new[] { "win", "tie", "loss" });
        a.AsObject().Remove("elapsedMs");
        b.AsObject().Remove("elapsedMs");
        Assert.Equal(a.ToJsonString(), b.ToJsonString());
    }

    [Fact]
    public async Task A_fight_without_a_seed_uses_the_boards_seed()
    {
        var request = MidBoard.Replace("\"options\": { \"runs\": 500 }", "\"firstAttacker\": \"player\"", StringComparison.Ordinal);
        var (_, a) = await Server.PostJsonAsync("/v1/fight", request);
        var (_, b) = await Server.PostJsonAsync("/v1/fight", request);
        Assert.Equal((int)a["seed"]!, (int)b["seed"]!);
        Assert.Equal(a["log"]!.ToJsonString(), b["log"]!.ToJsonString());
    }

    [Fact]
    public async Task A_card_no_table_knows_makes_the_answer_approximate()
    {
        var unknown = """{ "cardId": "ZZ_NotACard_1", "attack": 3, "health": 3 }""";
        var minion = $$"""{ "cardId": "{{Vanilla}}", "attack": 1, "health": 1 }""";
        var (_, body) = await Server.PostJsonAsync("/v1/simulate", OneOnOne(minion, unknown));
        Assert.True((bool)body["approximate"]!);
        var item = Assert.Single(body["unsimulatedCards"]!.AsArray())!;
        Assert.Equal("ZZ_NotACard_1", (string?)item["cardId"]);
        Assert.Equal("opponent", (string?)item["side"]);
        Assert.Equal("unknown", (string?)item["reason"]);

        // The client can say what the card carries: no effect markers, nothing to simulate.
        var plain = """{ "cardId": "ZZ_NotACard_1", "attack": 3, "health": 3, "effectMarkers": [] }""";
        var (_, known) = await Server.PostJsonAsync("/v1/simulate", OneOnOne(minion, plain));
        Assert.False((bool)known["approximate"]!);
    }

    [Fact]
    public async Task A_pool_card_that_acts_in_combat_without_an_effect_is_listed()
    {
        var missing = CombatRelevance.Table.FirstOrDefault(r =>
            r is { Relevance: Relevance.Relevant, Origin: Origin.Pool } && EffectRegistry.Default.Get(r.CardId, golden: false) is null);
        if (missing is null)
        {
            return; // Every combat-relevant pool card has an effect: nothing left to report.
        }

        var minion = $$"""{ "cardId": "{{missing.CardId}}", "attack": 3, "health": 3 }""";
        var (_, body) = await Server.PostJsonAsync("/v1/simulate", OneOnOne(minion, minion));
        Assert.True((bool)body["approximate"]!);
        Assert.Equal(2, body["unsimulatedCards"]!.AsArray().Count);
        Assert.All(body["unsimulatedCards"]!.AsArray(), i => Assert.Equal("missing_effect", (string?)i!["reason"]));
    }

    [Fact]
    public async Task An_empty_board_loses_without_a_simulation()
    {
        var minion = $$"""{ "cardId": "{{Vanilla}}", "attack": 1, "health": 1 }""";
        var (_, body) = await Server.PostJsonAsync("/v1/simulate", $$"""{ "player": { "board": [] }, "opponent": { "board": [ {{minion}} ] } }""");
        Assert.Equal(1.0, (double)body["loss"]!);
        Assert.Equal("none", (string?)body["stoppedBy"]);
    }

    [Fact]
    public async Task A_duos_teammate_enters_when_the_board_is_empty()
    {
        var minion = $$"""{ "cardId": "{{Vanilla}}", "attack": 5, "health": 5 }""";
        var small = $$"""{ "cardId": "{{Vanilla}}", "attack": 1, "health": 1 }""";
        var (response, body) = await Server.PostJsonAsync("/v1/simulate",
            $$"""{ "player": { "board": [], "teammate": { "board": [ {{minion}} ] } }, "opponent": { "board": [ {{small}} ] } }""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1.0, (double)body["win"]!);
    }

    public static TheoryData<string, string> InvalidRequests => new()
    {
        { """{ "opponent": { "board": [] } }""", "player" },
        { """{ "player": { "board": [] }, "opponent": { "board": [] }, "firstAttacker": "both" }""", "firstAttacker" },
        { Board("""{ "cardId": "BG<1>", "attack": 1, "health": 1 }"""), "player.board[0].cardId" },
        { Board("""{ "attack": 1, "health": 1 }"""), "player.board[0].cardId" },
        { Board("""{ "cardId": "BGS_004", "health": 1 }"""), "player.board[0].attack" },
        { Board("""{ "cardId": "BGS_004", "attack": -1, "health": 1 }"""), "player.board[0].attack" },
        { Board("""{ "cardId": "BGS_004", "attack": 1, "health": 0 }"""), "player.board[0].health" },
        { Board("""{ "cardId": "BGS_004", "attack": 1, "health": 100000000 }"""), "player.board[0].health" },
        { Board("""{ "cardId": "BGS_004", "attack": 1, "health": 5, "maxHealth": 4 }"""), "player.board[0].maxHealth" },
        { Board("""{ "cardId": "BGS_004", "attack": 1, "health": 1, "tribes": ["DINOSAUR"] }"""), "player.board[0].tribes" },
        { Board("""{ "cardId": "BGS_004", "attack": 1, "health": 1, "tier": 8 }"""), "player.board[0].tier" },
        { Board(string.Join(",", Enumerable.Repeat("""{ "cardId": "BGS_004", "attack": 1, "health": 1 }""", 8))), "player.board" },
        { """{ "player": { "board": [], "teammate": { "board": [], "teammate": { "board": [] } } }, "opponent": { "board": [] } }""", "player.teammate.teammate" },
        { """{ "player": { "board": [], "tavernTier": 9 }, "opponent": { "board": [] } }""", "player.tavernTier" },
        { """{ "player": { "board": [], "tags": { "BAD TAG": 1 } }, "opponent": { "board": [] } }""", "player.tags" },
        { """{ "player": { "board": [] }, "opponent": { "board": [] }, "options": { "runs": 10001 } }""", "options.runs" },
        { """{ "player": { "board": [] }, "opponent": { "board": [] }, "options": { "runs": 0 } }""", "options.runs" },
        { """{ "player": { "board": [] }, "opponent": { "board": [] }, "options": { "timeBudgetMs": 1001 } }""", "options.timeBudgetMs" },
        { """{ "player": { "board": [] }, "opponent": { "board": [] }, "options": { "targetHalfWidth": 0.6 } }""", "options.targetHalfWidth" },
    };

    private static string Board(string minions) => $$"""{ "player": { "board": [ {{minions}} ] }, "opponent": { "board": [] } }""";

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task An_invalid_request_is_a_400_that_says_where(string json, string path)
    {
        var (response, body) = await Server.PostJsonAsync("/v1/simulate", json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (string?)body["error"]);
        Assert.Contains(body["errors"]!.AsArray(), e => (string?)e!["path"] == path);
    }

    [Theory]
    [InlineData("""{ "player": { "board": [] }, "opponent": { "board": [] }, "colour": 1 }""")]
    [InlineData("""{ "player": { "board": [ { "cardId": "BGS_004", "attack": "1", "health": 1 } ] }, "opponent": { "board": [] } }""")]
    [InlineData("""{ "player": { "board": [] }, "opponent": { "board": [] }, }""")]
    [InlineData("""{ "player": { "board": [] }, "player": { "board": [] }, "opponent": { "board": [] } }""")]
    [InlineData("""{ "player": """)]
    [InlineData("null")]
    public async Task Malformed_or_unknown_json_is_a_400(string json)
    {
        var (response, body) = await Server.PostJsonAsync("/v1/simulate", json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_json", (string?)body["error"]);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_415()
    {
        var response = await Server.Client.PostAsync("/v1/simulate", new StringContent("player=1", System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task The_fight_does_not_take_simulation_options()
    {
        var (response, body) = await Server.PostJsonAsync("/v1/fight", MidBoard);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_json", (string?)body["error"]);
    }

    [Fact]
    public async Task Answers_are_not_cached_or_sniffed()
    {
        var response = await Server.Client.GetAsync("/health");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Server"));
    }

    internal static JsonNode Parse(string json) => JsonNode.Parse(json)!;
}
