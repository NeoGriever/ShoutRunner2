using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ShoutRunner.Runtime;

public sealed class RouteExecutor : IDisposable
{
    private enum Phase { Idle, PickWorld, Travel, Actions, Wait, Move, RowDelay, Paused }
    private enum TravelKind { World, City, Home }
    private readonly Config config;
    private readonly LifestreamBridge lifestream;
    private readonly NavmeshBridge navmesh;
    private List<WorldJob> jobs = new();
    private readonly Dictionary<string, DateTime> retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private Phase phase;
    private Phase beforePause;
    private bool pausePending;
    private DateTime restartAt = DateTime.MinValue;
    private Guid restartPresetId;
    private DateTime nextGilPoll = DateTime.MinValue;

    private WorldJob? current;
    private WorldProgress? progress;
    private int actionIndex;
    private int rowIndex;
    private DateTime phaseStarted;
    private DateTime readySince;
    private DateTime lastMoveAttempt;
    private bool moveIssued;
    private DateTime nextTravelAttempt;
    private DateTime pausedAt;
    private bool travelSent;
    private bool sawBusy;
    private TravelKind travelKind;
    private string travelTarget = "";
    private uint beforeMap;
    private uint beforeTerritory;
    private uint targetTerritory;
    private bool blockedByToast;
    private string toastReason = "";
    private RouteNode? activeNode;
    private string activeCity = "";
    private string preferredDc = "";
    private string deferredDc = "";

    public RouteExecutor(Config config, LifestreamBridge lifestream, NavmeshBridge navmesh)
    {
        this.config = config; this.lifestream = lifestream; this.navmesh = navmesh;
        Plugin.Framework.Update += Tick;
    }
    public bool IsRunning => phase != Phase.Idle;
    public bool IsPaused => phase == Phase.Paused;
    public bool CanPause => IsRunning;
    public bool PausePending => pausePending;
    public bool RestartPending => restartAt != DateTime.MinValue;
    public string MiniAction => RestartPending ? "Restart queued" : pausePending ? "Pause queued" :
        phase == Phase.Paused ? "Paused" : phase == Phase.Idle ? "---" :
        phase == Phase.Travel ? $"Travel to: {travelTarget}" :
        current != null ? $"{current.World}: {Status}" : Status;
    public long? GilDelta => config.GilStart.HasValue && config.GilPresetId == CurrentPresetId && config.GilLast.HasValue
        ? config.GilLast.Value - config.GilStart.Value : null;

    public static float JobCompletion(WorldJob job, WorldProgress? p)
    {
        if (p == null) return 0;
        if (p.Result is WorldResult.Complete or WorldResult.Inaccessible) return 1;
        var total = 1.0f; // Travel/arrival has one step.
        foreach (var action in job.Actions)
            total += action.Node.Kind is NodeKind.Speak or NodeKind.Cmd
                ? Math.Max(1, action.Node.Rows.Count(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Text))) : 1;
        var done = p.ArrivalConfirmed ? 1.0f : 0.0f;
        for (var i = 0; i < Math.Min(p.ActionIndex, job.Actions.Count); i++)
        {
            var node = job.Actions[i].Node;
            done += node.Kind is NodeKind.Speak or NodeKind.Cmd
                ? Math.Max(1, node.Rows.Count(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Text))) : 1;
        }
        if (p.ActionIndex < job.Actions.Count && p.ArrivalConfirmed)
        {
            var node = job.Actions[p.ActionIndex].Node;
            if (node.Kind is NodeKind.Speak or NodeKind.Cmd)
                done += Math.Min(p.RowIndex, Math.Max(1, node.Rows.Count(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Text))));
        }
        return Math.Clamp(done / total, 0, 1);
    }
    public float OverallCompletion(Guid presetId)
    {
        var preset = config.Presets.FirstOrDefault(p => p.Id == presetId);
        if (preset == null) return 0;
        var planned = RoutePlanner.Build(preset);
        if (planned.Count == 0) return 0;
        return (float)planned.Average(j => (double)JobCompletion(j, config.Progress.FirstOrDefault(p =>
            p.PresetId == presetId && p.NodeId == j.NodeId && p.World.Equals(j.World, StringComparison.OrdinalIgnoreCase))));
    }

    private static unsafe long? ReadGil()
    {
        try
        {
            if (!Plugin.PlayerState.IsLoaded) return null;
            var manager = InventoryManager.Instance();
            if (manager == null) return null;
            var container = manager->GetInventoryContainer(InventoryType.Currency);
            if (container == null || !container->IsLoaded) return null;
            var slot = container->GetInventorySlot(0);
            return slot == null ? null : (long)slot->Quantity;
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Gil read failed"); return null; }
    }
    private void SampleGil(bool resetBaseline = false)
    {
        var value = ReadGil();
        if (!value.HasValue) return;
        if (resetBaseline || config.GilPresetId != CurrentPresetId || !config.GilStart.HasValue)
        {
            config.GilPresetId = CurrentPresetId;
            config.GilStart = value.Value;
            config.GilLast = value.Value;
            config.Save();
            Log($"Gil baseline: {value.Value:N0}");
        }
        else if (config.GilLast != value)
        {
            var change = value.Value - config.GilLast!.Value;
            config.GilLast = value.Value;
            config.Save();
            Log($"Gil change: {change:+#;-#;0} (balance {value.Value:N0}, net {GilDelta:+#;-#;0})");
        }
    }
    public void RequestRestart(Guid presetId)
    {
        if (RestartPending) return;
        Stop();
        restartPresetId = presetId;
        restartAt = DateTime.UtcNow.AddSeconds(2);
        Status = "Restarting in 2 seconds";
    }

    public bool IsTeleporting => phase == Phase.Travel && travelSent;
    public string Status { get; private set; } = "Ready";
    public IReadOnlyList<RouteEvent> Events => config.EventLog;
    public WorldJob? CurrentJob => current;
    public void Dispose() => Plugin.Framework.Update -= Tick;
    public void Log(string message)
    {
        config.EventLog.Add(new RouteEvent
        {
            AtUtc = DateTime.UtcNow, PresetId = CurrentPresetId,
            CurrentWorld = TravelObservation.World, TargetWorld = current?.World ?? "",
            DataCenter = current?.DataCenter ?? "", Message = message,
        });
        if (config.EventLog.Count > 5000) config.EventLog.RemoveAt(0);
        config.Save();
        Plugin.Log.Information(message);
    }

    public bool Start(RoutePreset preset)
    {
        if (IsRunning) return false;
        jobs = RoutePlanner.Build(preset);
        if (jobs.Count == 0) { Status = "No worlds in this preset"; return false; }
        var currentRegion = TravelObservation.RegionOf(TravelObservation.World);
        var open = jobs.Where(j => !config.Progress.Any(p => p.PresetId == preset.Id && p.NodeId == j.NodeId
            && p.World.Equals(j.World, StringComparison.OrdinalIgnoreCase)
            && p.Result is WorldResult.Complete or WorldResult.Inaccessible)).ToList();
        if (open.Any(j => j.Region != currentRegion) && !config.AllowCrossRegion)
        {
            Status = "Physical data center travel is disabled"; return false;
        }
        foreach (var job in jobs)
            if (!config.Progress.Any(p => p.PresetId == preset.Id && p.NodeId == job.NodeId && p.World.Equals(job.World, StringComparison.OrdinalIgnoreCase)))
                config.Progress.Add(new WorldProgress { PresetId = preset.Id, NodeId = job.NodeId, World = job.World, DataCenter = job.DataCenter });
        config.Save();
        CurrentPresetId = preset.Id;
        config.AutoResumePresetId = preset.Id;
        config.Save();
        phase = Phase.PickWorld;
        pausePending = false;
        SampleGil(config.GilPresetId != preset.Id || !config.GilStart.HasValue);
        preferredDc = "";
        deferredDc = "";
        Log($"Preset '{preset.Name}' started: {jobs.Count} world jobs");
        return true;
    }
    public Guid CurrentPresetId { get; private set; }
    public void Stop()
    {
        if (!IsRunning) return;
        if (phase == Phase.Travel && (travelSent || lifestream.IsBusy())) lifestream.Abort();
        if (phase == Phase.Move || (phase == Phase.Paused && beforePause == Phase.Move)) navmesh.Stop();
        ResetUnfinishedTravelTimers();
        current = null; progress = null; phase = Phase.Idle; pausePending = false;
        config.AutoResumePresetId = null; config.Save();
        Status = "Stopped"; Log("Route stopped");
    }
    public void TogglePause()
    {
        if (!IsRunning) return;
        if (pausePending) { pausePending = false; Status = "Pause cancelled"; return; }
        if (phase == Phase.Travel)
        {
            pausePending = true;
            Status = "Pause queued after travel";
            Log("Pause queued after current travel");
            return;
        }
        if (phase == Phase.Paused)
        {
            var pausedFor = DateTime.UtcNow - pausedAt;
            if (beforePause is Phase.Wait or Phase.RowDelay) readySince += pausedFor;
            if (beforePause == Phase.Move) phaseStarted += pausedFor;
            foreach (var item in config.Progress.Where(p => p.PresetId == CurrentPresetId && !p.ArrivalConfirmed
                && p.Result is WorldResult.Traveling or WorldResult.Deferred))
                if (item.FirstAttemptUtc.HasValue) item.FirstAttemptUtc += pausedFor;
            config.Save();
            phase = beforePause; Status = "Resumed";
        }
        else if (IsRunning)
        {
            beforePause = phase; pausedAt = DateTime.UtcNow;
            phase = Phase.Paused; Status = "Paused";
        }
    }
    public void ResetProgress(Guid presetId)
    {
        if (IsRunning) return;
        config.Progress.RemoveAll(x => x.PresetId == presetId);
        config.EventLog.RemoveAll(x => x.PresetId == presetId);
        if (config.GilPresetId == presetId)
        {
            config.GilStart = null; config.GilLast = null; config.GilPresetId = Guid.Empty;
        }
        config.Save();
    }

    public void OnTradeInterrupted()
    {
        if (!IsTeleporting) return;
        Log($"Trade interrupted travel to {travelTarget}; restarting Lifestream");
        lifestream.Abort();
        travelSent = false; sawBusy = false; blockedByToast = false;
        nextTravelAttempt = DateTime.UtcNow.AddMilliseconds(500);
        phaseStarted = DateTime.UtcNow;
    }
    public void OnTravelError(string text)
    {
        if (!IsTeleporting || travelKind != TravelKind.World) return;
        var s = text.ToLowerInvariant();
        var known = false;
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<LogMessage>();
            known = new uint[] { 9419, 9426, 9427 }.Any(id =>
                sheet.GetRow(id).Text.ToString().Trim().Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        if (!known && !(s.Contains("congest") || s.Contains("unable to visit") || s.Contains("cannot visit")
            || s.Contains("überlast") || s.Contains("nicht besucht") || s.Contains("unavailable"))) return;
        blockedByToast = true; toastReason = text;
    }

    private WorldProgress ProgressOf(WorldJob job) => config.Progress.First(p => p.PresetId == CurrentPresetId && p.NodeId == job.NodeId && p.World.Equals(job.World, StringComparison.OrdinalIgnoreCase));
    private string Key(WorldJob job) => $"{job.NodeId}:{job.World}";

    private void Tick(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (RestartPending && now >= restartAt)
        {
            restartAt = DateTime.MinValue;
            var preset = config.Presets.FirstOrDefault(p => p.Id == restartPresetId);
            if (preset != null)
            {
                ResetProgress(preset.Id);
                Start(preset);
            }
            return;
        }
        if (RestartPending) return;
        if (IsRunning && now >= nextGilPoll)
        {
            nextGilPoll = now.AddMilliseconds(300);
            SampleGil();
        }
        if (phase is Phase.Idle or Phase.Paused) return;
        if (pausePending && phase != Phase.Travel)
        {
            pausePending = false;
            TogglePause();
            return;
        }
        var oldAction = actionIndex;
        var oldRow = rowIndex;
        switch (phase)
        {
            case Phase.PickWorld: PickWorld(now); break;
            case Phase.Travel: TickTravel(now); break;
            case Phase.Actions: TickActions(now); break;
            case Phase.Wait: TickWait(now); break;
            case Phase.Move: TickMove(now); break;
            case Phase.RowDelay:
                if (now >= readySince) phase = Phase.Actions;
                break;
        }
        if (progress != null && (oldAction != actionIndex || oldRow != rowIndex))
        {
            progress.ActionIndex = actionIndex;
            progress.RowIndex = rowIndex;
            config.Save();
        }
    }
    private void PickWorld(DateTime now)
    {
        foreach (var job in jobs)
        {
            var p = ProgressOf(job);
            if (p.Result is WorldResult.Complete or WorldResult.Inaccessible) continue;
            if (!p.ArrivalConfirmed && p.FirstAttemptUtc.HasValue && now - p.FirstAttemptUtc.Value >= TimeSpan.FromMinutes(30)
                && !(TravelObservation.World.Equals(job.World, StringComparison.OrdinalIgnoreCase) && !TravelObservation.Loading))
            {
                p.Result = WorldResult.Inaccessible; p.Detail = "Unreachable for 30 minutes";
                config.Save(); Log($"{job.World}: inaccessible after 30 minutes");
            }
        }
        var remaining = jobs.Where(j => ProgressOf(j).Result is not (WorldResult.Complete or WorldResult.Inaccessible)).ToList();
        if (remaining.Count == 0)
        {
            phase = Phase.Idle; Status = "All worlds completed";
            config.AutoResumePresetId = null; config.Save();
            Log("Preset completed"); return;
        }
        current = RouteScheduler.ChooseNext(remaining, ProgressOf,
            j => !retryAfter.TryGetValue(Key(j), out var until) || until <= now,
            preferredDc, deferredDc);
        if (current == null)
        {
            Status = $"Waiting to retry worlds ({remaining.Count} remaining)";
            return;
        }
        preferredDc = current.DataCenter;
        progress = ProgressOf(current);
        var resumeActions = progress.Result == WorldResult.Running;
        actionIndex = resumeActions ? Math.Max(0, progress.ActionIndex) : 0;
        rowIndex = resumeActions ? Math.Max(0, progress.RowIndex) : 0;
        if (resumeActions && Enum.TryParse<TravelKind>(progress.PendingTravel, out var pendingKind)
            && pendingKind is TravelKind.Home or TravelKind.City)
        {
            BeginTravel(pendingKind, progress.PendingTarget);
            return;
        }
        var actualRegion = TravelObservation.RegionOf(TravelObservation.World);
        if (current.Region != actualRegion &&
            !(config.AllowCrossRegion && (current.Region == "Oceania"
                || (actualRegion == "Oceania" && current.World.Equals(TravelObservation.HomeWorld, StringComparison.OrdinalIgnoreCase)))))
        {
            StopWithError($"Travel from {actualRegion} to {current.Region} / {current.World} is unavailable");
            return;
        }
        progress.Result = WorldResult.Traveling;
        config.Save();
        if (TravelObservation.World.Equals(current.World, StringComparison.OrdinalIgnoreCase) && !TravelObservation.Loading)
            ArrivedWorld();
        else BeginTravel(TravelKind.World, current.World);
    }
    private void BeginTravel(TravelKind kind, string target)
    {
        travelKind = kind; travelTarget = target;
        targetTerritory = 0;
        if (kind == TravelKind.City && AetheryteCatalog.TryFindByName(target, false, out var targetAetheryte))
            targetTerritory = AetheryteCatalog.GetTerritory(targetAetheryte);
        if (progress != null)
        {
            progress.PendingTravel = kind.ToString();
            progress.PendingTarget = target;
            config.Save();
        }
        travelSent = sawBusy = blockedByToast = false;
        beforeMap = TravelObservation.MapId;
        beforeTerritory = Plugin.ClientState.TerritoryType;
        phaseStarted = DateTime.UtcNow;
        nextTravelAttempt = phaseStarted;
        phase = Phase.Travel;
        Status = $"Lifestream: {target}";
    }
    private bool TargetReached() => travelKind switch
    {
        TravelKind.World or TravelKind.Home => TravelObservation.World.Equals(travelTarget, StringComparison.OrdinalIgnoreCase),
        TravelKind.City => (TravelObservation.InCity(travelTarget)
                || (targetTerritory != 0 && Plugin.ClientState.TerritoryType == targetTerritory))
            && (sawBusy || TravelObservation.MapId != beforeMap || Plugin.ClientState.TerritoryType != beforeTerritory),
        _ => false,
    };
    private void TickTravel(DateTime now)
    {
        if (travelKind == TravelKind.World && progress?.FirstAttemptUtc is { } firstAttempt
            && now - firstAttempt >= TimeSpan.FromMinutes(30) && !TargetReached())
        {
            lifestream.Abort(); travelSent = false;
            progress.Result = WorldResult.Inaccessible;
            progress.Detail = "Unreachable for 30 minutes";
            progress.PendingTravel = ""; progress.PendingTarget = "";
            config.Save();
            Log($"{travelTarget}: inaccessible after 30 minutes");
            current = null; progress = null; phase = Phase.PickWorld;
            return;
        }
        if (blockedByToast) { FailTravel(toastReason); return; }
        var attemptLimit = travelKind == TravelKind.City ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15);
        if (now - phaseStarted > attemptLimit) { FailTravel("Travel timed out"); return; }
        if (!travelSent)
        {
            if (now < nextTravelAttempt || lifestream.IsBusy() || TravelObservation.Loading
                || Plugin.Condition[ConditionFlag.TradeOpen]) return;
            if (!lifestream.Available) { StopWithError("Lifestream is not loaded"); return; }
            if (TargetReached() || (travelKind == TravelKind.City
                && (TravelObservation.InCity(travelTarget)
                    || (targetTerritory != 0 && Plugin.ClientState.TerritoryType == targetTerritory))))
            { CompleteTravel(); return; }
            if (travelKind == TravelKind.World && progress != null)
            {
                progress.FirstAttemptUtc ??= now;
                progress.Attempts++;
                config.Save();
            }
            bool accepted;
            if (travelKind == TravelKind.City)
            {
                if (!AetheryteCatalog.TryFindByName(travelTarget, false, out var aetheryte))
                { StopWithError($"City aetheryte is unavailable: {travelTarget}"); return; }
                accepted = lifestream.Teleport(aetheryte.AetheryteId, aetheryte.SubIndex);
            }
            else accepted = lifestream.ChangeWorld(travelTarget);
            if (!accepted)
            {
                if (travelKind == TravelKind.World) { FailTravel("Lifestream rejected travel"); return; }
                StopWithError($"Lifestream rejected travel to {travelTarget}"); return;
            }
            travelSent = true; phaseStarted = now; readySince = DateTime.MinValue;
            Log($"Travel started: {travelTarget}"); return;
        }
        var busy = lifestream.IsBusy() || TravelObservation.TeleportBusy;
        if (busy) { sawBusy = true; readySince = DateTime.MinValue; return; }
        if (!TargetReached())
        {
            readySince = DateTime.MinValue;
            if (!lifestream.IsBusy() && now - phaseStarted > TimeSpan.FromSeconds(10) && !sawBusy)
                FailTravel("Travel did not start");
            return;
        }
        if (readySince == DateTime.MinValue) readySince = now;
        if (now - readySince >= TimeSpan.FromSeconds(2)) CompleteTravel();
    }
    private void CompleteTravel()
    {
        Log($"Arrival confirmed: {travelTarget} ({TravelObservation.World})");
        travelSent = false;
        if (progress != null)
        {
            progress.PendingTravel = ""; progress.PendingTarget = "";
            config.Save();
        }
        if (travelKind == TravelKind.World) ArrivedWorld();
        else { actionIndex++; rowIndex = 0; phase = Phase.Actions; }
    }
    private void ArrivedWorld()
    {
        if (progress == null || current == null) return;
        progress.Result = WorldResult.Running; progress.Detail = "Arrival confirmed";
        progress.ArrivalConfirmed = true;
        progress.PendingTravel = ""; progress.PendingTarget = "";
        config.Save(); phase = Phase.Actions;
        Status = $"{current.DataCenter} / {current.World}: actions";
    }
    private void FailTravel(string reason)
    {
        Log($"{travelTarget}: {reason}");
        lifestream.Abort(); travelSent = false;
        if (progress != null) { progress.PendingTravel = ""; progress.PendingTarget = ""; }
        if (travelKind == TravelKind.World && current != null && progress != null)
        {
            progress.Result = WorldResult.Deferred; progress.Detail = reason;
            retryAfter[Key(current)] = DateTime.UtcNow.AddSeconds(30);
            deferredDc = current.DataCenter;
            preferredDc = "";
            config.Save(); current = null; progress = null; phase = Phase.PickWorld;
        }
        else StopWithError(reason);
    }
    private void StopWithError(string message)
    {
        if (progress != null) { progress.Result = WorldResult.Deferred; progress.Detail = message; config.Save(); }
        ResetUnfinishedTravelTimers();
        phase = Phase.Idle; Status = message;
        config.AutoResumePresetId = null; config.Save();
        Log($"Error: {message}");
    }
    private void ResetUnfinishedTravelTimers()
    {
        foreach (var item in config.Progress.Where(p => p.PresetId == CurrentPresetId && !p.ArrivalConfirmed
            && p.Result is WorldResult.Traveling or WorldResult.Deferred))
            item.FirstAttemptUtc = null;
        config.Save();
    }
    private void TickActions(DateTime now)
    {
        if (current == null || progress == null) { phase = Phase.PickWorld; return; }
        if (actionIndex >= current.Actions.Count)
        {
            progress.Result = WorldResult.Complete; progress.Detail = "All actions completed";
            config.Save(); Log($"{current.World} completed");
            current = null; progress = null; phase = Phase.PickWorld; return;
        }
        var action = current.Actions[actionIndex];
        var node = action.Node; activeNode = node;
        switch (node.Kind)
        {
            case NodeKind.City:
                activeCity = action.Target;
                if (TravelObservation.InCity(action.Target)
                    || (AetheryteCatalog.TryFindByName(action.Target, false, out var presentAetheryte)
                        && AetheryteCatalog.GetTerritory(presentAetheryte) == Plugin.ClientState.TerritoryType))
                { actionIndex++; break; }
                BeginTravel(TravelKind.City, action.Target); break;
            case NodeKind.Home:
                if (TravelObservation.World.Equals(TravelObservation.HomeWorld, StringComparison.OrdinalIgnoreCase))
                { actionIndex++; break; }
                BeginTravel(TravelKind.Home, TravelObservation.HomeWorld); break;
            case NodeKind.Speak:
            case NodeKind.Cmd:
                TickRows(node, now); break;
            case NodeKind.Wait:
                readySince = now.AddSeconds(Math.Max(0, node.DelaySeconds));
                phase = Phase.Wait; Status = $"Waiting: {node.DelaySeconds:F1}s"; break;
            case NodeKind.Goto:
                if (!node.IgnoreMapCheck && node.MapId != TravelObservation.MapId)
                { Log($"Goto skipped: map {node.MapId} != {TravelObservation.MapId}"); actionIndex++; break; }
                phaseStarted = now; lastMoveAttempt = DateTime.MinValue; moveIssued = false;
                phase = Phase.Move; Status = $"Goto: {node.Position}"; break;
            default: actionIndex++; break;
        }
    }
    private void TickRows(RouteNode node, DateTime now)
    {
        if (rowIndex >= node.Rows.Count) { rowIndex = 0; actionIndex++; return; }
        var row = node.Rows[rowIndex];
        if (!row.Enabled || string.IsNullOrWhiteSpace(row.Text)) { rowIndex++; return; }
        if (node.Kind == NodeKind.Cmd && !row.Text.TrimStart().StartsWith('/'))
        { Log($"Invalid command skipped: {row.Text}"); rowIndex++; return; }
        var command = node.Kind == NodeKind.Cmd ? row.Text.Trim() :
            $"/{(row.Speech == SpeechKind.Shout ? "shout" : row.Speech == SpeechKind.Yell ? "yell" : "say")} {row.Text}";
        if (Plugin.SendChatCommand(command)) Log($"{node.Kind} submitted: {command}");
        else Log($"{node.Kind} rejected: {command}");
        rowIndex++;
        readySince = now.AddSeconds(Math.Max(0, row.DelaySeconds));
        phase = Phase.RowDelay;
    }
    private void TickWait(DateTime now)
    {
        if (activeNode == null || now < readySince) return;
        if (activeNode.WaitLifestream && !lifestream.Available) { StopWithError("Wait requires Lifestream"); return; }
        if (activeNode.WaitNavmesh && !navmesh.Available) { StopWithError("Wait requires vnavmesh"); return; }
        if (activeNode.WaitLifestream && lifestream.IsBusy()) return;
        if (activeNode.WaitNavmesh && !TravelObservation.Loading && (navmesh.IsMoving() || navmesh.IsFinding())) return;
        actionIndex++; phase = Phase.Actions;
    }
    private void TickMove(DateTime now)
    {
        if (activeNode == null) { phase = Phase.Actions; return; }
        if (!navmesh.Available) { StopWithError("Goto requires vnavmesh"); return; }
        if (now - phaseStarted >= TimeSpan.FromMinutes(5))
        {
            navmesh.Stop(); Log("Goto timed out after five minutes; node completed");
            actionIndex++; phase = Phase.Actions; return;
        }
        if (!activeNode.IgnoreMapCheck && activeNode.MapId != TravelObservation.MapId)
        { navmesh.Stop(); Log("Goto cancelled: map changed"); actionIndex++; phase = Phase.Actions; return; }
        var pos = TravelObservation.Position;
        if (moveIssued && pos.HasValue && Vector3.Distance(pos.Value, activeNode.Position) <= Math.Max(0.2f, activeNode.ArrivalRadius))
        { Log("Goto arrival confirmed"); actionIndex++; phase = Phase.Actions; return; }
        if (TravelObservation.Loading || !navmesh.IsReady()) return;
        if (!navmesh.IsMoving() && !navmesh.IsFinding() && now - lastMoveAttempt >= TimeSpan.FromSeconds(3))
        {
            lastMoveAttempt = now;
            moveIssued = navmesh.Move(activeNode.Position) || moveIssued;
        }
    }
}
