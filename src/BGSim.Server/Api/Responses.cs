namespace BGSim.Server.Api;

/// <summary>Odds of one combat from the player's side (docs/server.md, "POST /v1/simulate").</summary>
/// <param name="StoppedBy">
/// Why the runs ended: "runs" (all asked for), "precision" (targetHalfWidth reached), "time" (the time budget), "cancelled"
/// (the client went away) or "none" (an empty board: nothing to simulate). Only "time" makes the numbers depend on load.
/// </param>
/// <param name="Unsimulated">What CombatOdds.Unsimulated counts: flagged minions and player-level cards, plus one when an engine limit cut fights short.</param>
public sealed record SimulateResponse(
    double Win,
    double Tie,
    double Loss,
    int Runs,
    double HalfWidth,
    string StoppedBy,
    DamageDto? DamageTaken,
    DamageDto? DamageDealt,
    double? LethalTaken,
    double? LethalDealt,
    int DamageCap,
    bool Approximate,
    int Unsimulated,
    IReadOnlyList<UnsimulatedDto> UnsimulatedCards,
    int Estimated,
    int Truncated,
    double ElapsedMs);

/// <summary>Hero damage over the fights of one outcome: mean, 10th and 90th percentile, max, fights per damage value.</summary>
public sealed record DamageDto(double Mean, int Low, int High, int Max, IReadOnlyList<int> Counts);

/// <param name="Side">"player" or "opponent".</param>
/// <param name="Reason">missing_effect, missing_input or unknown (the coverage matrix's words).</param>
/// <param name="Extra">A player-level card (hero power, trinket...) rather than a minion.</param>
public sealed record UnsimulatedDto(string CardId, bool Golden, string Side, string Reason, bool Extra);

/// <summary>One fight (docs/server.md, "POST /v1/fight").</summary>
/// <param name="Outcome">"win", "tie" or "loss" for the player.</param>
public sealed record FightResponse(
    string Outcome,
    int Seed,
    bool Truncated,
    IReadOnlyList<SurvivorDto> Player,
    IReadOnlyList<SurvivorDto> Opponent,
    IReadOnlyList<string> Log,
    int LogLines,
    bool LogTruncated,
    double ElapsedMs);

public sealed record SurvivorDto(string CardId, bool Golden, int Attack, int Health);

public sealed record InfoResponse(
    string Version,
    string Commit,
    string Runtime,
    string CardDataPatch,
    int Effects,
    int EffectProblems,
    LimitsDto Limits);

public sealed record LimitsDto(
    int MaxBodyBytes,
    int MaxRuns,
    int DefaultRuns,
    int MaxTimeBudgetMs,
    int DefaultTimeBudgetMs,
    double DefaultTargetHalfWidth,
    int MaxConcurrent,
    int QueueLimit,
    int MaxFightLogLines,
    int MaxBoard,
    bool ApiKeyRequired,
    bool Cors);

/// <param name="Error">A stable code: invalid_json, invalid_request, unsupported_media_type, body_too_large, unauthorized, busy, internal.</param>
public sealed record ErrorResponse(string Error, string Message, IReadOnlyList<ErrorItem>? Errors = null);

/// <param name="Path">Where in the request, e.g. player.board[2].attack.</param>
public sealed record ErrorItem(string Path, string Message);
