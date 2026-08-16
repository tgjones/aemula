using System;
using System.Collections.Generic;
using System.Numerics;
using Aemula.Debugging;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;

namespace Aemula.UI.Oscilloscope;

/// <summary>
/// Logic-analyzer-style debugger window: a channel sidebar (grouped, collapsible,
/// per-channel show/hide) next to a waveform pane. Digital channels only for now -
/// see docs/oscilloscope-plan.md for the phased plan (bus/hex-band rendering,
/// timescale controls, etc. come later).
/// </summary>
public sealed class OscilloscopeWindow : DebuggerWindow
{
    private readonly Debugger _debugger;
    private readonly ScopeChannelNode _root;
    private readonly ScopeRecorder _recorder;
    private readonly Dictionary<ScopeChannel, int> _channelIndex;
    private readonly bool[] _channelVisible;
    private readonly List<ScopeChannel> _visibleDigitalChannels = [];

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

        _channelVisible = new bool[_recorder.Channels.Length];
        Array.Fill(_channelVisible, true);

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
        var sidebarWidth = ImGui.GetFontSize() * 12f;

        ImGui.BeginChild("##oscilloscope_channels"u8, new Vector2(sidebarWidth, 0), ImGuiChildFlags.Borders);
        DrawChannelTree(_root);
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##oscilloscope_waveforms"u8);
        DrawWaveforms();
        ImGui.EndChild();
    }

    private void DrawChannelTree(ScopeChannelNode node)
    {
        switch (node)
        {
            case ScopeChannel channel:
                var index = _channelIndex[channel];
                var visible = _channelVisible[index];
                if (ImGui.Checkbox(channel.Name, ref visible))
                {
                    _channelVisible[index] = visible;
                }
                break;

            case ScopeChannelGroup group:
                if (ImGui.TreeNodeEx(group.Name, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    foreach (var child in group.Children)
                    {
                        DrawChannelTree(child);
                    }

                    ImGui.TreePop();
                }
                break;
        }
    }

    private unsafe void DrawWaveforms()
    {
        _visibleDigitalChannels.Clear();
        foreach (var channel in _recorder.Channels)
        {
            if (channel.Kind == ScopeChannelKind.Digital && _channelVisible[_channelIndex[channel]])
            {
                _visibleDigitalChannels.Add(channel);
            }
        }

        if (_visibleDigitalChannels.Count == 0)
        {
            ImGui.TextDisabled("No channels selected.");
            return;
        }

        // One sample = one pixel, fixed zoom for now - see phase 3 in the plan for
        // proper zoom/pan. Since new samples always land at the right edge (index
        // visibleCount - 1) and we recompute this range every frame, the view is
        // right-anchored to "now" while running and holds still for free once the
        // debugger stops (no more ticks means no more samples to show).
        var availableSamples = (int)Math.Min(_recorder.TotalSamples, _recorder.Capacity);
        var plotWidth = (int)MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var visibleCount = Math.Min(availableSamples, plotWidth);

        // One subplot row per channel, its title sitting right above that row's
        // waveform, with LinkAllX keeping every row's time axis in lockstep - the
        // layout the plan (and Saleae/Logic-style analyzers) actually call for,
        // rather than folding every channel into one plot via ImPlot's own digital-
        // signal auto-stacking, which left channel names stranded in the sidebar
        // with no visible link to which band was which.
        if (!ImPlot.BeginSubplots(
            "##oscilloscope_subplots"u8,
            _visibleDigitalChannels.Count,
            1,
            ImGui.GetContentRegionAvail(),
            ImPlotSubplotFlags.LinkAllX | ImPlotSubplotFlags.NoMenus))
        {
            return;
        }

        if (visibleCount > 0)
        {
            Span<double> xs = visibleCount <= 4096 ? stackalloc double[visibleCount] : new double[visibleCount];
            for (var i = 0; i < visibleCount; i++)
            {
                xs[i] = i;
            }

            Span<double> ys = visibleCount <= 4096 ? stackalloc double[visibleCount] : new double[visibleCount];

            foreach (var channel in _visibleDigitalChannels)
            {
                if (!ImPlot.BeginPlot(channel.Name, default, ImPlotFlags.NoLegend | ImPlotFlags.NoMenus))
                {
                    continue;
                }

                ImPlot.SetupAxes(
                    ""u8,
                    ""u8,
                    ImPlotAxisFlags.NoTickLabels | ImPlotAxisFlags.NoGridLines,
                    ImPlotAxisFlags.NoTickLabels | ImPlotAxisFlags.NoGridLines);
                ImPlot.SetupAxesLimits(0, Math.Max(visibleCount - 1, 1), 0, 1, ImPlotCond.Always);

                FillVisibleSamples(channel, visibleCount, ys);

                fixed (double* xsPtr = xs)
                fixed (double* ysPtr = ys)
                {
                    ImPlot.PlotDigital(channel.Name, xsPtr, ysPtr, visibleCount);
                }

                ImPlot.EndPlot();
            }
        }

        ImPlot.EndSubplots();
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
