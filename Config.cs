using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.IO;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace ShoutRunner;

public enum NodeKind { Phys, Log, World, City, Speak, Wait, Goto, Home, Cmd }
public enum SpeechKind { Shout, Yell, Say }
public enum WorldResult { Pending, Traveling, Running, Complete, Deferred, Inaccessible }

[Serializable]
public sealed class TextRow
{
    public Guid Id = Guid.NewGuid();
    public bool Enabled = true;
    public string Text = "";
    public SpeechKind Speech = SpeechKind.Shout;
    public float DelaySeconds = 1;
    public TextRow Copy() => new() { Enabled = Enabled, Text = Text, Speech = Speech, DelaySeconds = DelaySeconds };
}

[Serializable]
public sealed class RouteNode
{
    public Guid Id = Guid.NewGuid();
    public NodeKind Kind;
    public bool Enabled = true;
    public bool Collapsed;
    public string Name = "";
    public List<string> Choices = new();
    public bool All;
    public List<RouteNode> Children = new();
    public List<TextRow> Rows = new();
    public float DelaySeconds;
    public bool WaitLifestream;
    public bool WaitNavmesh;
    public Vector3 Position;
    public uint MapId;
    public bool IgnoreMapCheck;
    public float ArrivalRadius = 1.5f;
    public bool UseCustomColor;
    public Vector4 Color = new(0.3f, 0.5f, 0.7f, 1);

    public RouteNode Copy() => new()
    {
        Kind = Kind, Enabled = Enabled, Collapsed = Collapsed, Name = Name,
        Choices = new(Choices), All = All, Children = Children.Select(x => x.Copy()).ToList(),
        Rows = Rows.Select(x => x.Copy()).ToList(), DelaySeconds = DelaySeconds,
        WaitLifestream = WaitLifestream, WaitNavmesh = WaitNavmesh,
        Position = Position, MapId = MapId, IgnoreMapCheck = IgnoreMapCheck, ArrivalRadius = ArrivalRadius,
        UseCustomColor = UseCustomColor, Color = Color,
    };
}

[Serializable]
public sealed class RoutePreset
{
    public Guid Id = Guid.NewGuid();
    public string Name = "Preset";
    public bool IsDefault;
    public List<RouteNode> Nodes = new();
    public RoutePreset Copy(string name) => new() { Name = name, Nodes = Nodes.Select(n => n.Copy()).ToList() };
}

[Serializable]
public sealed class TradeIncident
{
    public DateTime AtUtc;
    public string Player = "Unknown";
    public string HomeWorld = "Unknown";
    public string CurrentWorld = "Unknown";
    public string City = "Unknown";
}

[Serializable]
public sealed class WorldProgress
{
    public Guid PresetId;
    public Guid NodeId;
    public string World = "";
    public string DataCenter = "";
    public WorldResult Result;
    public DateTime? FirstAttemptUtc;
    public int Attempts;
    public string Detail = "";
    public int ActionIndex;
    public int RowIndex;
    public bool ArrivalConfirmed;
    public string PendingTravel = "";
    public string PendingTarget = "";
}

[Serializable]
public sealed class RouteEvent
{
    public DateTime AtUtc;
    public Guid PresetId;
    public string CurrentWorld = "";
    public string TargetWorld = "";
    public string DataCenter = "";
    public string Message = "";
}

[Serializable]
public sealed class Config : IPluginConfiguration
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public List<RoutePreset> Presets = new();
    public Guid ActivePresetId;
    public bool AllowCrossRegion;
    public List<TradeIncident> TradeIncidents = new();
    public List<WorldProgress> Progress = new();
    public List<RouteEvent> EventLog = new();
    public Guid? AutoResumePresetId;
    public Guid GilPresetId;
    public long? GilStart;
    public long? GilLast;


    public static Config Load()
    {
        var path = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "ShoutRunner2.json");
        Config config;
        if (File.Exists(path))
        {
            try { config = FromJson(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Cannot read {path}; original file was preserved.");
                throw;
            }
        }
        else config = CreateFresh();
        if (config.Version < CurrentVersion)
        {
            config = CreateFresh();
            config.Save();
            return config;
        }
        config.Presets ??= new();
        config.TradeIncidents ??= new();
        config.Progress ??= new();
        config.EventLog ??= new();
        if (!config.Presets.Any(p => p.IsDefault))
        {
            config.Presets.Add(CreateDefaultPreset());
        }
        if (config.Presets.All(p => p.Id != config.ActivePresetId))
            config.ActivePresetId = config.Presets[0].Id;
        return config;
    }

    public static Config CreateFresh()
    {
        var config = new Config();
        var preset = CreateDefaultPreset();
        config.Presets.Add(preset);
        config.ActivePresetId = preset.Id;
        return config;
    }

    private static RoutePreset CreateDefaultPreset()
    {
        var preset = new RoutePreset { Name = "Default", IsDefault = true };
        var phys = new RouteNode { Kind = NodeKind.Phys, Choices = new() { "Europe" } };
        var log = new RouteNode { Kind = NodeKind.Log, All = true };
        var world = new RouteNode { Kind = NodeKind.World, All = true };
        var command = new RouteNode { Kind = NodeKind.Cmd,
            Rows = new() { new TextRow { Text = "/echo Hallo", DelaySeconds = 0 } } };
        world.Children.Add(command);
        log.Children.Add(world);
        phys.Children.Add(log);
        preset.Nodes.Add(phys);
        return preset;
    }

    public RoutePreset Active => Presets.First(p => p.Id == ActivePresetId);
    public void Save()
    {
        var directory = Plugin.PluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ShoutRunner2.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, true);
    }
    public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
    public static Config FromJson(string json)
    {
        var config = JsonConvert.DeserializeObject<Config>(json) ?? new Config();
        foreach (var preset in config.Presets ?? new List<RoutePreset>())
            NormalizeNodes(preset.Nodes ?? new List<RouteNode>(), new HashSet<Guid>());
        return config;
    }

    private static void NormalizeNodes(List<RouteNode> nodes, HashSet<Guid> seen)
    {
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var node = nodes[i];
            if (node.Id == Guid.Empty || !seen.Add(node.Id)) nodes.RemoveAt(i);
        }
        foreach (var node in nodes)
        {
            node.Choices = (node.Choices ?? new List<string>())
                .Where(choice => !string.IsNullOrWhiteSpace(choice))
                .Select(choice => choice.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            node.Children ??= new();
            NormalizeNodes(node.Children, seen);
        }
    }
}
