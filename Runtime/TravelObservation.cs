using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace ShoutRunner.Runtime;

public static class TravelObservation
{
    private static readonly string[] LoadingAddons = ["_NowLoading", "FadeMiddle", "FadeBlack"];
    public static string World => Plugin.ObjectTable.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ToString() ?? "";
    public static string HomeWorld => Plugin.ObjectTable.LocalPlayer?.HomeWorld.ValueNullable?.Name.ToString() ?? "";
    public static uint MapId
    {
        get
        {
            var map = Plugin.ClientState.MapId;
            if (map != 0) return map;
            try { return Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRow(Plugin.ClientState.TerritoryType).Map.RowId; }
            catch { return 0; }
        }
    }
    public static Vector3? Position => Plugin.ObjectTable.LocalPlayer?.Position;
    public static string City
    {
        get
        {
            try
            {
                var row = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRow(Plugin.ClientState.TerritoryType);
                return row.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
            }
            catch { return "Unknown"; }
        }
    }
    public static bool Loading => !Plugin.PlayerState.IsLoaded
        || Plugin.Condition[ConditionFlag.BetweenAreas]
        || Plugin.Condition[ConditionFlag.BetweenAreas51]
        || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
        || OverlayVisible();
    private static bool OverlayVisible()
    {
        foreach (var name in LoadingAddons)
        {
            try { if (Plugin.GameGui.GetAddonByName(name, 1).IsVisible) return true; }
            catch { }
        }
        return false;
    }
    public static bool TeleportBusy => Loading || Plugin.Condition[ConditionFlag.Casting];
    public static bool InCity(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;
        try
        {
            // Territory names from a fixed language, independent of the client locale.
            var territory = Plugin.ClientState.TerritoryType;
            var englishTerritory = Plugin.DataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English)
                .GetRow(territory).PlaceName.ValueNullable?.Name.ToString() ?? "";
            if (AetheryteCatalog.TryGetCityId(destination, out var destinationId))
            {
                var aetheryte = Plugin.DataManager.GetExcelSheet<Aetheryte>().GetRow(destinationId);
                if (aetheryte.Territory.RowId == territory) return true;
            }
            // Some city territories have multiple maps (e.g. Ul'dah / Limsa).
            if (destination.StartsWith("Ul'dah", StringComparison.OrdinalIgnoreCase))
                return englishTerritory.Contains("Ul'dah", StringComparison.OrdinalIgnoreCase)
                    || englishTerritory.Contains("Steps of Nald", StringComparison.OrdinalIgnoreCase)
                    || englishTerritory.Contains("Steps of Thal", StringComparison.OrdinalIgnoreCase);
            if (destination.StartsWith("Limsa Lominsa", StringComparison.OrdinalIgnoreCase))
                return englishTerritory.Contains("Limsa Lominsa", StringComparison.OrdinalIgnoreCase);
            if (destination.Equals("Gridania", StringComparison.OrdinalIgnoreCase))
                return englishTerritory.Contains("Gridania", StringComparison.OrdinalIgnoreCase);
            return englishTerritory.Equals(destination, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to determine current city territory.");
            return false;
        }
    }
    public static string RegionOf(string world) => WorldCatalog.All().Where(x => x.Name.Equals(world, StringComparison.OrdinalIgnoreCase)).Select(x => WorldCatalog.RegionLabel(x.Region)).FirstOrDefault() ?? WorldCatalog.OtherRegion;
    public static string DcOf(string world) => WorldCatalog.All().Where(x => x.Name.Equals(world, StringComparison.OrdinalIgnoreCase)).Select(x => x.DataCenter).FirstOrDefault() ?? "";
}
