using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.App.Design;

/// <summary>The mockup's sample library plus a simulated compile queue. Used by --fake and --screenshots. Events fire on
/// a timer thread, like a real background worker.</summary>
public sealed class FakeScsKiller : IScsKiller
{
    readonly object gate = new();
    readonly List<GameState> games;
    readonly List<QueueItem> queue = [];
    readonly FakeVendor vendor = new();
    readonly Timer timer;
    bool paused, running;

    /// <summary>A 25-minute session at about 60 FPS, or the report of a real recorder folder's scskiller_frames.bin and
    /// creates csv when SCSKILLER_FAKE_FRAMES names one (screenshots of a measured session).</summary>
    static FrameReport? SampleFrames()
    {
        if (Environment.GetEnvironmentVariable("SCSKILLER_FAKE_FRAMES") is { Length: > 0 } dir)
            return FrameLog.Read(Path.Combine(dir, FrameLog.FileName), Path.Combine(dir, "scskiller_creates.csv"));
        Hitch[] hitches = [new(TimeSpan.FromSeconds(3), 2_430, HitchCause.Loading), new(TimeSpan.FromSeconds(186), 77, HitchCause.Shader),
            new(TimeSpan.FromSeconds(247), 180, HitchCause.Other), new(TimeSpan.FromSeconds(611), 1_309, HitchCause.Loading),
            new(TimeSpan.FromSeconds(615), 62, HitchCause.Shader), new(TimeSpan.FromSeconds(1_020), 55, HitchCause.Other)];
        var peaks = Enumerable.Range(0, FrameLog.GraphColumns).Select(i => (float)(17 + 6 * Math.Abs(Math.Sin(i * 0.37)) + (i % 97 == 0 ? 14 : 0))).ToArray();
        foreach (var h in hitches) peaks[(int)(h.At.TotalSeconds / 1500 * FrameLog.GraphColumns)] = (float)h.Ms;
        return new FrameReport(TimeSpan.FromMinutes(25), TimeSpan.FromSeconds(48), 88_400, 41.3, hitches, peaks);
    }

    public FakeScsKiller()
    {
        // Ready and stale games get the estimate the fake compiles at: 3 pipelines per shader, ~480/s, 24 KB each.
        static GameState G(string id, string name, string ver, string exe, int? shaders, GameStatus status, string reason,
            bool encrypted = false, string? unsupported = null, PlanStats? plan = null, long? cache = null, TimeSpan? time = null, string at = "steam", string? exePath = null)
        {
            bool warmable = status is GameStatus.Ready or GameStatus.Stale && shaders != null;
            var store = at switch { "steam" => Store.Steam, "epic" => Store.Epic, "xbox" => Store.Xbox, "ea" => Store.EA, "manual" => Store.Manual, _ => Store.Other };   // gog/ubisoft/battlenet/purple/hoyoplay/gaijin: Other, like the real sources
            var game = new Game($"{at}:{id}", name, store, $@"X:\Sample\{name}", exePath ?? $@"X:\Sample\{name}\{exe}");
            return new(game,
                new EngineInfo("Unreal", ver, null, "D3D12", encrypted, unsupported), AntiCheat.None, status, reason,
                shaders, plan, cache ?? (warmable ? shaders * 72_000L : null), time ?? (warmable ? TimeSpan.FromSeconds(shaders!.Value / 160.0) : null),
                null, null, null, false, null, KnownStutter: Core.Games.StutterList.Current.Find(game));
        }

        var ff7 = G("2909400", "FINAL FANTASY VII REBIRTH", "4.26", "ff7rebirth_.exe", 34_323, GameStatus.Warmed, "",
                plan: new PlanStats(994, 116_203, 180, 994, true, MiddlewareItems: 82, Uncovered: 214, StageSets: 117_411, LeftOut: 214), cache: 3_000_000_000, time: TimeSpan.FromMinutes(5))
            with
            {
                Engine = new EngineInfo("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null),
                WarmedDriverVersion = "610.88", WarmedAt = DateTimeOffset.Now.AddDays(-2), LastWarmTime = TimeSpan.FromSeconds(288),
                LastSession = new SessionStats(TimeSpan.FromMinutes(25), 995, 989, 5, 1, 59.6), CacheOnDisk = 2_870_000_000,
                LastFrames = SampleFrames(),
                LastWarmFailed = 3, LastWarmSkipped = 50, Community = new CommunityInfo(1_165, DateTimeOffset.Now.AddDays(-3), true),
                RecordingSharedAt = DateTimeOffset.Now.AddDays(-1), RecordingBytes = 58_700_000,
                Middleware = [   // FF7 with OptiScaler: its bundle folds into one tag
                    new("OptiScaler (FSR4, FidelityFX)", ["OptiScaler.dll", "amdxcffx64.dll", "amd_fidelityfx_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll",
                        "amd_fidelityfx_framegeneration_dx12.dll", "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "dlssg_to_fsr3_amd_is_better.dll"], 82),
                    new("DLSS", ["nvngx_dlss.dll"], 0)],
            };
        // its fork adds its own root-signature slots: a partial plan (the rest needs a recording)
        var hogwarts = G("990080", "Hogwarts Legacy", "4.27", "HogwartsLegacy.exe", 61_204, GameStatus.Stale, "driver changed: 596.36 -> 610.88",
                plan: new PlanStats(0, 41_422, 41_422, 105, false, Uncovered: 182_857, StageSets: 224_279, LeftOut: 182_857)) with { WarmedDriverVersion = "596.36", WarmedAt = DateTimeOffset.Now.AddDays(-30), LastWarmTime = TimeSpan.FromSeconds(371),
                    Middleware = [new("FSR 2/3", ["ffx_fsr2_api_dx12_x64.dll"], 14), new("XeSS", ["libxess.dll"], 0), new("DLSS", ["nvngx_dlss.dll"], 0),
                        new("FSR3 frame generation", ["dlssg_to_fsr3_amd_is_better.dll"], 0)] };
        const string materials = "shaders stored inside materials";
        const string packed = "no raw DXBC/DXIL shaders in its files (0.5 GB sampled): shaders are compressed or packed: needs an engine reader";
        games =
        [
            ff7,
            hogwarts,
            G("1817230", "Hi-Fi RUSH", "4.27", "Hi-Fi-RUSH.exe", 28_415, GameStatus.Ready, Core.Planning.Planner.NoRecording) with { RecorderMod = "ReShade", RecorderSkip = Core.App.ScsKiller.SkipForeignDll },
            G("1627720", "Lies of P", "4.27", "LOP-Win64-Shipping.exe", 52_880, GameStatus.Ready, Core.Planning.Planner.NoRecording),
            G("1139900", "Ghostrunner", "4.25", "Ghostrunner-Win64-Shipping.exe", 18_770, GameStatus.Ready, Core.Planning.Planner.Untested) with { Middleware = [new("DLSS", ["nvngx_dlss.dll"], 0)] },
            G("3489700", "Stellar Blade Demo", "4.26", "SB-Win64-Shipping.exe", 94_473, GameStatus.Ready, Core.Planning.Planner.Untested),
            G("1522820", "Orcs Must Die! 3", "4.26", "OrcsMustDie3.exe", 20_932, GameStatus.Ready, Core.Planning.Planner.NoRecording),
            // in the community database, which this signed-out PC doesn't download from
            // a real exe: its icon loads (the screenshots' library-groups checks it survives re-display)
            G("1623730", "Palworld", "5.1", "Palworld-Win64-Shipping.exe", 122_835, GameStatus.NeedsRecording, Core.Planning.Planner.Record + "; " + Core.App.ScsKiller.InDbNote, at: "xbox",
                exePath: Environment.ProcessPath)
                with { InCommunityDb = true, CommunityDbPsos = 18_406 },
            G("3400000", "Life is Strange: Reunion", "5.5", "Reunion-Win64-Shipping.exe", 44_736, GameStatus.NeedsRecording, Core.Planning.Planner.Record + "; not in the community database yet", at: "ea")
                with { InCommunityDb = false },
            // compiled without its ray tracing (the plan can't rebuild Unreal 5's), in the community database
            G("3300000", "Darwin's Paradox", "5.3", "DarwinsParadox-Win64-Shipping.exe", 108_542, GameStatus.Warmed, "warmed for driver 610.88; " + Core.App.ScsKiller.RtAfterRecordingNote, at: "epic",
                plan: new PlanStats(0, 96_210, 96_210, 64, false, Uncovered: 794, RtLibraries: 412, RtUncovered: 412, StageSets: 97_004, LeftOut: 794, RtInline: 0)) with
            {
                WarmedDriverVersion = "610.88", WarmedAt = DateTimeOffset.Now.AddDays(-1).AddHours(-3), LastWarmTime = TimeSpan.FromSeconds(204), CacheOnDisk = 2_310_000_000,
                LastWarmFailed = 0, LastWarmSkipped = 0, InCommunityDb = true, CommunityDbPsos = 2_914,
            },
            // r.RayTracing.AllowPipeline=0: hardware Lumen traces rays inline, compiled from its files; its DXIL libraries go unused
            G("3600000", "SILENT HILL: Townfall", "5.6", "Townfall-Win64-Shipping.exe", 45_741, GameStatus.Ready, "planned from a recording",
                plan: new PlanStats(12_437, 48_134, 48_134, 682, true, RtLibraries: 10_278, RtUncovered: 0, StageSets: 122_075, RtInline: 1_193)) with
            {
                Engine = new EngineInfo("Unreal", "5.6", null, "D3D12", false, null, NoRtPipelines: true),
                LastSession = new SessionStats(TimeSpan.FromMinutes(12), 4_388, 0, 3_902, 486, 141.0), RecordedEnough = true, RecordingBytes = 4_200_000,
            },
            G("3200000", "Windrose Demo", "5.5", "Windrose-Win64-Shipping.exe", null, GameStatus.Unsupported, "shaders cannot be read yet", encrypted: true),
            G("554620", "Life is Strange Remastered", "4.23", "LiSRemastered.exe", null, GameStatus.Unsupported, materials, unsupported: materials),
            // known to stutter (the real list): two recommended, Elden Ring blocked by anti-cheat
            // fully covered from its files alone, compiled an hour ago, nothing failed, a session without a stutter
            G("Sample.AtomicHeart", "Atomic Heart", "4.27", "AtomicHeart-WinGDK-Shipping.exe", 157_069, GameStatus.Warmed, "", at: "xbox",
                plan: new PlanStats(0, 188_730, 212, 118, false, StageSets: 188_730), cache: 4_500_000_000, time: TimeSpan.FromMinutes(7)) with
            {
                WarmedDriverVersion = "610.88", WarmedAt = DateTimeOffset.Now.AddHours(-1), LastWarmTime = TimeSpan.FromSeconds(372), CacheOnDisk = 4_310_000_000,
                LastWarmFailed = 0, LastWarmSkipped = 0, LastSession = new SessionStats(TimeSpan.FromMinutes(41), 2_310, 0, 2_310, 0, 1.2),
            },
            G("1286680", "Tiny Tina's Wonderlands", "4.21", "Wonderlands.exe", 38_112, GameStatus.NeedsRecording, Core.Planning.Planner.Record) with { Playing = true },
            G("1245620", "ELDEN RING", "-", "eldenring.exe", null, GameStatus.Unsupported, "needs a recording, which EasyAntiCheat blocks") with { AntiCheat = AntiCheat.EasyAntiCheat,
                Engine = new EngineInfo("FromSoft", "Dantelion", null, "D3D12", false, null), OfflineEligible = true, OfflineRecord = true },
            G("293760", "Automation", "4.27", "Automation-Win64-Shipping.exe", null, GameStatus.Unsupported, packed, unsupported: packed, at: "gog"),
            // added by the user from their exe: no store launches them
            G("5f1c0e9a2b7d4c30", "The Talos Principle 2", "5.3", "Talos2-Win64-Shipping.exe", 71_244, GameStatus.Ready, Core.Planning.Planner.NoRecording, at: "manual"),
            G("a93e4b1170cd2f86", "Satisfactory", "5.3", "FactoryGameSteam-Win64-Shipping.exe", 39_512, GameStatus.Unsupported,
                "needs a recording, " + ScsKiller.ManualNoRecording, at: "manual") with { RecorderSkip = ScsKiller.SkipManual, RootUnconfirmed = true },   // its folder not confirmed yet
        ];
        Vendor = vendor;
        timer = new Timer(_ => Tick(), null, 500, 500);
    }

    /// <summary>The sample NVIDIA GPU; screenshots swap in an Intel one for the Library's notice.</summary>
    public IGpuVendorBackend Vendor { get; set; }
    public Settings Settings { get; set; } = new(30, WarmPriority.BelowNormal, DriverUpdateMode.Ask, 8, true);
    public IReadOnlyList<GameState> Games { get { lock (gate) return HideGames ? [] : games.ToList(); } }

    /// <summary>Screenshots of a scan in progress: scans wait for <see cref="HoldScan"/>; <see cref="HideGames"/> = first run.</summary>
    public TaskCompletionSource? HoldScan { get; set; }
    public bool HideGames { get; set; }
    public void ShowGames() { HideGames = false; GameChanged?.Invoke(Games[0]); }
    public IReadOnlyList<QueueItem> Queue { get { lock (gate) return queue.ToList(); } }
    public event Action<GameState>? GameChanged;
    public event Action<QueueItem>? QueueChanged;

    public async Task<IReadOnlyList<GameState>> ScanAsync(CancellationToken ct, bool userRequested = false)
    {
        if (HoldScan is { } hold) await hold.Task;
        await Task.Delay(600, ct);
        return Games;
    }

    public IReadOnlyList<GameState> StaleGames() { lock (gate) return games.Where(g => g.Status == GameStatus.Stale).ToList(); }
    public IReadOnlyList<GameState> DriverStaleGames() => StaleGames().Where(g => g.WarmedDriverVersion != vendor.Gpu.DriverVersion).ToList();
    public void DismissStale() { }
    public bool QueueRunning => running;
    public bool Compiling => Queue.Any(Format.Running);
    public bool SetEncryptionKey(string gameId, string key) => false;
    public bool ShouldNotifyStale() => Settings.OnDriverUpdate == DriverUpdateMode.Ask && DriverStaleGames().Count > 0;
    public void ApplyDriverUpdateMode() { }
    public Task<IReadOnlyList<GameState>> RescanAsync(CancellationToken ct, bool userRequested = false) => ScanAsync(ct);
    public void EnqueueWhenIdle(string gameId) => Enqueue(gameId);

    /// <summary>Adds at the end; runs only after StartQueue, like the real queue.</summary>
    public void Enqueue(string gameId) => Update(() =>
    {
        if (queue.Any(q => q.GameId == gameId && !q.PlanCheck && q.Stage is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped))) return null;
        queue.RemoveAll(q => q.GameId == gameId);
        var item = new QueueItem(gameId, QueueStage.Waiting, null, null);
        queue.Add(item);
        return item;
    });

    /// <summary>ScsKiller.CheckPlans' background plan rebuilds: listed apart, never run here.</summary>
    public void CheckPlans(params string[] gameIds)
    {
        foreach (var id in gameIds) Update(() =>
        {
            var item = new QueueItem(id, QueueStage.Waiting, null, null, "starts when the PC is idle", PlanCheck: true);
            queue.Add(item);
            return item;
        });
    }

    public void Remove(string gameId) => Update(() =>
    {
        var item = queue.FirstOrDefault(q => q.GameId == gameId);
        if (item is null) return null;
        queue.Remove(item);
        return item with { Stage = QueueStage.Stopped };
    });

    public void Compile(string gameId)
    {
        Enqueue(gameId);
        if (!running) StartQueue();
    }

    public void StartQueue() => Update(() =>
    {
        paused = false;
        running = queue.Any(q => q.Stage == QueueStage.Waiting && !q.PlanCheck);
        return queue.FirstOrDefault(q => q.Stage == QueueStage.Waiting && !q.PlanCheck);
    });

    /// <summary>index = position among the waiting items (0 = next to run).</summary>
    public void MoveInQueue(string gameId, int index) => Update(() =>
    {
        var item = queue.FirstOrDefault(q => q.GameId == gameId && q.Stage == QueueStage.Waiting);
        if (item is null) return null;
        queue.Remove(item);
        var waiting = queue.Where(q => q.Stage == QueueStage.Waiting && !q.PlanCheck).ToList();
        index = Math.Clamp(index, 0, waiting.Count);
        queue.Insert(index < waiting.Count ? queue.IndexOf(waiting[index]) : waiting.Count > 0 ? queue.IndexOf(waiting[^1]) + 1 : queue.Count, item);
        return item;
    });

    public IReadOnlyList<CachePart> GameCaches(string gameId, bool gamePrecache = false)
    {
        lock (gate)
            return games.FirstOrDefault(x => x.Game.Id == gameId)?.CacheOnDisk is { } bytes ? [new CachePart(Core.App.ScsKiller.DriverPart, [], bytes)] : [];
    }

    public bool ClearGameCache(string gameId, bool gamePrecache = false)
    {
        GameState g;
        lock (gate)
        {
            int i = games.FindIndex(x => x.Game.Id == gameId);
            if (i < 0 || games[i].CacheOnDisk is not { } bytes) return false;
            if (queue.Any(q => q.GameId == gameId && q.Stage is QueueStage.Planning or QueueStage.Warming or QueueStage.Paused))
                throw new InvalidOperationException($"{games[i].Game.Name} is compiling; stop it first.");
            Interlocked.Add(ref vendor.Used, -bytes);
            games[i] = g = games[i] with { Status = GameStatus.Ready, StatusReason = "cache cleared", CacheOnDisk = null, WarmedDriverVersion = null, WarmedAt = null };
        }
        GameChanged?.Invoke(g);
        return true;
    }

    public void PauseQueue() => Update(() => { paused = true; return SetCurrent(q => q.Stage == QueueStage.Warming ? q with { Stage = QueueStage.Paused } : q); });
    public void ResumeQueue() => Update(() => { paused = false; return SetCurrent(q => q.Stage == QueueStage.Paused ? q with { Stage = QueueStage.Warming } : q); });
    public void StopQueue() => Update(() => { paused = running = false; return SetCurrent(q => q with { Stage = QueueStage.Stopped }); });

    public void InstallRecorder(string gameId) => SetRecorderOverride(gameId, RecorderOverride.On);
    public void UninstallRecorder(string gameId) => SetRecorderOverride(gameId, RecorderOverride.Off);
    public void SetRecorderOverride(string gameId, RecorderOverride value) => SetRecorder(gameId, value);
    public void SetRecordAlongsideMod(string gameId, bool on)
    {
        GameState g;
        lock (gate)
        {
            int i = games.FindIndex(x => x.Game.Id == gameId);
            var skip = on ? null : Core.App.ScsKiller.SkipForeignDll;
            bool eff = Core.App.ScsKiller.RecorderEffective(games[i].RecorderOverride, Settings.RecordAllGames, skip);
            games[i] = g = games[i] with { RecordAlongsideMod = on, RecorderSkip = skip, RecorderEffective = eff, RecorderInstalled = eff };
        }
        GameChanged?.Invoke(g);
    }
    public void ReconcileRecorders(string? gameId = null) { }   // the design data installs nothing by itself
    public void SetOfflineRecording(string gameId, bool on)
    {
        GameState g;
        lock (gate)
        {
            int i = games.FindIndex(x => x.Game.Id == gameId);
            games[i] = g = games[i] with { OfflineRecord = on };
        }
        GameChanged?.Invoke(g);
    }
    public Task StartOfflineSession(string gameId, bool confirmed) => Task.CompletedTask;   // the design data starts no game
    public bool ClearRecording(string gameId)
    {
        GameState g;
        lock (gate)
        {
            int i = games.FindIndex(x => x.Game.Id == gameId);
            if (i < 0 || games[i].RecordingBytes == 0) return false;
            if (games[i].Playing) throw new InvalidOperationException($"{games[i].Game.Name} is running");
            games[i] = g = games[i] with { RecordingBytes = 0, RecordingPaused = false };
        }
        GameChanged?.Invoke(g);
        return true;
    }

    public void SetCarefulCompile(string gameId, bool on)
    {
        GameState g;
        lock (gate)
        {
            int i = games.FindIndex(x => x.Game.Id == gameId);
            if (games[i].Careful is not { } c) throw new InvalidOperationException("the careful compile is for AMD drivers");
            games[i] = g = games[i] with { Careful = c with { On = on } };
        }
        GameChanged?.Invoke(g);
    }
    public void RefreshGame(string gameId) => GameChanged?.Invoke(Games.FirstOrDefault(g => g.Game.Id == gameId) ?? throw new ArgumentException($"unknown game '{gameId}'"));
    public void RefreshCacheSizes() { }   // sample data: nothing changes on disk
    public ManualAdd PreviewManualGame(string exePath) => throw new ArgumentException("The sample library can't add games.");
    public string? ManualFolderProblem(string exePath, string installDir) => null;
    public ManualAdd AddManualGame(string exePath, string? installDir = null) => throw new ArgumentException("The sample library can't add games.");
    public void RemoveManualGame(string gameId)
    {
        lock (gate) games.RemoveAll(g => g.Game.Id == gameId && g.Game.Store == Store.Manual);
        GameChanged?.Invoke(Games[0]);
    }

    /// <summary>Screenshots: put the running item at a given fraction of its compile.</summary>
    public void JumpTo(double fraction) => Update(() => SetCurrent(q =>
    {
        long total = TotalFor(q.GameId);
        long done = (long)(total * fraction);
        return q with { Stage = QueueStage.Warming, Progress = new(done, total, 3, 480, done * 24_000, Skipped: 50) };
    }, includeWaiting: true));

    void SetRecorder(string gameId, RecorderOverride value)
    {
        GameState g;
        lock (gate)
        {
            int i = games.FindIndex(x => x.Game.Id == gameId);
            if (value == RecorderOverride.On && games[i].AntiCheat != AntiCheat.None) throw new InvalidOperationException($"{games[i].Game.Name} uses anti-cheat; recording is not allowed.");
            bool on = ScsKiller.RecorderEffective(value, Settings.RecordAllGames, games[i].AntiCheat != AntiCheat.None ? ScsKiller.SkipAntiCheat : null);
            games[i] = g = games[i] with { RecorderOverride = value, RecorderEffective = on, RecorderInstalled = on };
        }
        GameChanged?.Invoke(g);
    }

    // Waiting -> Planning (one tick) -> Warming at ~480 pipelines/s scaled by threads -> Done (game becomes Warmed).
    void Tick()
    {
        GameState? finished = null;
        Update(() => SetCurrent(q =>
        {
            if (paused || !running) return q;
            if (q.Stage == QueueStage.Waiting) return q with { Stage = QueueStage.Planning };
            long total = TotalFor(q.GameId);
            if (q.Stage == QueueStage.Planning) return q with { Stage = QueueStage.Warming, Progress = new(0, total, 0, 0, 0) };
            var p = q.Progress!;
            double rate = 480.0 * Settings.Threads / 30 * (0.9 + Random.Shared.NextDouble() * 0.2);
            long done = Math.Min(total, p.Done + (long)(rate / 2));
            Interlocked.Add(ref vendor.Used, (done - p.Done) * 24_000);
            if (done < total) return q with { Progress = new(done, total, p.Failed + (Random.Shared.Next(40) == 0 ? 1 : 0), rate, done * 24_000, p.Skipped) };
            int i = games.FindIndex(g => g.Game.Id == q.GameId);
            finished = games[i] = games[i] with
            {
                Status = GameStatus.Warmed, StatusReason = "", WarmedDriverVersion = vendor.Gpu.DriverVersion, WarmedAt = DateTimeOffset.Now,
                LastWarmTime = TimeSpan.FromSeconds(total / 480.0), EstimatedCacheBytes = total * 24_000, CacheOnDisk = total * 24_000,
                Plan = games[i].Plan ?? new PlanStats(0, total, 180, 900, true, StageSets: total), LastWarmFailed = p.Failed, LastWarmSkipped = p.Skipped,
            };
            running = queue.Any(x => x.Stage == QueueStage.Waiting && !x.PlanCheck);   // the queue stops once it's empty
            return q with { Stage = QueueStage.Done, Progress = new(total, total, p.Failed, rate, Skipped: p.Skipped), Note = Core.App.ScsKiller.WarmCounts(p.Failed, p.Skipped) };
        }, includeWaiting: true));
        if (finished != null) GameChanged?.Invoke(finished);
    }

    long TotalFor(string gameId)
    {
        var g = games.First(x => x.Game.Id == gameId);
        return g.Plan is { } p ? p.Recorded + p.Generated + p.D3D11Shaders + p.MiddlewareItems : (g.ShaderCount ?? 10_000) * 3L;
    }

    QueueItem? SetCurrent(Func<QueueItem, QueueItem> change, bool includeWaiting = false)
    {
        int i = queue.FindIndex(q => !q.PlanCheck && (q.Stage is QueueStage.Planning or QueueStage.Warming or QueueStage.Paused
                                     || (includeWaiting && q.Stage == QueueStage.Waiting)));
        if (i < 0) return null;
        var next = change(queue[i]);
        if (next == queue[i]) return null;
        queue[i] = next;
        return next;
    }

    void Update(Func<QueueItem?> change)
    {
        QueueItem? item;
        lock (gate) item = change();
        if (item != null) QueueChanged?.Invoke(item);
    }

    sealed class FakeVendor : IGpuVendorBackend
    {
        public long Used = 11_600_000_000;
        CacheLimit limit = new(16L << 30, true);
        public GpuVendor Vendor => GpuVendor.Nvidia;
        public GpuInfo Gpu { get; } = new(GpuVendor.Nvidia, "NVIDIA GeForce", "610.88", 0, 32UL << 30);
        public VendorCaps Caps { get; } = new("nvidia-1", true, true, true);
        public CacheUsage GetCacheUsage() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache"),
            Interlocked.Read(ref Used), true);
        public CacheLimit? GetCacheLimit() => limit;
        public void SetCacheLimit(CacheLimit l) { Thread.Sleep(800); limit = l; }   // the real app runs it elevated (Elevated.Run) unless it is admin
        AutoShaderState auto = new(AutoShaderCompilation.Off, false);   // NVIDIA App's default
        public AutoShaderState? GetAutoShaderCompilation() => auto;
        public void SetAutoShaderCompilation(AutoShaderCompilation level)
        {
            Thread.Sleep(800);   // the real backend writes the driver setting (admin) and runs NvOSC.exe
            if (SCSKiller.Core.Vendors.NvidiaBackend.RefuseReason(level, limit) is { } why) throw new InvalidOperationException(why);
            auto = new(level, level != AutoShaderCompilation.Off);
        }
    }
}
