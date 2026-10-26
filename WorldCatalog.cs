using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace ShoutRunner;

public sealed record WorldEntry(string Name, string DataCenter, byte Region);

public static class WorldCatalog
{
    public const string OtherRegion = "Other";

    public static readonly IReadOnlyDictionary<string, string> DcToRegion =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Chaos"] = "Europe",
            ["Light"] = "Europe",
            ["Materia"] = "Oceania",
            ["Aether"] = "North America",
            ["Crystal"] = "North America",
            ["Dynamis"] = "North America",
            ["Primal"] = "North America",
            ["Elemental"] = "Japan",
            ["Gaia"] = "Japan",
            ["Mana"] = "Japan",
            ["Meteor"] = "Japan",
        };

    public static readonly string[] RegionOrder =
        ["North America", "Europe", "Japan", "Oceania"];

    // Public Worlds listed by the Lodestone. The game sheet is merged on load so
    // new Worlds still appear without requiring a plugin update.
    private static readonly (string DataCenter, byte Region, string Worlds)[] OfficialWorlds =
    [
        ("Aether", 2, "Adamantoise Cactuar Faerie Gilgamesh Jenova Midgardsormr Sargatanas Siren"),
        ("Crystal", 2, "Balmung Brynhildr Coeurl Diabolos Goblin Malboro Mateus Zalera"),
        ("Dynamis", 2, "Cuchulainn Golem Halicarnassus Kraken Maduin Marilith Rafflesia Seraph"),
        ("Primal", 2, "Behemoth Excalibur Exodus Famfrit Hyperion Lamia Leviathan Ultros"),
        ("Chaos", 3, "Cerberus Louisoix Moogle Omega Phantom Ragnarok Sagittarius Spriggan"),
        ("Light", 3, "Alpha Lich Odin Phoenix Raiden Shiva Twintania Zodiark"),
        ("Materia", 4, "Bismarck Ravana Sephirot Sophia Zurvan"),
        ("Elemental", 1, "Aegis Atomos Carbuncle Garuda Gungnir Kujata Tonberry Typhon"),
        ("Gaia", 1, "Alexander Bahamut Durandal Fenrir Ifrit Ridill Tiamat Ultima"),
        ("Mana", 1, "Anima Asura Chocobo Hades Ixion Masamune Pandaemonium Titan"),
        ("Meteor", 1, "Belias Mandragora Ramuh Shinryu Unicorn Valefor Yojimbo Zeromus"),
    ];

    private static readonly List<WorldEntry> Entries = BuildOfficialCatalog();

    private static List<WorldEntry> BuildOfficialCatalog() =>
        OfficialWorlds.SelectMany(group => group.Worlds.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => new WorldEntry(name, group.DataCenter, group.Region)))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static void Load()
    {
        var byName = BuildOfficialCatalog().ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<World>();
            foreach (var row in sheet)
            {
                if (!row.IsPublic) continue;

                // Avoid test servers
                if (row.Name.ToString() == "Cloudtest01") continue;
                if (row.Name.ToString() == "Cloudtest02") continue;

                var name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                name = Plugin.PluginInterface.Sanitizer.Sanitize(name);

                var dc = row.DataCenter.ValueNullable?.Name.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(dc))
                    dc = Plugin.PluginInterface.Sanitizer.Sanitize(dc);
                else
                    dc = "?";

                // Keep the bundled Lodestone assignment for known Worlds. The
                // sheet only contributes newly added public Worlds.
                var knownRegion = OfficialWorlds.FirstOrDefault(group =>
                    group.DataCenter.Equals(dc, StringComparison.OrdinalIgnoreCase)).Region;
                var region = knownRegion != 0 ? knownRegion : row.Region;
                if (region is >= 1 and <= 4 && dc != "?")
                    byName.TryAdd(name, new WorldEntry(name, dc, region));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to load World sheet.");
        }
        Entries.Clear();
        Entries.AddRange(byName.Values.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<WorldEntry> All() => Entries;

    public static IEnumerable<byte> Regions() =>
        Entries.Select(e => e.Region).Distinct().OrderBy(r => r);

    public static IEnumerable<IGrouping<string, WorldEntry>> DataCentersInRegion(byte region) =>
        Entries.Where(e => e.Region == region)
               .GroupBy(e => e.DataCenter)
               .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    public static bool Exists(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && Entries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static string RegionFor(string dc) => DcToRegion.GetValueOrDefault(dc, OtherRegion);

    public static IEnumerable<WorldEntry> WorldsInRegion(string region) =>
        Entries.Where(e => RegionLabel(e.Region) == region)
               .OrderBy(e => e.DataCenter, StringComparer.OrdinalIgnoreCase)
               .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    public static string RegionLabel(byte region) => region switch
    {
        1 => "Japan",
        2 => "North America",
        3 => "Europe",
        4 => "Oceania",
        _ => $"Region {region}",
    };
}
