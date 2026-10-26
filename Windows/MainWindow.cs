using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using ShoutRunner.Runtime;

namespace ShoutRunner.Windows;

public sealed class MainWindow : Window
{
    private static readonly NodeKind[] Kinds = Enum.GetValues<NodeKind>();
    private static readonly string[] Labels = ["Phys", "Log", "World", "City", "Speak", "Wait", "Goto", "Home", "Cmd"];
    private static readonly (string Label, string Region)[] RegionChoices =
        [("EU", "Europe"), ("NA", "North America"), ("OCE", "Oceania"), ("JP", "Japan")];
    private static readonly string[] Cities =
        [.. new[] { "Gridania", "Ul'dah - Steps of Nald", "Limsa Lominsa Lower Decks", "Foundation", "Mor Dhona", "Idyllshire", "Rhalgr's Reach", "Kugane", "The Crystarium", "Eulmore", "Old Sharlayan", "Radz-at-Han", "Tuliyollal", "Solution Nine" }
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    private readonly Config config;
    private readonly RouteExecutor executor;
    private readonly LifestreamBridge lifestream;
    private readonly NavmeshBridge navmesh;
    private readonly TradeInterceptor trades;
    private readonly HashSet<Guid> selected = new();
    private List<RouteNode>? selectedSiblings;
    private RouteNode? selectedNode;
    private int selectedDepth = -1;
    private string newPresetName = "New Preset";
    private string renamePresetName = "";
    private Guid renamePresetId = Guid.Empty;
    private bool confirmPresetDelete;

    public MainWindow(Config config, RouteExecutor executor, LifestreamBridge lifestream, NavmeshBridge navmesh, TradeInterceptor trades)
        : base("ShoutRunner##main")
    {
        this.config = config; this.executor = executor; this.lifestream = lifestream;
        this.navmesh = navmesh; this.trades = trades;
        Size = new Vector2(1040, 700); SizeCondition = ImGuiCond.FirstUseEver;
    }
    public void Open() => IsOpen = true;
    public Action? ToggleStatusWindow;
    public Func<bool>? StatusWindowOpen;
    public override void Draw()
    {
        ImGui.TextUnformatted($"Status: {executor.Status}");
        if (executor.CurrentJob is { } job)
        { ImGui.SameLine(); ImGui.TextDisabled($"{job.Region} / {job.DataCenter} / {job.World}"); }
        if (ImGui.BeginTabBar("##tabs"))
        {
            if (ImGui.BeginTabItem("Route")) { DrawRoute(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Preview")) { DrawPreview(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Progress")) { DrawProgress(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Trade interceptor")) { DrawTrades(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Log")) { DrawLog(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Settings")) { DrawSettings(); ImGui.EndTabItem(); }
            var toggleLabel = StatusWindowOpen?.Invoke() == true ? "Close Status Window" : "Open Status Window";
            if (ImGui.TabItemButton(toggleLabel, ImGuiTabItemFlags.Trailing)) ToggleStatusWindow?.Invoke();
            ImGui.EndTabBar();
        }
    }
    private void DrawRoute()
    {
        if (executor.IsRunning) ImGui.BeginDisabled();
        DrawPresets();
        if (executor.IsRunning) ImGui.EndDisabled();
        ImGui.Separator();
        if (!executor.IsRunning)
        {
            if (ImGui.Button("Run")) executor.Start(config.Active);
            Tooltip("Start this preset.");
        }
        else
        {
            if (!executor.CanPause) ImGui.BeginDisabled();
            if (ImGui.Button(executor.IsPaused ? "Resume" : "Pause")) executor.TogglePause();
            Tooltip("Pause or resume.");
            if (!executor.CanPause) ImGui.EndDisabled();
            ImGui.SameLine(); if (ImGui.Button("Stop")) executor.Stop();
            Tooltip("Stop the route.");
        }
        ImGui.SameLine();
        var count = RoutePlanner.Build(config.Active).Count;
        ImGui.TextDisabled($"{count} world jobs");
        ImGui.Separator();
        if (executor.IsRunning) ImGui.BeginDisabled();
        DrawBulkToolbar();
        DrawAddBar();
        ImGui.Separator();
        ImGui.BeginChild("##nodes", new Vector2(0, 0), false);
        DrawNodes(config.Active.Nodes, 0, "", null);
        ImGui.EndChild();
        if (executor.IsRunning) ImGui.EndDisabled();
    }
    private bool PresetNameExists(string name, Guid? exceptId = null) =>
        config.Presets.Any(p => p.Id != exceptId && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void DrawPresets()
    {
        var active = config.Active;
        if (renamePresetId != active.Id)
        {
            renamePresetId = active.Id;
            renamePresetName = active.Name;
        }

        ImGui.TextUnformatted("Selected preset");
        ImGui.SetNextItemWidth(310);
        if (ImGui.BeginCombo("##presetSelection", active.Name))
        {
            foreach (var p in config.Presets)
                if (ImGui.Selectable(p.Name + "##" + p.Id, p.Id == config.ActivePresetId))
                { config.ActivePresetId = p.Id; ClearSelection(); config.Save(); }
            ImGui.EndCombo();
        }
        Tooltip("Select the route preset to edit.");
        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
        {
            var baseName = active.Name + " copy";
            var name = baseName;
            for (var i = 2; PresetNameExists(name); i++) name = baseName + " " + i;
            var copy = active.Copy(name);
            config.Presets.Add(copy); config.ActivePresetId = copy.Id;
            ClearSelection(); config.Save();
        }
        Tooltip("Create a copy of the selected preset.");
        ImGui.SameLine();
        if (active.IsDefault) ImGui.BeginDisabled();
        if (ImGui.Button("Delete"))
        { confirmPresetDelete = true; ImGui.OpenPopup("Delete preset?##shoutrunner"); }
        if (active.IsDefault) ImGui.EndDisabled();
        Tooltip("Delete the selected preset after confirmation.");

        if (ImGui.BeginPopupModal("Delete preset?##shoutrunner"))
        {
            ImGui.TextWrapped("Delete this preset and its saved progress and events? This cannot be undone.");
            if (ImGui.Button("Cancel")) { confirmPresetDelete = false; ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Delete permanently") && confirmPresetDelete && !config.Active.IsDefault)
            {
                var id = config.ActivePresetId;
                config.Presets.RemoveAll(p => p.Id == id);
                config.Progress.RemoveAll(p => p.PresetId == id);
                config.EventLog.RemoveAll(e => e.PresetId == id);
                if (config.AutoResumePresetId == id) config.AutoResumePresetId = null;
                config.ActivePresetId = config.Presets[0].Id;
                ClearSelection(); config.Save();
                confirmPresetDelete = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Rename selected preset");
        if (active.IsDefault) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(310);
        ImGui.InputText("##renamePreset", ref renamePresetName, 80);
        ImGui.SameLine();
        var rename = renamePresetName.Trim();
        var canRename = rename.Length > 0 && !rename.Equals(active.Name, StringComparison.Ordinal)
            && !PresetNameExists(rename, active.Id);
        if (!canRename) ImGui.BeginDisabled();
        if (ImGui.Button("Save name"))
        { active.Name = rename; config.Save(); }
        if (!canRename) ImGui.EndDisabled();
        if (active.IsDefault) ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextUnformatted("Create a new preset");
        ImGui.SetNextItemWidth(310);
        ImGui.InputText("##newPresetName", ref newPresetName, 80);
        ImGui.SameLine();
        var newName = newPresetName.Trim();
        var canCreate = newName.Length > 0 && !PresetNameExists(newName);
        if (!canCreate) ImGui.BeginDisabled();
        if (ImGui.Button("Create"))
        {
            var preset = new RoutePreset { Name = newName };
            config.Presets.Add(preset); config.ActivePresetId = preset.Id;
            ClearSelection(); config.Save();
        }
        if (!canCreate) ImGui.EndDisabled();
    }
    private void DrawBulkToolbar()
    {
        var refs = SelectedRefs();
        var valid = refs.Count > 0 && !executor.IsRunning;
        var sameParent = valid && refs.All(r => ReferenceEquals(r.Siblings, refs[0].Siblings));
        if (!valid) ImGui.BeginDisabled();
        if (!sameParent) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Up")) MoveSelected(-1);
        Tooltip("Move selected nodes up.");
        ImGui.SameLine(); if (ImGui.SmallButton("Down")) MoveSelected(1);
        Tooltip("Move selected nodes down.");
        if (!sameParent) ImGui.EndDisabled();
        ImGui.SameLine(); if (ImGui.SmallButton("Duplicate")) DuplicateSelected();
        Tooltip("Copy selected nodes.");
        ImGui.SameLine(); if (ImGui.SmallButton("Delete")) DeleteSelected();
        Tooltip("Delete selected nodes.");
        if (!valid) ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextDisabled($"{selected.Count} selected (same level)");
        if (!valid || selectedDepth <= 0) return;
        var parents = ParentListsAtDepth(selectedDepth).ToList();
        if (parents.Count <= 1) return;
        ImGui.SameLine(); ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Move to", "Choose parent..."))
        {
            for (var i = 0; i < parents.Count; i++)
                if (ImGui.Selectable(parents[i].Label + "##move" + i)) MoveSelectedTo(parents[i].Siblings);
            ImGui.EndCombo();
        }
    }
    private void MoveSelected(int direction)
    {
        var refs = SelectedRefs();
        if (refs.Count == 0) return;
        var list = refs[0].Siblings;
        if (direction < 0)
        {
            for (var i = 1; i < list.Count; i++)
                if (selected.Contains(list[i].Id) && !selected.Contains(list[i - 1].Id))
                    (list[i - 1], list[i]) = (list[i], list[i - 1]);
        }
        else
        {
            for (var i = list.Count - 2; i >= 0; i--)
                if (selected.Contains(list[i].Id) && !selected.Contains(list[i + 1].Id))
                    (list[i + 1], list[i]) = (list[i], list[i + 1]);
        }
        config.Save();
    }
    private void DuplicateSelected()
    {
        var refs = SelectedRefs();
        if (refs.Count == 0) return;
        var last = refs[^1];
        last.Siblings.InsertRange(last.Siblings.IndexOf(last.Node) + 1, refs.Select(r => r.Node.Copy()));
        ClearSelection(); config.Save();
    }
    private void DeleteSelected()
    {
        foreach (var r in SelectedRefs()) r.Siblings.Remove(r.Node);
        ClearSelection(); config.Save();
    }
    private void MoveSelectedTo(List<RouteNode> destination)
    {
        var refs = SelectedRefs();
        if (refs.Count == 0) return;
        var moving = refs.Select(r => r.Node).ToList();
        foreach (var r in refs) r.Siblings.Remove(r.Node);
        destination.AddRange(moving);
        selectedSiblings = destination;
        selectedNode = moving[^1];
        config.Save();
    }
    private void ClearSelection()
    {
        selected.Clear(); selectedSiblings = null; selectedNode = null; selectedDepth = -1;
    }
    private sealed record NodeRef(RouteNode Node, List<RouteNode> Siblings, int Depth);
    private List<NodeRef> SelectedRefs()
    {
        var result = new List<NodeRef>();
        void Visit(List<RouteNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                if (selected.Contains(node.Id)) result.Add(new NodeRef(node, nodes, depth));
                Visit(node.Children, depth + 1);
            }
        }
        Visit(config.Active.Nodes, 0);
        return result;
    }
    private IEnumerable<(List<RouteNode> Siblings, string Label)> ParentListsAtDepth(int depth)
    {
        var result = new List<(List<RouteNode>, string)>();
        var requiredParentKind = depth switch
        {
            1 => NodeKind.Phys, 2 => NodeKind.Log, 3 => NodeKind.World,
            4 => NodeKind.City, _ => (NodeKind?)null,
        };
        void Visit(List<RouteNode> nodes, int currentDepth, string path)
        {
            foreach (var node in nodes)
            {
                var label = path + Labels[(int)node.Kind] + " " + Summary(node);
                if (currentDepth + 1 == depth && node.Kind == requiredParentKind)
                    result.Add((node.Children, label));
                else if (currentDepth + 1 < depth) Visit(node.Children, currentDepth + 1, label + " / ");
            }
        }
        Visit(config.Active.Nodes, 0, "");
        return result;
    }
    private void DrawAddBar()
    {
        ImGui.TextUnformatted("Add node:"); ImGui.SameLine();
        var parent = selectedNode;
        var children = parent == null ? config.Active.Nodes :
            parent.Kind is NodeKind.Phys or NodeKind.Log or NodeKind.World or NodeKind.City ? parent.Children : selectedSiblings ?? config.Active.Nodes;
        var depth = parent == null ? 0 :
            parent.Kind is NodeKind.Phys or NodeKind.Log or NodeKind.World or NodeKind.City
                ? selectedDepth + 1 : selectedDepth;
        for (var i = 0; i < Kinds.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 2);
            var allowed = Allowed(Kinds[i], depth) && !executor.IsRunning;
            if (!allowed) ImGui.BeginDisabled();
            ColoredButton(Kinds[i], Labels[i] + "##add" + i, false, () =>
            {
                var node = NewNode(Kinds[i]); children.Add(node); config.Save();
                ClearSelection(); selected.Add(node.Id); selectedSiblings = children;
                selectedNode = node; selectedDepth = depth;
            });
            if (!allowed) ImGui.EndDisabled();
            Tooltip("Add " + Labels[i] + " node.");
        }
    }
    private static bool Allowed(NodeKind kind, int depth) => depth switch
    {
        0 => kind == NodeKind.Phys,
        1 => kind == NodeKind.Log,
        2 => kind == NodeKind.World,
        3 => kind is NodeKind.City or NodeKind.Cmd,
        4 => kind is NodeKind.Speak or NodeKind.Wait or NodeKind.Goto or NodeKind.Home or NodeKind.Cmd,
        _ => false,
    };
    private static RouteNode NewNode(NodeKind kind)
    {
        var node = new RouteNode { Kind = kind };
        if (kind == NodeKind.Phys) node.Choices.Add("Europe");
        if (kind is NodeKind.Log or NodeKind.World) node.All = true;
        if (kind == NodeKind.City) node.Choices.Add("Gridania");
        if (kind is NodeKind.Speak or NodeKind.Cmd) node.Rows.Add(new TextRow());
        return node;
    }
    private void DrawNodes(List<RouteNode> nodes, int depth, string region, IReadOnlyList<string>? selectedDcs)
    {
        foreach (var node in nodes.ToList())
        {
            ImGui.PushID(node.Id.ToString());
            ImGui.Indent(depth == 0 ? 0 : 22);
            var color = NodeColor(node);
            ImGui.Spacing();
            var headerMin = ImGui.GetCursorScreenPos();
            var headerHeight = ImGui.GetFrameHeight() + 6f;
            var headerMax = headerMin + new Vector2(ImGui.GetContentRegionAvail().X, headerHeight);
            var headerColor = new Vector4(color.X, color.Y, color.Z,
                node.Enabled ? Math.Clamp(color.W, .35f, 1f) : .35f);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(headerMin, headerMax, ImGui.GetColorU32(headerColor), 4f);
            if (selected.Contains(node.Id))
                drawList.AddRect(headerMin, headerMax, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), 4f);
            var brightness = color.X * .2126f + color.Y * .7152f + color.Z * .0722f;
            ImGui.PushStyleColor(ImGuiCol.Text,
                node.Enabled && brightness > .55f ? new Vector4(.08f, .08f, .08f, 1) : Vector4.One);
            ImGui.SetCursorScreenPos(headerMin + new Vector2(6f, 3f));
            var picked = selected.Contains(node.Id);
            if (ImGui.Checkbox("##select", ref picked))
            {
                if (picked && selectedDepth != depth) ClearSelection();
                if (picked)
                { selected.Add(node.Id); selectedSiblings = nodes; selectedNode = node; selectedDepth = depth; }
                else
                {
                    selected.Remove(node.Id);
                    if (selected.Count == 0) ClearSelection();
                    else
                    {
                        var last = SelectedRefs()[^1];
                        selectedNode = last.Node; selectedSiblings = last.Siblings;
                    }
                }
            }
            Tooltip("Select this node.");
            ImGui.SameLine();
            var enabled = node.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled)) { node.Enabled = enabled; config.Save(); }
            Tooltip("Enable this node.");
            ImGui.SameLine();
            var toggleMin = ImGui.GetCursorScreenPos();
            var toggleWidth = Math.Max(1f, headerMax.X - toggleMin.X - 6f);
            if (ImGui.InvisibleButton("##toggle-settings", new Vector2(toggleWidth, ImGui.GetFrameHeight())))
            { node.Collapsed = !node.Collapsed; config.Save(); }
            var indicator = node.Collapsed ? "> " : "v ";
            drawList.PushClipRect(toggleMin, headerMax, true);
            drawList.AddText(toggleMin + new Vector2(2f, 2f),
                ImGui.GetColorU32(node.Enabled && brightness > .55f
                    ? new Vector4(.08f, .08f, .08f, 1) : Vector4.One),
                indicator + Labels[(int)node.Kind] + "  " + Summary(node));
            drawList.PopClipRect();
            ImGui.PopStyleColor();
            ImGui.SetCursorScreenPos(new Vector2(headerMin.X, headerMax.Y + 3f));
            if (!node.Collapsed)
            {
                DrawKindBar(node, depth);
                DrawNodeFields(node, region, selectedDcs);
            }
            var nextRegion = node.Kind == NodeKind.Phys ? node.Choices.FirstOrDefault() ?? "" : region;
            var nextDcs = node.Kind == NodeKind.Log
                ? (node.All
                    ? WorldCatalog.WorldsInRegion(nextRegion).Select(w => w.DataCenter).Distinct().ToList()
                    : node.Choices.ToList())
                : selectedDcs;
            if (node.Children.Count > 0) DrawNodes(node.Children, depth + 1, nextRegion, nextDcs);
            ImGui.Unindent(depth == 0 ? 0 : 22);
            ImGui.PopID();
        }
    }
    private void DrawKindBar(RouteNode node, int depth)
    {
        for (var i = 0; i < Kinds.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 2);
            var kind = Kinds[i];
            var allowed = Allowed(kind, depth) && !executor.IsRunning
                && (kind == node.Kind || kind is NodeKind.Phys or NodeKind.Log or NodeKind.World or NodeKind.City
                    || node.Children.Count == 0);
            if (!allowed) ImGui.BeginDisabled();
            ColoredButton(kind, Labels[i] + "##kind", node.Kind == kind, () =>
            {
                if (node.Kind == kind) return;
                node.Kind = kind; node.Rows.Clear(); node.Choices.Clear(); node.All = false;
                if (kind is NodeKind.Speak or NodeKind.Cmd) node.Rows.Add(new TextRow());
                config.Save();
            }, node.Kind == kind && node.UseCustomColor ? node.Color : null);
            if (!allowed) ImGui.EndDisabled();
            Tooltip(!allowed && node.Children.Count > 0 && kind is NodeKind.Cmd or NodeKind.Speak or NodeKind.Wait or NodeKind.Goto or NodeKind.Home
                ? "Move child nodes first."
                : KindTooltip(kind));
        }
        ImGui.SameLine();
        var swatchWidth = ImGui.GetFrameHeight();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - swatchWidth));
        var color = NodeColor(node);
        ImGui.SetNextItemWidth(swatchWidth);
        if (ImGui.ColorEdit4("##node-color", ref color,
            ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel
            | ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoTooltip))
        {
            color.W = 1f;
            node.Color = color;
            node.UseCustomColor = Vector4.DistanceSquared(color, DefaultNodeColor(node.Kind)) > .00001f;
            config.Save();
        }
        Tooltip("Node color. Right-click to reset.");
        if (ImGui.BeginPopupContextItem("##node-color-reset"))
        {
            if (ImGui.MenuItem("Reset to type color"))
            {
                node.Color = DefaultNodeColor(node.Kind);
                node.UseCustomColor = false;
                config.Save();
            }
            ImGui.EndPopup();
        }
    }
    private void DrawNodeFields(RouteNode node, string region, IReadOnlyList<string>? selectedDcs)
    {
        switch (node.Kind)
        {
            case NodeKind.Phys: DrawRegionChoices(node); break;
            case NodeKind.Log:
                DrawDataCenterChoices(node, WorldCatalog.WorldsInRegion(region).Select(w => w.DataCenter)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
                break;
            case NodeKind.World:
                var worlds = WorldCatalog.WorldsInRegion(region)
                    .GroupBy(w => w.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                    .OrderBy(w => w.DataCenter, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
                DrawMultiChoice(node, worlds.Select(w => w.Name),
                    worlds.ToDictionary(w => w.Name, w => w.DataCenter, StringComparer.OrdinalIgnoreCase), selectedDcs);
                break;
            case NodeKind.City: DrawOrderedCities(node, Cities); break;
            case NodeKind.Speak or NodeKind.Cmd: DrawRows(node); break;
            case NodeKind.Wait:
                if (ImGui.InputFloat("Delay (s)", ref node.DelaySeconds, .5f)) { node.DelaySeconds = Math.Max(0, node.DelaySeconds); config.Save(); }
                Tooltip("Wait before checking conditions.");
                if (ImGui.Checkbox("Wait for Lifestream", ref node.WaitLifestream)) config.Save();
                Tooltip("Wait for Lifestream.");
                if (ImGui.Checkbox("Wait for vnavmesh", ref node.WaitNavmesh)) config.Save();
                Tooltip("Wait for vnavmesh.");
                break;
            case NodeKind.Goto:
                if (ImGui.Button("Capture current position") && TravelObservation.Position is { } pos)
                { node.Position = pos; node.MapId = TravelObservation.MapId; config.Save(); }
                Tooltip("Capture position and map.");
                ImGui.SameLine(); ImGui.TextDisabled($"Map {node.MapId}");
                if (ImGui.InputFloat3("XYZ", ref node.Position)) config.Save();
                if (ImGui.Checkbox("Ignore map check", ref node.IgnoreMapCheck)) config.Save();
                Tooltip("Skip map check. Five-minute limit.");
                if (ImGui.InputFloat("Arrival radius", ref node.ArrivalRadius, .1f)) { node.ArrivalRadius = Math.Max(.2f, node.ArrivalRadius); config.Save(); }
                break;
            case NodeKind.Home: ImGui.TextDisabled("Travel to home world; continue with next node on arrival."); break;
        }
    }
    private void DrawRegionChoices(RouteNode node)
    {
        var selectedChoice = node.Choices.FirstOrDefault() ?? "";
        ImGui.TextUnformatted("Physical region");
        ImGui.PushID("region-segments");
        for (var i = 0; i < RegionChoices.Length; i++)
        {
            var (label, region) = RegionChoices[i];
            if (DrawSegment(label, i, RegionChoices.Length,
                selectedChoice.Equals(region, StringComparison.OrdinalIgnoreCase), NodeColor(node),
                $"Select {region}."))
            {
                node.Choices.Clear(); node.Choices.Add(region); config.Save();
            }
        }
        ImGui.PopID();
    }
    private void DrawDataCenterChoices(RouteNode node, IReadOnlyList<string> options)
    {
        ImGui.TextUnformatted("Logical data centers");
        ImGui.PushID("dc-segments");
        if (DrawSegment("All", 0, options.Count + 1, node.All, NodeColor(node),
            "Select or clear all DCs."))
        {
            node.All = !node.All;
            node.Choices.Clear();
            config.Save();
        }
        for (var i = 0; i < options.Count; i++)
        {
            var dc = options[i];
            var selectedDc = node.All || node.Choices.Contains(dc, StringComparer.OrdinalIgnoreCase);
            if (!DrawSegment(dc, i + 1, options.Count + 1, selectedDc, NodeColor(node),
                    $"Toggle {dc}.")) continue;
            if (node.All)
            {
                node.All = false;
                node.Choices.Clear();
                node.Choices.AddRange(options);
            }
            if (selectedDc) node.Choices.RemoveAll(value => value.Equals(dc, StringComparison.OrdinalIgnoreCase));
            else node.Choices.Add(dc);
            config.Save();
        }
        ImGui.PopID();
        if (options.Count == 0) ImGui.TextDisabled("Choose a physical region to see its data centers.");
        var unavailable = node.Choices.Where(value => !options.Contains(value, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unavailable.Count == 0 || node.All) return;
        ImGui.TextColored(new Vector4(1, .65f, .2f, 1), $"Unavailable here: {string.Join(", ", unavailable)}");
        if (ImGui.SmallButton("Remove unavailable"))
        {
            node.Choices.RemoveAll(value => unavailable.Contains(value, StringComparer.OrdinalIgnoreCase));
            config.Save();
        }
        Tooltip("Remove unavailable DCs.");
    }
    private static bool DrawSegment(string label, int index, int count, bool selected, Vector4 accent, string help)
    {
        if (index > 0) ImGui.SameLine(0f, 0f);
        var style = ImGui.GetStyle();
        var height = ImGui.GetFrameHeight();
        var rounding = Math.Min(Math.Max(4f, style.FrameRounding), height * .5f);
        var textSize = ImGui.CalcTextSize(label);
        var size = new Vector2(textSize.X + style.FramePadding.X * 2f + 8f, height);
        var position = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##segment-" + index, size);
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        Tooltip(help);
        var color = selected ? accent : style.Colors[(int)(held ? ImGuiCol.ButtonActive
            : hovered ? ImGuiCol.ButtonHovered : ImGuiCol.Button)];
        if (selected && hovered) color = Vector4.Lerp(color, Vector4.One, held ? .10f : .06f);
        var maximum = position + size;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(position, maximum, ImGui.GetColorU32(color),
            index == 0 || index == count - 1 ? rounding : 0f);
        if (index == 0 && count > 1)
            drawList.AddRectFilled(new Vector2(maximum.X - rounding, position.Y), maximum, ImGui.GetColorU32(color));
        else if (index == count - 1 && count > 1)
            drawList.AddRectFilled(position, new Vector2(position.X + rounding, maximum.Y), ImGui.GetColorU32(color));
        if (index > 0)
            drawList.AddLine(position, new Vector2(position.X, maximum.Y),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, .3f)));
        var brightness = color.X * .2126f + color.Y * .7152f + color.Z * .0722f;
        var textColor = selected && brightness > .55f ? new Vector4(.08f, .08f, .08f, 1f) : Vector4.One;
        drawList.AddText(position + (size - textSize) * .5f, ImGui.GetColorU32(textColor), label);
        return clicked;
    }

    private void DrawOrderedCities(RouteNode node, IReadOnlyList<string> cities)
    {
        // Choices are ordered; their positions define execution order.
        // Do not sort this collection.
        var changed = false;

        if (ImGui.SmallButton("Select all##city"))
        {
            node.All = false;
            node.Choices.Clear();
            node.Choices.AddRange(cities);
            changed = true;
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Select none##city"))
        {
            node.All = false;
            node.Choices.Clear();
            changed = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{node.Choices.Count}/{cities.Count} selected");

        var unavailable = node.Choices
            .Where(x => !cities.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unavailable.Count > 0)
        {
            ImGui.TextColored(
                new Vector4(1f, .65f, .2f, 1f),
                $"Unavailable: {string.Join(", ", unavailable)}"
            );

            if (ImGui.SmallButton("Remove unavailable##city"))
            {
                node.Choices.RemoveAll(
                    x => unavailable.Contains(
                        x, StringComparer.OrdinalIgnoreCase
                    )
                );
                changed = true;
            }
        }

        if (ImGui.CollapsingHeader(
            "Choices##ordered-cities",
            ImGuiTreeNodeFlags.DefaultOpen))
        {
            var rowHeight = ImGui.GetFrameHeightWithSpacing();
            var rows = (cities.Count + 1) / 2;
            var height = Math.Min(
                300f,
                Math.Max(rowHeight * 2f, rows * rowHeight + 8f)
            );

            if (ImGui.BeginChild(
                "##ordered-city-list",
                new Vector2(0, height),
                true))
            {
                if (ImGui.BeginTable(
                    "##ordered-city-columns",
                    2,
                    ImGuiTableFlags.SizingStretchSame |
                    ImGuiTableFlags.NoSavedSettings))
                {
                    for (var i = 0; i < cities.Count; i++)
                    {
                        var city = cities[i];

                        if (i % 2 == 0)
                            ImGui.TableNextRow();

                        ImGui.TableSetColumnIndex(i % 2);
                        ImGui.PushID(i);

                        var index = node.Choices.FindIndex(
                            x => x.Equals(
                                city,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );

                        var selected = index >= 0;
                        var buttonLabel = selected
                            ? (index + 1).ToString()
                            : " ";

                        // Fixed-width number buttons.
                        if (selected)
                        {
                            ImGui.PushStyleColor(
                                ImGuiCol.Button,
                                new Vector4(.22f, .48f, .70f, 1f)
                            );
                        }

                        if (ImGui.Button(
                            buttonLabel + "##city-toggle",
                            new Vector2(32f, 0f)))
                        {
                            if (selected)
                            {
                                node.Choices.RemoveAt(index);
                            }
                            else
                            {
                                node.Choices.Add(city);
                            }

                            node.All = false;
                            changed = true;
                        }

                        if (selected)
                            ImGui.PopStyleColor();

                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip(
                                selected
                                    ? $"Remove {city} from route"
                                    : $"Append {city} to route"
                            );
                        }

                        ImGui.SameLine();
                        ImGui.TextUnformatted(city);

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.EndChild();
        }

        if (changed)
            config.Save();
    }

    private void DrawMultiChoice(RouteNode node, IEnumerable<string> options,
        IReadOnlyDictionary<string, string>? groups = null, IReadOnlyList<string>? activeDcs = null)
    {
        var values = options.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var supportsDynamicAll = node.Kind is NodeKind.Log or NodeKind.World;
        if (ImGui.SmallButton("Select all"))
        {
            node.All = supportsDynamicAll;
            node.Choices.Clear();
            if (!supportsDynamicAll) node.Choices.AddRange(values);
            config.Save();
        }
        Tooltip("Select all choices.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Select none"))
        {
            node.All = false;
            node.Choices.Clear();
            config.Save();
        }
        Tooltip("Clear all choices.");
        ImGui.SameLine();
        var selectedCount = node.All && supportsDynamicAll
            ? values.Count
            : values.Count(value => node.Choices.Contains(value, StringComparer.OrdinalIgnoreCase));
        ImGui.TextDisabled($"{selectedCount}/{values.Count} selected");

        var unavailable = node.Choices.Where(x => !values.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
        if (!node.All && unavailable.Count > 0)
        {
            ImGui.TextColored(new Vector4(1, .65f, .2f, 1),
                $"Unavailable here: {string.Join(", ", unavailable)}");
            if (ImGui.SmallButton("Remove unavailable"))
            {
                node.Choices.RemoveAll(x => unavailable.Contains(x, StringComparer.OrdinalIgnoreCase));
                config.Save();
            }
            Tooltip("Remove unavailable choices.");
        }

        var choicesOpen = ImGui.CollapsingHeader("Choices##checkbox-list", ImGuiTreeNodeFlags.DefaultOpen);
        Tooltip(groups == null ? "Show choices." : "Worlds grouped by DC.");
        if (!choicesOpen) return;
        var rowHeight = ImGui.GetFrameHeight() + 5f;
        var rows = groups == null ? values.Count : values.GroupBy(value => groups.GetValueOrDefault(value, "?"))
            .Sum(group => 1 + (group.Count() + 1) / 2);
        var height = Math.Min(300f, Math.Max(rowHeight * 2, rows * rowHeight + 8f));
        if (ImGui.BeginChild("##checkbox-list", new Vector2(0, height), true))
        {
            if (values.Count == 0) ImGui.TextDisabled("No choices available for this parent node.");
            if (groups == null)
            {
                foreach (var value in values)
                {
                    var rowTop = ImGui.GetCursorPosY();
                    DrawChoiceCheckbox(node, value, values, supportsDynamicAll, false);
                    ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), rowTop + rowHeight));
                }
            }
            else
            {
                foreach (var group in values.GroupBy(value => groups.GetValueOrDefault(value, "?")))
                {
                    var active = activeDcs == null || activeDcs.Contains(group.Key, StringComparer.OrdinalIgnoreCase);
                    ImGui.TextColored(active ? new Vector4(.6f, .85f, 1f, 1f) : new Vector4(.7f, .7f, .7f, 1f),
                        $"{group.Key} ({group.Count()})" + (active ? "" : " · not selected in Log"));
                    Tooltip(active ? "Selected DC." : "Select this DC in Log.");
                    var worlds = group.ToList();
                    if (ImGui.BeginTable("##world-columns-" + group.Key, 2,
                        ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
                    {
                        for (var i = 0; i < worlds.Count; i += 2)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            DrawChoiceCheckbox(node, worlds[i], values, supportsDynamicAll, true);
                            ImGui.Dummy(new Vector2(0, 5f));
                            if (i + 1 >= worlds.Count) continue;
                            ImGui.TableSetColumnIndex(1);
                            DrawChoiceCheckbox(node, worlds[i + 1], values, supportsDynamicAll, true);
                            ImGui.Dummy(new Vector2(0, 5f));
                        }
                        ImGui.EndTable();
                    }
                }
            }
        }
        ImGui.EndChild();
    }
    private void DrawChoiceCheckbox(RouteNode node, string value, IReadOnlyList<string> values,
        bool supportsDynamicAll, bool isWorld)
    {
        var yes = node.All && supportsDynamicAll || node.Choices.Contains(value, StringComparer.OrdinalIgnoreCase);
        if (ImGui.Checkbox(value, ref yes))
        {
            if (node.All && supportsDynamicAll)
            {
                node.All = false;
                node.Choices.Clear();
                node.Choices.AddRange(values);
            }
            if (yes) node.Choices.Add(value);
            else node.Choices.RemoveAll(choice => choice.Equals(value, StringComparison.OrdinalIgnoreCase));
            config.Save();
        }
        Tooltip(isWorld ? "Toggle this world." : "Toggle this choice.");
    }
    private static void Tooltip(string message)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 1f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(.10f, .11f, .14f, .98f));
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One);
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(message);
        ImGui.EndTooltip();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }
    private static string KindTooltip(NodeKind kind) => kind switch
    {
        NodeKind.Phys => "Physical region.",
        NodeKind.Log => "Logical data centers.",
        NodeKind.World => "World travel targets.",
        NodeKind.City => "City travel targets.",
        NodeKind.Speak => "Shout, Yell, or Say.",
        NodeKind.Wait => "Wait for conditions.",
        NodeKind.Goto => "Navigate to coordinates.",
        NodeKind.Home => "Return home.",
        _ => "Run /commands.",
    };
    private void DrawRows(RouteNode node)
    {
        foreach (var row in node.Rows.ToList())
        {
            ImGui.PushID(row.Id.ToString());
            if (ImGui.Checkbox("##on", ref row.Enabled)) config.Save();
            Tooltip("Enable this row.");
            ImGui.SameLine();
            var index = node.Rows.IndexOf(row);
            if (ImGui.SmallButton("Up") && index > 0) { (node.Rows[index - 1], node.Rows[index]) = (node.Rows[index], node.Rows[index - 1]); config.Save(); }
            Tooltip("Move up.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Down") && index < node.Rows.Count - 1) { (node.Rows[index + 1], node.Rows[index]) = (node.Rows[index], node.Rows[index + 1]); config.Save(); }
            Tooltip("Move down.");
            ImGui.SameLine();
            if (node.Kind == NodeKind.Speak)
            {
                foreach (var (letter, kind) in new[] { ("S", SpeechKind.Shout), ("Y", SpeechKind.Yell), ("T", SpeechKind.Say) })
                {
                    ColoredButton(NodeKind.Speak, letter + "##" + kind, row.Speech == kind, () => { row.Speech = kind; config.Save(); });
                    Tooltip(kind switch { SpeechKind.Shout => "Shout.", SpeechKind.Yell => "Yell.", _ => "Say." });
                    ImGui.SameLine(0, 2);
                }
            }
            var invalid = node.Kind == NodeKind.Cmd && !string.IsNullOrWhiteSpace(row.Text) && !row.Text.TrimStart().StartsWith('/');
            if (invalid) ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1, .15f, .15f, 1));
            ImGui.SetNextItemWidth(330);
            if (ImGui.InputText("##text", ref row.Text, 500)) config.Save();
            Tooltip(node.Kind == NodeKind.Cmd
                ? "Command must start with /."
                : "Chat message.");
            if (invalid)
                ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                    ImGui.GetColorU32(new Vector4(1, .1f, .1f, 1)));
            if (invalid) ImGui.PopStyleColor();
            ImGui.SameLine();
            var delayStep = node.Kind == NodeKind.Cmd ? 1f : .5f;
            if (ImGui.SmallButton("-")) { row.DelaySeconds = Math.Max(0, row.DelaySeconds - delayStep); config.Save(); }
            Tooltip("Shorter delay.");
            ImGui.SameLine(); ImGui.TextUnformatted($"{row.DelaySeconds:F1}s"); ImGui.SameLine();
            if (ImGui.SmallButton("+")) { row.DelaySeconds += delayStep; config.Save(); }
            Tooltip("Longer delay.");
            ImGui.SameLine();
            if (ImGui.SmallButton("D")) { node.Rows.Insert(node.Rows.IndexOf(row) + 1, row.Copy()); config.Save(); }
            Tooltip("Duplicate row.");
            ImGui.SameLine();
            if (ImGui.SmallButton("X")) { node.Rows.Remove(row); config.Save(); }
            Tooltip("Delete row.");
            ImGui.PopID();
        }
        if (ImGui.SmallButton("+ row")) { node.Rows.Add(new TextRow()); config.Save(); }
        Tooltip("Add row.");
    }
    private static string Summary(RouteNode node) => node.Kind switch
    {
        NodeKind.Phys => node.Choices.FirstOrDefault() ?? "Choose region",
        NodeKind.Log or NodeKind.World => node.All ? "All" : string.Join(", ", node.Choices),
        NodeKind.City => string.Join(", ", node.Choices),
        NodeKind.Speak or NodeKind.Cmd => $"{node.Rows.Count} rows",
        NodeKind.Wait => $"{node.DelaySeconds:F1}s",
        NodeKind.Goto => $"Map {node.MapId} / {node.Position}",
        _ => "Home world",
    };
    private static Vector4 NodeColor(RouteNode node) => node.UseCustomColor ? node.Color : DefaultNodeColor(node.Kind);
    private static Vector4 DefaultNodeColor(NodeKind kind) => kind switch
    {
        NodeKind.Phys => new(.60f, .35f, .75f, 1), NodeKind.Log => new(.32f, .48f, .82f, 1),
        NodeKind.World => new(.25f, .68f, .75f, 1), NodeKind.City => new(.27f, .70f, .45f, 1),
        NodeKind.Speak => new(.85f, .65f, .20f, 1), NodeKind.Wait => new(.70f, .60f, .30f, 1),
        NodeKind.Goto => new(.35f, .75f, .50f, 1), NodeKind.Home => new(.70f, .40f, .65f, 1),
        _ => new(.60f, .60f, .60f, 1),
    };
    private static void ColoredButton(NodeKind kind, string label, bool active, Action clicked, Vector4? custom = null)
    {
        var color = custom ?? DefaultNodeColor(kind);
        if (!active) color *= .65f;
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        if (ImGui.SmallButton(label)) clicked();
        ImGui.PopStyleColor();
    }
    private void DrawProgress()
    {
        var selectedJobs = RoutePlanner.Build(config.Active);
        var items = config.Progress.Where(p => p.PresetId == config.ActivePresetId
            && selectedJobs.Any(j => j.NodeId == p.NodeId && j.World.Equals(p.World, StringComparison.OrdinalIgnoreCase))).ToList();
        var done = items.Count(p => p.Result is WorldResult.Complete or WorldResult.Inaccessible);
        ImGui.TextUnformatted($"{done}/{items.Count} worlds finished");
        if (!executor.IsRunning && ImGui.Button("Reset preset progress")) executor.ResetProgress(config.ActivePresetId);
        ImGui.ProgressBar(executor.OverallCompletion(config.ActivePresetId), new Vector2(-1, 0));
        if (ImGui.BeginTable("##progress", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            foreach (var h in new[] { "DC", "World", "State", "Attempts", "Detail", "Actions" }) ImGui.TableSetupColumn(h);
            ImGui.TableHeadersRow();
            foreach (var p in items)
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(p.DataCenter);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(p.World);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(p.Result.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(p.Attempts.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(p.Detail);
                ImGui.TableNextColumn();
                var job = selectedJobs.FirstOrDefault(j => j.NodeId == p.NodeId && j.World.Equals(p.World, StringComparison.OrdinalIgnoreCase));
                if (job != null)
                {
                    var fraction = RouteExecutor.JobCompletion(job, p);
                    ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{fraction * 100:0}%");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted($"Arrival + {job.Actions.Count} route actions");
                        for (var i = 0; i < job.Actions.Count; i++)
                            ImGui.TextUnformatted($"{(i < p.ActionIndex ? "[x]" : "[ ]")} {job.Actions[i].Node.Kind} {job.Actions[i].Target}");
                        ImGui.EndTooltip();
                    }
                }
            }
            ImGui.EndTable();
        }
    }
    private void DrawPreview()
    {
        var jobs = RoutePlanner.Build(config.Active);
        ImGui.TextUnformatted($"Expanded route: {jobs.Count} worlds. Selections run alphabetically within each node.");
        ImGui.BeginChild("##preview", new Vector2(0, 0), false);
        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            ImGui.PushID(i);
            if (ImGui.TreeNode($"{i + 1}. {job.Region} / {job.DataCenter} / {job.World} ({job.Actions.Count} actions)"))
            {
                foreach (var action in job.Actions)
                {
                    var detail = action.Node.Kind switch
                    {
                        NodeKind.City => action.Target,
                        NodeKind.Speak or NodeKind.Cmd => $"{action.Node.Rows.Count(r => r.Enabled)} rows",
                        NodeKind.Wait => $"{action.Node.DelaySeconds:F1}s",
                        NodeKind.Goto => $"Map {action.Node.MapId} / {action.Node.Position}",
                        _ => "",
                    };
                    ImGui.BulletText($"{action.Node.Kind}: {detail}");
                }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        ImGui.EndChild();
    }
    private void DrawTrades()
    {
        ImGui.TextUnformatted($"Cancel mode: {(trades.CancelMode ? "active" : "idle")}");
        if (ImGui.BeginTable("##trades", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            foreach (var h in new[] { "Time", "Player", "Home world", "Current world", "City" }) ImGui.TableSetupColumn(h);
            ImGui.TableHeadersRow();
            foreach (var x in config.TradeIncidents.AsEnumerable().Reverse())
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(x.AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(x.Player);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(x.HomeWorld);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(x.CurrentWorld);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(x.City);
            }
            ImGui.EndTable();
        }
    }
    private void DrawLog()
    {
        foreach (var item in executor.Events.Where(e => e.PresetId == config.ActivePresetId).Reverse())
            ImGui.TextUnformatted($"{item.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  [{item.CurrentWorld} -> {item.TargetWorld}] {item.Message}");
    }
    private void DrawSettings()
    {
        if (ImGui.Checkbox("Allow travel to Oceania from other physical regions", ref config.AllowCrossRegion)) config.Save();
        ImGui.TextDisabled("Other cross-region directions remain unavailable.");
        ImGui.TextUnformatted($"Lifestream: {(lifestream.Available ? "loaded" : "missing")}");
        ImGui.TextUnformatted($"vnavmesh: {(navmesh.Available ? "loaded" : "missing")}");
    }
}
