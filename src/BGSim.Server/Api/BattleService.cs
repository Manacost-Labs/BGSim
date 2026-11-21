using System.Diagnostics;
using Hearthpulse.Core.Battlegrounds;

namespace BGSim.Server.Api;

/// <summary>Runs checked requests through the simulator and shapes the answers. Holds no state between requests.</summary>
internal sealed class BattleService(ServerLimits limits)
{
    /// <summary>Checks the options; null when one is out of range (the error is in <paramref name="errors"/>).</summary>
    public SimOptions? Options(OptionsDto? dto, int firstAttackerEntityId, InputErrors errors, CancellationToken cancel)
    {
        var runs = dto?.Runs ?? limits.DefaultRuns;
        if (runs < 1 || runs > limits.MaxRuns)
        {
            errors.Add("options.runs", $"must be from 1 to {limits.MaxRuns}");
        }

        var budget = dto?.TimeBudgetMs ?? limits.DefaultTimeBudgetMs;
        if (budget < 1 || budget > limits.MaxTimeBudgetMs)
        {
            errors.Add("options.timeBudgetMs", $"must be from 1 to {limits.MaxTimeBudgetMs}");
        }

        var halfWidth = dto?.TargetHalfWidth ?? limits.DefaultTargetHalfWidth;
        if (!(halfWidth >= 0 && halfWidth <= 0.5))
        {
            errors.Add("options.targetHalfWidth", "must be from 0 to 0.5");
        }

        var cap = dto?.DamageCap ?? 0;
        if (cap < 0 || cap > 1000)
        {
            errors.Add("options.damageCap", "must be from 0 to 1000");
        }

        if (errors.Any)
        {
            return null;
        }

        return new SimOptions
        {
            Runs = runs,
            MinRuns = Math.Min(500, runs),
            TimeBudget = TimeSpan.FromMilliseconds(budget),
            TargetHalfWidth = halfWidth,
            // One thread per request: the gate spreads requests over the cores instead.
            Parallelism = 1,
            DamageCap = cap,
            CountDamage = dto?.CountDamage ?? true,
            FirstAttackerEntityId = firstAttackerEntityId,
            Cancel = cancel,
        };
    }

    public static SimulateResponse Simulate(Battle battle, SimOptions options)
    {
        var clock = Stopwatch.StartNew();
        var odds = CombatSimulator.Run(battle.Player, battle.Opponent, battle.PlayerFirst, options);
        var unsimulated = CombatSimulator.Explain(battle.Player, isOpponent: false)
            .Concat(CombatSimulator.Explain(battle.Opponent, isOpponent: true))
            .Select(u => new UnsimulatedDto(u.CardId, u.Golden, u.IsOpponent ? "opponent" : "player", u.Code, u.Extra))
            .ToArray();
        var elapsed = clock.Elapsed.TotalMilliseconds;

        var stoppedBy = odds.Runs == 0 ? "none"
            : odds.Runs >= options.Runs ? "runs"
            : options.TargetHalfWidth > 0 && odds.HalfWidth <= options.TargetHalfWidth ? "precision"
            : options.Cancel.IsCancellationRequested ? "cancelled"
            : "time";
        return new SimulateResponse(
            odds.Win,
            odds.Tie,
            odds.Loss,
            odds.Runs,
            odds.HalfWidth,
            stoppedBy,
            Damage(odds.DamageTaken),
            Damage(odds.DamageDealt),
            odds.LethalTaken,
            odds.LethalDealt,
            odds.DamageCap,
            odds.Approximate,
            odds.Unsimulated,
            unsimulated,
            odds.Estimated,
            odds.Truncated,
            Math.Round(elapsed, 3));
    }

    public FightResponse Fight(Battle battle, int? seed)
    {
        var clock = Stopwatch.StartNew();
        var (player, opponent) = (CombatSimulator.StartingBoard(battle.Player), CombatSimulator.StartingBoard(battle.Opponent));
        var used = seed ?? CombatSimulator.TeamSeed(battle.Player, battle.Opponent, battle.PlayerFirst);
        var fight = CombatSimulator.RunSingle(player, opponent, battle.PlayerFirst, new Random(used), trace: true,
            firstAttackerEntityId: battle.FirstAttackerEntityId);
        var elapsed = clock.Elapsed.TotalMilliseconds;

        var log = fight.Log.Count > limits.MaxFightLogLines ? fight.Log.Take(limits.MaxFightLogLines).ToArray() : fight.Log;
        return new FightResponse(
            fight.Outcome > 0 ? "win" : fight.Outcome < 0 ? "loss" : "tie",
            used,
            fight.Truncated,
            Survivors(fight.Ours),
            Survivors(fight.Theirs),
            log,
            fight.Log.Count,
            log.Count < fight.Log.Count,
            Math.Round(elapsed, 3));
    }

    private static SurvivorDto[] Survivors(IReadOnlyList<SimMinion> minions) =>
        minions.Where(m => m.Health > 0).Select(m => new SurvivorDto(m.CardId, m.Golden, m.Attack, m.Health)).ToArray();

    private static DamageDto? Damage(DamageSpread? spread) =>
        spread is null ? null : new DamageDto(Math.Round(spread.Mean, 4), spread.Low, spread.High, spread.Max, spread.Counts);
}
