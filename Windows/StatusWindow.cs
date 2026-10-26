using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ShoutRunner.Runtime;

namespace ShoutRunner.Windows;

public sealed class StatusWindow : Window
{
    private readonly Config config;
    private readonly RouteExecutor executor;
    private bool confirmRestart;
    public StatusWindow(Config config, RouteExecutor executor) : base("ShoutRunner Status##status")
    {
        this.config = config;
        this.executor = executor;
        Size = new Vector2(320, 158);
        SizeCondition = ImGuiCond.Always;
        Flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse;
        IsOpen = false;
    }

    private static string FormatGil(long? amount)
    {
        if (!amount.HasValue) return "---,---,---";
        var digits = Math.Min(Math.Abs((decimal)amount.Value), 999999999m).ToString("000000000");
        var result = digits.Insert(6, ",").Insert(3, ",").ToCharArray();
        for (var i = 0; i < result.Length; i++)
        {
            if (result[i] == ',') continue;
            if (result[i] != '0') break;
            result[i] = '-';
        }
        if (amount.Value < 0)
        {
            // Use the rightmost padding dash as a minus sign; display stays 11 chars.
            // The remaining dashes are placeholders.
        }
        return new string(result);
    }

    public override void Draw()
    {
        ImGui.ProgressBar(executor.OverallCompletion(config.ActivePresetId), new Vector2(-1, 0));
        var action = executor.MiniAction;
        if (action.StartsWith("Travel to: ", StringComparison.Ordinal))
            action = action[11..];
        ImGui.TextUnformatted("Travel to:");
        ImGui.SameLine();
        ImGui.TextDisabled(action.Length > 27 ? action[..24] + "..." : action);
        ImGui.TextUnformatted("Cost:");
        ImGui.SameLine();
        // Use a strict eleven-character padded value; active run uses net Gil change.
        var gilText = FormatGil(executor.GilDelta) + " gil";
        using (Plugin.PluginInterface.UiBuilder.MonoFontHandle.Push())
        {
            var room = ImGui.GetContentRegionAvail().X;
            var textWidth = ImGui.CalcTextSize(gilText).X;
            if (room > textWidth) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + room - textWidth);
            ImGui.TextUnformatted(gilText);
        }
        ImGui.Separator();
        if (executor.IsRunning)
        {
            if (ImGui.Button("Stop")) executor.Stop();
            ImGui.SameLine();
            var pauseLabel = executor.IsPaused ? "Resume" : executor.PausePending ? "Cancel pause" : "Pause";
            if (ImGui.Button(pauseLabel)) executor.TogglePause();
            ImGui.SameLine();
            DrawRestart();
        }
        else if (!executor.RestartPending)
        {
            if (ImGui.Button("Start")) executor.Start(config.Active);
            if (config.Progress.Any(x => x.PresetId == config.ActivePresetId))
            {
                ImGui.SameLine();
                if (ImGui.Button("Reset")) executor.ResetProgress(config.ActivePresetId);
            }
        }
        else ImGui.TextDisabled("Restarting...");
    }

    private void DrawRestart()
    {
        if (ImGui.Button(confirmRestart ? "Really?" : "Restart"))
        {
            if (confirmRestart) { confirmRestart = false; executor.RequestRestart(config.ActivePresetId); }
            else confirmRestart = true;
        }
        // Reset confirmation as soon as pointer leaves this same button.
        if (!ImGui.IsItemHovered()) confirmRestart = false;
    }
}
