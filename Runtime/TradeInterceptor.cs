using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ShoutRunner.Runtime;

public sealed class TradeInterceptor : IDisposable
{
    private static readonly Regex TradeRequest = new(@"^(.+?)(?: möchte mit dir handeln| wishes to trade with you)\.$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Config config;
    private readonly Func<bool> activeTeleport;
    private readonly Action interrupted;
    private DateTime cancelUntilUtc;
    private DateTime lastTradeUtc;
    private string recentName = "";
    private DateTime recentNameUtc;
    private string recentPayloadWorld = "";
    private bool wasOpen;
    private bool requestAlreadyHandled;
    private readonly Dictionary<string, (string world, DateTime time)> sources = new(StringComparer.OrdinalIgnoreCase);

    public bool CancelMode => DateTime.UtcNow < cancelUntilUtc;

    public TradeInterceptor(Config config, Func<bool> activeTeleport, Action interrupted)
    {
        this.config = config;
        this.activeTeleport = activeTeleport;
        this.interrupted = interrupted;
        Plugin.ChatGui.ChatMessage += OnChat;
        Plugin.ChatGui.LogMessage += OnLogMessage;
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (!activeTeleport() && !CancelMode) return;
        var source = message.SourceEntity;
        if (source == null || !source.IsPlayer) return;
        var name = source.Name.ToString();
        var world = source.HomeWorld.ValueNullable?.Name.ToString() ?? "";
        if (name.Length > 0 && world.Length > 0) sources[name] = (world, DateTime.UtcNow);
        if (sources.Count > 1000)
            foreach (var key in sources.Where(x => DateTime.UtcNow - x.Value.time > TimeSpan.FromSeconds(10)).Select(x => x.Key).ToList())
                sources.Remove(key);
    }

    private void OnChat(IHandleableChatMessage message)
    {
        if (!activeTeleport() && !CancelMode) return;
        var text = message.Message.TextValue;
        var match = TradeRequest.Match(text);
        if (!match.Success) return;
        recentName = match.Groups[1].Value.Trim();
        recentNameUtc = DateTime.UtcNow;
        var playerLink = message.Message.Payloads.OfType<PlayerPayload>()
            .FirstOrDefault(p => p.PlayerName.Equals(recentName, StringComparison.OrdinalIgnoreCase));
        recentPayloadWorld = playerLink == null ? "" : playerLink.World.ValueNullable?.Name.ToString() ?? "";
        HandleTrade();
        requestAlreadyHandled = true;
    }

    public unsafe void Tick()
    {
        var open = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.TradeOpen];
        try { open |= Plugin.GameGui.GetAddonByName("Trade", 1).IsVisible; } catch { }
        if (open && (activeTeleport() || CancelMode))
        {
            if (!wasOpen && !requestAlreadyHandled) HandleTrade();
            requestAlreadyHandled = false;
            try { InventoryManager.Instance()->RefuseTrade(); }
            catch (Exception ex) { Plugin.Log.Warning($"Trade refusal failed: {ex.Message}"); }
        }
        wasOpen = open;
        if (!open && DateTime.UtcNow - lastTradeUtc > TimeSpan.FromMilliseconds(500)) requestAlreadyHandled = false;
    }

    private void HandleTrade()
    {
        var now = DateTime.UtcNow;
        if (!activeTeleport() && !CancelMode) return;
        lastTradeUtc = now;
        cancelUntilUtc = now.AddSeconds(5);
        var raw = now - recentNameUtc < TimeSpan.FromSeconds(3) ? recentName : "Unknown";
        var payloadWorld = recentPayloadWorld;
        recentName = "";
        recentNameUtc = DateTime.MinValue;
        recentPayloadWorld = "";
        var matched = WorldCatalog.All().OrderByDescending(w => w.Name.Length)
            .FirstOrDefault(w => raw.EndsWith("@" + w.Name, StringComparison.OrdinalIgnoreCase));
        var exactName = matched == null ? raw : raw[..^(matched.Name.Length + 1)];
        var nearby = Plugin.ObjectTable.OfType<IPlayerCharacter>()
            .Where(p => p.Name.TextValue.Equals(exactName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var observedWorld = nearby.Count == 1 ? nearby[0].HomeWorld.ValueNullable?.Name.ToString() : null;
        var sourceWorld = sources.TryGetValue(exactName, out var source) && now - source.time < TimeSpan.FromSeconds(5)
            ? source.world : null;
        var incident = new TradeIncident
        {
            AtUtc = now, Player = exactName,
            HomeWorld = matched?.Name ?? (payloadWorld.Length > 0 ? payloadWorld : null)
                ?? sourceWorld ?? observedWorld ?? "Unknown", CurrentWorld = TravelObservation.World,
            City = TravelObservation.City,
        };
        config.TradeIncidents.Add(incident);
        config.Save();
        interrupted();
    }

    public void Dispose()
    {
        Plugin.ChatGui.ChatMessage -= OnChat;
        Plugin.ChatGui.LogMessage -= OnLogMessage;
    }
}
