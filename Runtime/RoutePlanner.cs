using System;
using System.Collections.Generic;
using System.Linq;

namespace ShoutRunner.Runtime;

public sealed record RouteAction(RouteNode Node, string Target = "");
public sealed record WorldJob(Guid NodeId, int Stage, string Region, string DataCenter, string World, List<RouteAction> Actions);

public static class RoutePlanner
{
    public static List<WorldJob> Build(RoutePreset preset, IReadOnlyList<WorldEntry>? catalog = null)
    {
        catalog ??= WorldCatalog.All();
        var jobs = new List<WorldJob>();
        var stage = 0;
        foreach (var phys in preset.Nodes.Where(n => n.Enabled && n.Kind == NodeKind.Phys))
        {
            var region = phys.Choices.FirstOrDefault() ?? "";
            var regionWorlds = catalog.Where(w => WorldCatalog.RegionLabel(w.Region) == region).ToList();
            foreach (var log in phys.Children.Where(n => n.Enabled && n.Kind == NodeKind.Log))
            {
                var dcs = (log.All ? regionWorlds.Select(w => w.DataCenter) : log.Choices)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
                foreach (var dc in dcs)
                {
                    var dcWorlds = regionWorlds.Where(w => w.DataCenter.Equals(dc, StringComparison.OrdinalIgnoreCase))
                        .Select(w => w.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var worldNode in log.Children.Where(n => n.Enabled && n.Kind == NodeKind.World))
                    {
                        var targets = worldNode.All ? dcWorlds : worldNode.Choices.Where(w => dcWorlds.Contains(w, StringComparer.OrdinalIgnoreCase))
                            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
                        foreach (var world in targets.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            var actions = new List<RouteAction>();
                            foreach (var node in worldNode.Children.Where(n => n.Enabled))
                            {
                                if (node.Kind == NodeKind.Cmd)
                                {
                                    actions.Add(new RouteAction(node));
                                    continue;
                                }
                                if (node.Kind != NodeKind.City) continue;
                                var city = node;
                                foreach (var target in city.Choices.Distinct(StringComparer.OrdinalIgnoreCase)
                                             )
                                {
                                    actions.Add(new RouteAction(city, target));
                                    foreach (var child in city.Children.Where(n => n.Enabled))
                                        actions.Add(new RouteAction(child));
                                }
                            }
                            jobs.Add(new WorldJob(worldNode.Id, stage, region, dc, world, actions));
                        }
                    }
                }
            }
            stage++;
        }
        return jobs;
    }
}
