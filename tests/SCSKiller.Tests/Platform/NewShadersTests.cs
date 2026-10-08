using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

public class NewShadersTests
{
    const string Driver = "100.01";
    static readonly DateTimeOffset At = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    static readonly DateTimeOffset Now = At.AddDays(3);

    static GameState S(string id, long recorded = 0, long? planNew = null, GameStatus status = GameStatus.Stale, AntiCheat antiCheat = AntiCheat.None,
        bool playing = false, string driver = Driver, DateTimeOffset? warmedAt = null, long plan = 0, string reason = "") =>
        new GameState(new Game(id, id, Store.Steam, "", ""), null, antiCheat, status, reason, null, plan > 0 ? new PlanStats(plan, 0, 0, 0, false) : null,
                null, null, driver, warmedAt ?? At, null, false, null)
            with { RecordedSinceWarm = recorded, NewPipelines = planNew, Playing = playing };

    static (IReadOnlyList<GameState> Due, Dictionary<string, string> Notified) Due(Dictionary<string, string> notified, params GameState[] games) =>
        DueAt(Now, notified, games);

    static (IReadOnlyList<GameState> Due, Dictionary<string, string> Notified) DueAt(DateTimeOffset now, Dictionary<string, string> notified, params GameState[] games) =>
        NewShaders.Due(games, [], notified, DriverStale(games), now);

    // as ScsKiller.DriverStaleGames gives them: warmed for another driver
    static HashSet<string> DriverStale(GameState[] games) => games.Where(s => s.WarmedDriverVersion != Driver).Select(s => s.Game.Id).ToHashSet();

    [Fact]
    public void Only_compiled_games_with_something_new_on_this_driver_are_told_about()
    {
        var (due, notified) = Due([],
            S("plan-check-nothing-new", planNew: 0, status: GameStatus.Warmed),
            S("never-compiled", recorded: 5_000, status: GameStatus.Ready) with { WarmedAt = null },
            S("unsupported", recorded: 5_000, status: GameStatus.Unsupported),
            S("anti-cheat", recorded: 5_000, antiCheat: AntiCheat.EasyAntiCheat),
            S("driver-stale", recorded: 5_000, driver: "99.00"),
            S("recorded", recorded: 300),
            S("plan-check-found-more", planNew: 200));
        Assert.Equal(["recorded", "plan-check-found-more"], due.Select(s => s.Game.Id));
        Assert.Equal(["recorded", "plan-check-found-more"], notified.Keys);
    }

    [Fact]
    public void Several_games_come_in_one_list_and_each_change_is_told_once()
    {
        var (due, notified) = Due([], S("a", recorded: 300), S("b", recorded: 100, planNew: 400));
        Assert.Equal(2, due.Count);
        Assert.Equal(500, NewShaders.Count(due[1]));

        (due, notified) = Due(notified, S("a", recorded: 300), S("b", recorded: 100, planNew: 400));
        Assert.Empty(due);

        (due, notified) = DueAt(Now.AddDays(1), notified, S("a", recorded: 700), S("b", recorded: 100, planNew: 400));
        Assert.Equal("a", Assert.Single(due).Game.Id);

        (due, notified) = DueAt(Now.AddDays(2), notified, S("a", recorded: 700), S("b", warmedAt: At.AddDays(1)));   // b compiled again: nothing new
        Assert.Empty(due);
        Assert.Equal(["a"], notified.Keys);
    }

    [Fact]
    public void Too_few_new_pipelines_are_not_told_about()
    {
        // 1% of the plan, at least 100 and at most 1,000
        var (due, _) = Due([],
            S("community-update", recorded: 123, plan: 55_910),   // its page still says 123 new
            S("two", planNew: 2, plan: 109_245),
            S("no-plan", recorded: 99),
            S("small-plan", recorded: 100, plan: 2_484),
            S("share", recorded: 560, plan: 55_910),
            S("huge-plan", recorded: 1_000, plan: 490_695),
            S("planner", planNew: 2, plan: 109_245, reason: "SCSKiller can now compile 2 more pipelines for this game"));   // told as before
        Assert.Equal(["small-plan", "share", "huge-plan", "planner"], due.Select(s => s.Game.Id));
        Assert.Equal(1_000, NewShaders.Enough(S("x", plan: 490_695)));
        Assert.Equal(559, NewShaders.Enough(S("x", plan: 55_910)));
        Assert.Equal(100, NewShaders.Enough(S("x")));
    }

    [Fact]
    public void A_game_is_told_about_at_most_once_a_day()
    {
        var (due, notified) = Due([], S("a", recorded: 300));
        Assert.Single(due);
        var told = notified["a"];

        (due, notified) = DueAt(Now.AddHours(23), notified, S("a", recorded: 900));
        Assert.Empty(due);
        Assert.Equal(told, notified["a"]);   // kept as told: the count since is told once the day is over

        (due, notified) = DueAt(Now.AddHours(23.5), notified, S("a"));   // nothing new for a while: the day still counts
        Assert.Empty(due);
        Assert.Equal(told, notified["a"]);

        (due, notified) = DueAt(Now.AddHours(23.9), notified, S("a", recorded: 900));
        Assert.Empty(due);

        (due, notified) = DueAt(Now.AddHours(24), notified, S("a", recorded: 900));
        Assert.Single(due);

        (due, _) = DueAt(Now.AddHours(25), notified,
            S("a", recorded: 2_000, reason: "SCSKiller can now compile 1,100 more pipelines for this game"));   // a newer planner: as before
        Assert.Single(due);
    }

    /// <summary>An entry kept before told times were (a recount after an update can change every count): told when it is
    /// first read, so no game is told again for that day, and the time is kept from then on.</summary>
    [Fact]
    public void An_entry_from_before_told_times_counts_as_told_now()
    {
        var key = NewShaders.Key(S("a", recorded: 300), new HashSet<string>())!;
        var (due, notified) = Due(new() { ["a"] = key }, S("a", recorded: 300));
        Assert.Empty(due);
        Assert.Equal($"{key}|{Now.UtcTicks}|p", notified["a"]);

        (due, notified) = Due(new() { ["a"] = key }, S("a", recorded: 2_000));
        Assert.Empty(due);
        Assert.Equal($"{key}|{Now.UtcTicks}|p", notified["a"]);

        (due, _) = DueAt(Now.AddDays(1), notified, S("a", recorded: 2_000));
        Assert.Single(due);

        (due, notified) = Due(new() { ["a"] = key }, S("a", recorded: 300, reason: "SCSKiller can now compile 300 more pipelines for this game"));
        Assert.Empty(due);   // what it told may have been this compile's planner notification
        Assert.Equal($"{key}|{Now.UtcTicks}|p", notified["a"]);
    }

    /// <summary>The minimum is on what is new since the game was told: a few more a day later aren't told about (The
    /// Witcher 3: 366 upscaler pipelines told, 373 after a pack update). Compiled since, the count starts over.</summary>
    [Fact]
    public void A_game_is_told_again_only_when_enough_more_is_new()
    {
        var (due, notified) = Due([], S("a", recorded: 366, plan: 24_993));
        Assert.Single(due);

        (due, notified) = DueAt(Now.AddDays(2), notified, S("a", recorded: 373, plan: 24_993));
        Assert.Empty(due);
        Assert.StartsWith($"{At.UtcTicks}|366|", notified["a"]);   // what was told stays the baseline

        (due, notified) = DueAt(Now.AddDays(3), notified, S("a", recorded: 366 + 249, plan: 24_993));
        Assert.Single(due);

        (due, _) = DueAt(Now.AddDays(5), notified, S("a", recorded: 300, plan: 24_993, warmedAt: At.AddDays(4)));
        Assert.Single(due);   // compiled since: 300 new since that compile
    }

    /// <summary>A rebuilt plan's notification skips the minimum and the day once per compile. A count that changes after
    /// it (a community download lands seconds later) waits like any other: FF7 Rebirth was told twice in two seconds.</summary>
    [Fact]
    public void A_rebuilt_plan_is_told_once_per_compile()
    {
        const string Planner = "SCSKiller can now compile 1,244 more pipelines for this game";
        var (due, notified) = Due([], S("a", recorded: 1_178, planNew: 1_244, reason: Planner));
        Assert.Single(due);
        Assert.EndsWith("|p", notified["a"]);

        (due, notified) = DueAt(Now.AddSeconds(2), notified, S("a", recorded: 1_181, planNew: 1_244, reason: Planner));
        Assert.Empty(due);

        (due, _) = DueAt(Now.AddHours(1), notified, S("a", planNew: 27_647, warmedAt: At.AddDays(1),
            reason: "SCSKiller can now compile 27,647 more pipelines for this game"));
        Assert.Single(due);   // another compile's plan, within the day
    }

    /// <summary>Before its plan is rebuilt, a newer planner's game counts what the last plan has: told as any other.</summary>
    [Fact]
    public void A_plan_not_rebuilt_yet_is_not_the_planners_notification()
    {
        var (due, _) = Due([], S("a", recorded: 7, reason: "SCSKiller can now compile more of this game"));
        Assert.Empty(due);
    }

    [Fact]
    public void A_running_game_waits_for_its_exit()
    {
        var (due, notified) = Due([], S("a", recorded: 300, playing: true));
        Assert.Empty(due);
        Assert.Empty(notified);

        (due, _) = Due(notified, S("a", recorded: 300));
        Assert.Equal("a", Assert.Single(due).Game.Id);
    }

    [Fact]
    public void A_game_queued_for_a_compile_is_not_told_about()
    {
        GameState[] games = [S("waiting", recorded: 100), S("idle", recorded: 100), S("plan-check", recorded: 100), S("done", recorded: 100)];
        QueueItem[] queue =
        [
            new("waiting", QueueStage.Waiting, null, null),
            new("idle", QueueStage.Warming, null, null, "starts when the PC is idle"),
            new("plan-check", QueueStage.Waiting, null, null, PlanCheck: true),   // a plan rebuild compiles nothing
            new("done", QueueStage.Done, null, null),
        ];
        var (due, notified) = NewShaders.Due(games, queue, new Dictionary<string, string>(), DriverStale(games), Now);
        Assert.Equal(["plan-check", "done"], due.Select(s => s.Game.Id));
        Assert.Equal(["plan-check", "done"], notified.Keys);
    }
}
