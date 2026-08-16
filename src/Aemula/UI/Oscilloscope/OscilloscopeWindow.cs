using System;
using System.Collections.Generic;
using System.Numerics;
using Aemula.Debugging;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;

namespace Aemula.UI.Oscilloscope;

/// <summary>
/// Logic-analyzer-style debugger window: one merged tree/waveform view, channel
/// name to the left of its own row's trace, grouped under collapsible headers
/// (expanded by default). All channels are always recorded and shown - no
/// per-channel hide toggle. Digital channels only for now - see
/// docs/oscilloscope-plan.md for the phased plan (bus/hex-band rendering,
/// timescale controls, etc. come later).
/// </summary>
public sealed class OscilloscopeWindow : DebuggerWindow
{
    private static readonly string[] DigitalTickLabels = ["L", "H"];

    private readonly Debugger _debugger;
    private readonly ScopeChannelNode _root;
    private readonly ScopeRecorder _recorder;
    private readonly Dictionary<ScopeChannel, int> _channelIndex;

    public override string DisplayName => "Oscilloscope";

    public override Pane PreferredPane => Pane.Bottom;

    public OscilloscopeWindow(Debugger debugger, ScopeChannelNode channels)
    {
        _debugger = debugger;
        _root = channels;
        _recorder = new ScopeRecorder(channels);

        _channelIndex = new Dictionary<ScopeChannel, int>();
        for (var i = 0; i < _recorder.Channels.Length; i++)
        {
            _channelIndex[_recorder.Channels[i]] = i;
        }

        _debugger.Ticked += OnTicked;
    }

    private void OnTicked()
    {
        if (!IsOpen)
        {
            return;
        }

        _recorder.Sample();
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        var labelColumnWidth = ImGui.GetFontSize() * 10f;

        // One sample = one pixel, fixed zoom for now - see phase 3 in the plan for
        // proper zoom/pan. Since new samples always land at the right edge (index
        // visibleCount - 1) and we recompute this range every frame, the view is
        // right-anchored to "now" while running and holds still for free once the
        // debugger stops (no more ticks means no more samples to show).
        var availableSamples = (int)Math.Min(_recorder.TotalSamples, _recorder.Capacity);
        var plotWidth = (int)MathF.Max(1f, ImGui.GetContentRegionAvail().X - labelColumnWidth);
        var visibleCount = Math.Min(availableSamples, plotWidth);

        Span<double> xs = visibleCount <= 4096 ? stackalloc double[visibleCount] : new double[visibleCount];
        for (var i = 0; i < visibleCount; i++)
        {
            xs[i] = i;
        }

        if (!ImGui.BeginTable(
            "##oscilloscope_table"u8,
            2,
            ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
            ImGui.GetContentRegionAvail()))
        {
            return;
        }

        ImGui.TableSetupColumn("Channel"u8, ImGuiTableColumnFlags.WidthFixed, labelColumnWidth);
        ImGui.TableSetupColumn("Waveform"u8, ImGuiTableColumnFlags.WidthStretch);

        DrawChannelNode(_root, visibleCount, xs);

        ImGui.EndTable();
    }

    private void DrawChannelNode(ScopeChannelNode node, int visibleCount, ReadOnlySpan<double> xs)
    {
        switch (node)
        {
            case ScopeChannel channel:
                DrawChannelRow(channel, visibleCount, xs);
                break;

            case ScopeChannelGroup group:
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.TreeNodeEx(group.Name, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAllColumns))
                {
                    foreach (var child in group.Children)
                    {
                        DrawChannelNode(child, visibleCount, xs);
                    }

                    ImGui.TreePop();
                }
                break;
        }
    }

    private unsafe void DrawChannelRow(ScopeChannel channel, int visibleCount, ReadOnlySpan<double> xs)
    {
        var rowHeight = ImGui.GetTextLineHeightWithSpacing() * 3.5f;

        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(channel.Name);

        ImGui.TableNextColumn();

        var isDigital = channel.Kind == ScopeChannelKind.Digital;

        Span<double> ys = visibleCount <= 4096 ? stackalloc double[visibleCount] : new double[visibleCount];
        if (visibleCount > 0)
        {
            FillVisibleSamples(channel, visibleCount, ys);
        }

        if (!ImPlot.BeginPlot(
            $"##{channel.Name}",
            new Vector2(-1, rowHeight),
            ImPlotFlags.NoLegend | ImPlotFlags.NoMenus | ImPlotFlags.NoMouseText))
        {
            return;
        }

        ImPlot.SetupAxes(
            ""u8,
            ""u8,
            ImPlotAxisFlags.NoTickLabels | ImPlotAxisFlags.NoGridLines,
            isDigital ? ImPlotAxisFlags.NoGridLines : ImPlotAxisFlags.NoGridLines | ImPlotAxisFlags.NoTickLabels);
        ImPlot.SetupAxesLimits(0, Math.Max(visibleCount - 1, 1), -0.1, 1.1, ImPlotCond.Always);

        if (isDigital)
        {
            Span<double> digitalTicks = stackalloc double[] { 0.0, 1.0 };
            fixed (double* digitalTicksPtr = digitalTicks)
            {
                ImPlot.SetupAxisTicks(ImAxis.Y1, digitalTicksPtr, 2, DigitalTickLabels);
            }
        }

        if (visibleCount > 0)
        {
            if (isDigital)
            {
                DrawDigitalTrace(channel, visibleCount, xs, ys);
            }
            else
            {
                DrawBusTrace(channel, visibleCount, ys);
            }
        }

        ImPlot.EndPlot();
    }

    private static unsafe void DrawDigitalTrace(ScopeChannel channel, int visibleCount, ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        fixed (double* xsPtr = xs)
        fixed (double* ysPtr = ys)
        {
            ImPlot.PlotStairs("##data"u8, xsPtr, ysPtr, visibleCount);
        }

        if (ImPlot.IsPlotHovered())
        {
            var mouse = ImPlot.GetPlotMousePos();
            var index = (int)Math.Round(mouse.X);
            if (index >= 0 && index < visibleCount)
            {
                var value = ys[index];
                ImGui.SetTooltip($"{channel.Name}: {(value != 0 ? "H" : "L")}");
            }
        }
    }

    // Hex-banded bus rendering: one filled/outlined rectangle per run of equal
    // samples, so edges land exactly at value-change points, with the hex value
    // centered in the rectangle when there's room for it. ImPlot has no built-in
    // "bus" mark, so this draws directly into plot pixel space via
    // GetPlotDrawList()/PlotToPixels() - see "Open risks" in the plan doc.
    private static void DrawBusTrace(ScopeChannel channel, int visibleCount, ReadOnlySpan<double> ys)
    {
        const double BandTop = 0.85;
        const double BandBottom = 0.15;

        var nibbles = (channel.BitWidth + 3) / 4;
        var fillColor = ImGui.GetColorU32(ImGuiCol.PlotHistogram, 0.35f);
        var borderColor = ImGui.GetColorU32(ImGuiCol.PlotHistogram);
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);

        var drawList = ImPlot.GetPlotDrawList();

        ImPlot.PushPlotClipRect();

        var runStart = 0;
        while (runStart < visibleCount)
        {
            var value = ys[runStart];

            var runEnd = runStart + 1;
            while (runEnd < visibleCount && ys[runEnd] == value)
            {
                runEnd++;
            }

            // Right edge is deliberately allowed to land one sample past the
            // window for the last run - PushPlotClipRect() above clips it back
            // to the axis limit, and it avoids the last (possibly one-sample-wide)
            // run collapsing to a zero-width band.
            var pMin = ImPlot.PlotToPixels(runStart, BandTop);
            var pMax = ImPlot.PlotToPixels(runEnd, BandBottom);

            drawList.AddRectFilled(pMin, pMax, fillColor);
            drawList.AddRect(pMin, pMax, borderColor);

            var text = ((ulong)value).ToString($"X{nibbles}");
            var textSize = ImGui.CalcTextSize(text);
            var segmentWidth = pMax.X - pMin.X;
            if (textSize.X + 4f <= segmentWidth)
            {
                var textPos = new Vector2(
                    pMin.X + (segmentWidth - textSize.X) * 0.5f,
                    (pMin.Y + pMax.Y - textSize.Y) * 0.5f);
                drawList.AddText(textPos, textColor, text);
            }

            runStart = runEnd;
        }

        ImPlot.PopPlotClipRect();

        if (ImPlot.IsPlotHovered())
        {
            var mouse = ImPlot.GetPlotMousePos();
            var index = (int)Math.Floor(mouse.X);
            if (index >= 0 && index < visibleCount)
            {
                var value = (ulong)ys[index];
                ImGui.SetTooltip($"{channel.Name}: {value.ToString($"X{nibbles}")}");
            }
        }
    }

    private void FillVisibleSamples(ScopeChannel channel, int visibleCount, Span<double> destination)
    {
        var channelIndex = _channelIndex[channel];
        var buffer = _recorder.GetChannelBuffer(channelIndex);
        var capacity = _recorder.Capacity;
        var writeIndex = _recorder.WriteIndex;

        for (var i = 0; i < visibleCount; i++)
        {
            var bufferIndex = (((writeIndex - visibleCount + i) % capacity) + capacity) % capacity;
            destination[i] = buffer[bufferIndex];
        }
    }

    public override void Dispose()
    {
        base.Dispose();

        _debugger.Ticked -= OnTicked;
    }
}
