using System;
using System.Collections.Generic;
using System.Linq;

namespace ShoutRunner.Runtime;

public static class RouteScheduler
{
    public static WorldJob? ChooseNext(
        IEnumerable<WorldJob> jobs,
        Func<WorldJob, WorldProgress> progressOf,
        Func<WorldJob, bool> eligible,
        string preferredDc,
        string deferredDc)
    {
        var unfinished = jobs.ToList();
        if (unfinished.Count == 0) return null;
        var stage = unfinished.Min(j => j.Stage);
        var ready = unfinished.Where(j => j.Stage == stage && eligible(j)).ToList();
        if (ready.Count == 0) return null;
        var pending = ready.Where(j => progressOf(j).Result == WorldResult.Pending).ToList();
        return ready.FirstOrDefault(j => progressOf(j).Result is WorldResult.Running or WorldResult.Traveling)
            ?? pending.FirstOrDefault(j => j.DataCenter == preferredDc)
            ?? pending.FirstOrDefault(j => j.DataCenter == deferredDc)
            ?? pending.FirstOrDefault()
            ?? ready.FirstOrDefault(j => j.DataCenter != deferredDc)
            ?? ready.FirstOrDefault();
    }
}
