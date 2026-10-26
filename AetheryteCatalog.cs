using System;
using System.Collections.Generic;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace ShoutRunner;

public static class AetheryteCatalog
{
    private static readonly Dictionary<uint, string> Names = new(150);
    private static readonly Dictionary<uint, string> EnglishNames = new(150);
    private static readonly Dictionary<uint, string> GermanNames = new(150);
    private static readonly Dictionary<string, uint> AetheryteIds = new(StringComparer.OrdinalIgnoreCase);

    private static void Register(uint id, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var clean = Plugin.PluginInterface.Sanitizer.Sanitize(name).Trim();
        if (!AetheryteIds.ContainsKey(clean)) AetheryteIds.Add(clean, id);
    }

    // Logical city names may differ from the actual teleport destination.
    // Aliases are resolved against the localized game data, not hardcoded IDs.
    private static readonly Dictionary<string, string[]> CityAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Gridania"] = ["New Gridania", "Neu-Gridania", "Neu Gridania"],
        };

    public static bool TryGetCityId(string name, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var key = name.Trim();

        // First resolve an actual aetheryte name.
        if (AetheryteIds.TryGetValue(key, out id))
            return true;

        // Then resolve a logical route destination to a known aetheryte.
        if (CityAliases.TryGetValue(key, out var aliases))
        {
            foreach (var alias in aliases)
            {
                if (AetheryteIds.TryGetValue(alias, out id))
                    return true;
            }
        }

        return false;
    }
    public static readonly List<TeleportInfo> AvailableAetherytes = new(80);

    public static void Load()
    {
        Names.Clear(); EnglishNames.Clear(); GermanNames.Clear(); AetheryteIds.Clear();
        try
        {
            var localSheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
            foreach (var row in localSheet)
            {
                var name = row.PlaceName.ValueNullable?.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                Names[row.RowId] = Plugin.PluginInterface.Sanitizer.Sanitize(name);
                Register(row.RowId, name);
            }
            foreach (var row in Plugin.DataManager.GetExcelSheet<Aetheryte>(ClientLanguage.English))
            {
                var name = row.PlaceName.ValueNullable?.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                EnglishNames[row.RowId] = name;
                Register(row.RowId, name);
            }
            foreach (var row in Plugin.DataManager.GetExcelSheet<Aetheryte>(ClientLanguage.German))
            {
                var name = row.PlaceName.ValueNullable?.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                GermanNames[row.RowId] = name;
                Register(row.RowId, name);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to load localized aetheryte sheets.");
        }
    }

    public static unsafe bool UpdateAvailable()
    {
        if (Control.GetLocalPlayer() == null) { AvailableAetherytes.Clear(); return false; }
        try
        {
            var tp = Telepo.Instance();
            if (tp->UpdateAetheryteList() == null) { AvailableAetherytes.Clear(); return false; }
            AvailableAetherytes.Clear();
            for (long i = 0; i < tp->TeleportList.LongCount; i++)
                AvailableAetherytes.Add(tp->TeleportList[i]);
            return true;
        }
        catch (Exception ex)
        {
            AvailableAetherytes.Clear();
            Plugin.Log.Error(ex, "Failed to update available aetherytes.");
            return false;
        }
    }

    public static string GetName(TeleportInfo info) =>
        Names.TryGetValue(info.AetheryteId, out var n) ? n : $"Aetheryte#{info.AetheryteId}";

    public static string GetName(uint aetheryteId) =>
        Names.TryGetValue(aetheryteId, out var n) ? n : $"Aetheryte#{aetheryteId}";

    public static uint GetTerritory(TeleportInfo info)
    {
        try { return Plugin.DataManager.GetExcelSheet<Aetheryte>().GetRow(info.AetheryteId).Territory.RowId; }
        catch { return 0; }
    }

    public static IEnumerable<(uint id, string name)> AllKnown()
    {
        foreach (var kv in Names) yield return (kv.Key, kv.Value);
    }

    public static bool TryFindByName(string name, bool partial, out TeleportInfo info)
    {
        info = default;
        if (string.IsNullOrWhiteSpace(name) || !UpdateAvailable()) return false;
        var search = name.Trim();
        if (TryGetCityId(search, out var id))
        {
            foreach (var tp in AvailableAetherytes)
                if (tp.AetheryteId == id) { info = tp; return true; }
            return false; // Known city, but its aetheryte is not unlocked.
        }
        foreach (var tp in AvailableAetherytes)
            if (GetName(tp).Equals(search, StringComparison.OrdinalIgnoreCase))
            { info = tp; return true; }
        if (!partial) return false;
        foreach (var tp in AvailableAetherytes)
            if (GetName(tp).Contains(search, StringComparison.OrdinalIgnoreCase))
            { info = tp; return true; }
        return false;
    }
}
