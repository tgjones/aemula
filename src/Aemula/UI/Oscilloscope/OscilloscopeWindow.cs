using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Aemula.Debugging;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;

namespace Aemula.UI.Oscilloscope;

/// <summary>
/// Logic-analyzer-style debugger window: one merged tree/waveform view, channel
/// name to the left of its own row's trace, grouped under collapsible headers
/// (expanded by default). All channels are always recorded and shown - no
/// per-channel hide toggle. See docs/oscilloscope-plan.md for the phased plan.
///
/// X-axis is in absolute sample index units (one tick = one <see cref="Debugger.Ticked"/>
/// call). The time ruler is drawn exactly once, as a frozen header row above the
/// scrolling channel rows (<see cref="DrawTimescaleRow"/>) rather than per-row -
/// individual channel rows plot data against the same range but hide their own
/// axis labels. While the debugger runs, the axis is pinned to the live edge every
/// frame (no interaction); once stopped, <see cref="_viewMin"/>/<see cref="_viewMax"/>
/// back ImPlot's own pan/zoom via SetupAxisLinks (see <see cref="SetupSharedXAxis"/>),
/// shared across every row's independent BeginPlot so they stay in sync without
/// ImPlot.BeginSubplots (see "Layout false starts" in the plan doc). The zoom level
/// can also be driven directly via the +/- buttons or the "ms / 100px" textbox in
/// the toolbar, Saleae Logic-style.
/// </summary>
public sealed class OscilloscopeWindow : DebuggerWindow
{
    // Digital's fixed 0/1 value-axis labels, in the same (Value, Label) shape
    // as ScopeChannel.AnalogTicks - see DrawValueAxisLabels for why both kinds
    // render through the one shared mechanism instead of ImPlot's own native
    // Y-axis tick labels.
    private static readonly (double Value, string Label)[] DigitalAxisLabels = [(1.0, "H"), (0.0, "L")];

    private const double ZoomFactor = 1.5;
    private const double MinWindowWidthSamples = 2.0;

    // Headroom added around each Analog channel's own AnalogMin/AnalogMax
    // (see ScopeChannel) so its trace doesn't clip against the plot edge -
    // a generic rendering choice, not specific to any one signal.
    private const double AnalogAxisPaddingFraction = 0.05;

    private readonly Debugger _debugger;
    private readonly ScopeChannelNode _root;
    private readonly ScopeRecorder _recorder;
    private readonly Dictionary<ScopeChannel, int> _channelIndex;
    private readonly double _cyclesPerSecond;
    private readonly ImPlotFormatter _timeAxisFormatter;

    // Group tree collapse state, keyed by "/"-joined name path from the root
    // (e.g. "Apple II/MOS6502"). Absence means expanded (the default); persisted
    // across sessions via GetPersistedSettingsLines/ApplyPersistedSettingsLine,
    // the same ImGuiSettingsHandler plumbing Program.cs already uses for IsOpen.
    private readonly HashSet<string> _collapsedGroupPaths = new();

    // Shared x-axis view range (absolute sample index units), backing every row's
    // (and the timescale ruler's) linked axis while stopped - see class remarks.
    private double _viewMin;
    private double _viewMax;
    private bool _wasStopped;

    // Saleae-style zoom readout/control, kept in sync with _viewMin/_viewMax (see
    // DrawOverride) but editable independently via the toolbar's +/- buttons and
    // textbox.
    private double _millisecondsPer100Px;
    private string _zoomInputBuffer = string.Empty;
    private bool _zoomInputWasActive;

    public override string DisplayName => "Oscilloscope";

    public override Pane PreferredPane => Pane.Bottom;

    public unsafe OscilloscopeWindow(Debugger debugger, ScopeChannelNode channels)
    {
        _debugger = debugger;
        _root = channels;
        _recorder = new ScopeRecorder(channels);
        _cyclesPerSecond = debugger.System.CyclesPerSecond;
        _timeAxisFormatter = FormatTimeAxisTick;
        // One sample per pixel, matching the fixed zoom phases 1/2 used as a
        // starting point.
        _millisecondsPer100Px = 100_000.0 / _cyclesPerSecond;

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

        // Fixed value-axis-label gutter, reserved identically inside every
        // row's Waveform cell regardless of channel kind - see
        // DrawValueAxisLabels for why this can't be left to ImPlot's own
        // per-row Y-tick-label reservation (its width varies with label
        // text, which desyncs each row's plot origin from the others').
        var valueLabelWidth = ImGui.GetFontSize() * 4f;

        var jumpToNow = ImGui.Button("Jump to Now"u8);
        ImGui.SameLine();
        ImGui.TextUnformatted("Zoom:"u8);
        ImGui.SameLine();
        var zoomOutClicked = ImGui.Button("-"u8);
        ImGui.SameLine();

        if (!_zoomInputWasActive)
        {
            _zoomInputBuffer = _millisecondsPer100Px.ToString("0.###", CultureInfo.InvariantCulture);
        }
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6f);
        var zoomCommitted = ImGui.InputText(
            "##zoomMsPer100Px"u8,
            ref _zoomInputBuffer,
            32,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
        _zoomInputWasActive = ImGui.IsItemActive();

        ImGui.SameLine();
        var zoomInClicked = ImGui.Button("+"u8);
        ImGui.SameLine();
        ImGui.TextUnformatted("ms / 100px"u8);

        var stopped = _debugger.Stopped;
        var justStopped = stopped && !_wasStopped;
        _wasStopped = stopped;

        var total = _recorder.TotalSamples;
        var capacity = _recorder.Capacity;
        var oldestRetained = Math.Max(0, total - capacity);
        var liveEdge = (double)total;
        // Padded so axis constraints/limits never collapse to a zero-width range
        // before any samples have been recorded.
        var axisUpperBound = Math.Max(liveEdge, oldestRetained + 1);

        var plotWidthPixels = Math.Max(1.0, ImGui.GetContentRegionAvail().X - labelColumnWidth - valueLabelWidth);

        var zoomChanged = zoomOutClicked || zoomInClicked;
        if (zoomOutClicked)
        {
            _millisecondsPer100Px *= ZoomFactor;
        }
        if (zoomInClicked)
        {
            _millisecondsPer100Px /= ZoomFactor;
        }
        if (zoomCommitted && double.TryParse(_zoomInputBuffer, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedZoom) && parsedZoom > 0)
        {
            _millisecondsPer100Px = parsedZoom;
            zoomChanged = true;
        }
        _millisecondsPer100Px = Math.Max(1e-6, _millisecondsPer100Px);

        var windowWidthSamples = Math.Max(
            MinWindowWidthSamples,
            _millisecondsPer100Px / 1000.0 * _cyclesPerSecond / 100.0 * plotWidthPixels);

        if (!stopped || justStopped || jumpToNow)
        {
            _viewMax = liveEdge;
            _viewMin = liveEdge - windowWidthSamples;
        }
        else if (zoomChanged)
        {
            var center = (_viewMin + _viewMax) * 0.5;
            _viewMin = center - windowWidthSamples * 0.5;
            _viewMax = center + windowWidthSamples * 0.5;
        }
        else if (total > 0)
        {
            // Steady stopped frame: reflect whatever the user just dragged or
            // scroll-zoomed via the linked axes back into the zoom readout. Skipped
            // while the buffer is still empty, since the view is then pinned to a
            // degenerate 1-sample span just to keep the axis calls well-formed (see
            // axisUpperBound above) - reading that back would corrupt the zoom
            // readout to ~0 before any real data exists.
            _millisecondsPer100Px = (_viewMax - _viewMin) / plotWidthPixels * 100.0 / _cyclesPerSecond * 1000.0;
        }

        ClampView(oldestRetained, axisUpperBound);

        if (_viewMax <= _viewMin)
        {
            _viewMax = _viewMin + 1;
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
        ImGui.TableSetupScrollFreeze(0, 1);

        DrawTimescaleRow(stopped, oldestRetained, axisUpperBound, valueLabelWidth);

        DrawChannelNode(_root, string.Empty, stopped, oldestRetained, axisUpperBound, valueLabelWidth);

        ImGui.EndTable();
    }

    protected override IEnumerable<KeyValuePair<string, string>> GetPersistedSettings()
    {
        if (_collapsedGroupPaths.Count > 0)
        {
            yield return new("CollapsedGroups", string.Join(';', _collapsedGroupPaths));
        }
    }

    protected override void ApplyPersistedSetting(string key, string value)
    {
        if (key != "CollapsedGroups")
        {
            return;
        }

        foreach (var path in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            _collapsedGroupPaths.Add(path);
        }
    }

    private void ClampView(double oldestRetained, double axisUpperBound)
    {
        var span = _viewMax - _viewMin;
        var maxSpan = axisUpperBound - oldestRetained;
        if (span > maxSpan)
        {
            span = maxSpan;
        }

        if (_viewMin < oldestRetained)
        {
            _viewMin = oldestRetained;
            _viewMax = _viewMin + span;
        }
        else if (_viewMax > axisUpperBound)
        {
            _viewMax = axisUpperBound;
            _viewMin = _viewMax - span;
        }
    }

    // Renders the shared time ruler once, as a frozen table header row (see
    // ImGui.TableSetupScrollFreeze in DrawOverride) so it stays pinned above the
    // channel rows while they scroll, instead of every row drawing its own
    // tick labels.
    private void DrawTimescaleRow(bool stopped, double oldestRetained, double axisUpperBound, float valueLabelWidth)
    {
        var rowHeight = ImGui.GetTextLineHeightWithSpacing() * 2f;

        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        // Same fixed shift every channel row applies (see DrawChannelRow),
        // so the ruler's own BeginPlot starts at the identical pixel X as
        // every channel row's, keeping the shared X axis genuinely aligned.
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + valueLabelWidth);

        if (!ImPlot.BeginPlot(
            "##timescale"u8,
            new Vector2(-1, rowHeight),
            ImPlotFlags.NoLegend | ImPlotFlags.NoMenus | ImPlotFlags.NoMouseText))
        {
            return;
        }

        ImPlot.SetupAxes(
            ""u8,
            ""u8,
            ImPlotAxisFlags.NoGridLines,
            ImPlotAxisFlags.NoGridLines | ImPlotAxisFlags.NoTickLabels);
        ImPlot.SetupAxisFormat(ImAxis.X1, _timeAxisFormatter);
        ImPlot.SetupAxisLimits(ImAxis.Y1, 0, 1, ImPlotCond.Always);

        SetupSharedXAxis(stopped, oldestRetained, axisUpperBound);

        ImPlot.EndPlot();
    }

    // Shared X1 setup used by both the timescale ruler and every channel row, so
    // they all resolve to the same axis range each frame - see class remarks for
    // why SetupAxisLinks is what keeps independently-BeginPlot'd rows in sync.
    private void SetupSharedXAxis(bool stopped, double oldestRetained, double axisUpperBound)
    {
        if (stopped)
        {
            ImPlot.SetupAxisLimitsConstraints(ImAxis.X1, oldestRetained, axisUpperBound);
            if (axisUpperBound - oldestRetained >= 4.0)
            {
                ImPlot.SetupAxisZoomConstraints(ImAxis.X1, 2.0, axisUpperBound - oldestRetained);
            }
            ImPlot.SetupAxisLinks(ImAxis.X1, ref _viewMin, ref _viewMax);
        }
        else
        {
            ImPlot.SetupAxisLimits(ImAxis.X1, _viewMin, _viewMax, ImPlotCond.Always);
        }
    }

    private void DrawChannelNode(ScopeChannelNode node, string parentPath, bool stopped, double oldestRetained, double axisUpperBound, float valueLabelWidth)
    {
        switch (node)
        {
            case ScopeChannel channel:
                DrawChannelRow(channel, stopped, oldestRetained, axisUpperBound, valueLabelWidth);
                break;

            case ScopeChannelGroup group:
                var path = parentPath.Length == 0 ? group.Name : $"{parentPath}/{group.Name}";

                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                // Applies persisted state the first time this ID is seen this
                // session; a manual toggle afterward takes over as normal,
                // reflected back into _collapsedGroupPaths below so it's what
                // gets written out again on save.
                ImGui.SetNextItemOpen(!_collapsedGroupPaths.Contains(path), ImGuiCond.FirstUseEver);

                var isOpen = ImGui.TreeNodeEx(group.Name, ImGuiTreeNodeFlags.SpanAllColumns);
                if (isOpen)
                {
                    _collapsedGroupPaths.Remove(path);

                    foreach (var child in group.Children)
                    {
                        DrawChannelNode(child, path, stopped, oldestRetained, axisUpperBound, valueLabelWidth);
                    }

                    ImGui.TreePop();
                }
                else
                {
                    _collapsedGroupPaths.Add(path);
                }
                break;
        }
    }

    private unsafe void DrawChannelRow(ScopeChannel channel, bool stopped, double oldestRetained, double axisUpperBound, float valueLabelWidth)
    {
        var rowHeight = ImGui.GetTextLineHeightWithSpacing() * 3.5f;

        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(channel.Name);

        ImGui.TableNextColumn();

        var isDigital = channel.Kind == ScopeChannelKind.Digital;
        var isAnalog = channel.Kind == ScopeChannelKind.Analog;

        // Deterministic per-channel color, distinct from every other channel's own
        // independent plot (each row is its own BeginPlot, so ImPlot's normal
        // per-plot color cycling would otherwise hand every row the same first
        // color) - GetColormapColor wraps by channel count, so this stays stable
        // and theme-independent regardless of how many channels are recorded.
        var color = ImPlot.GetColormapColor(_channelIndex[channel], ImPlotColormap.Deep);

        // Reserve the fixed value-label gutter (see DrawOverride/
        // DrawValueAxisLabels) before this row's own BeginPlot, so its plot
        // origin lands at the same pixel X as every other row's regardless
        // of what value-axis labels this channel has.
        var labelRegionScreenPos = ImGui.GetCursorScreenPos();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + valueLabelWidth);

        if (!ImPlot.BeginPlot(
            $"##{channel.Name}",
            new Vector2(-1, rowHeight),
            ImPlotFlags.NoLegend | ImPlotFlags.NoMenus | ImPlotFlags.NoMouseText))
        {
            return;
        }

        // Y1 never shows ImPlot's own native tick labels, on any channel
        // kind - their width varies with label text (e.g. Analog's "White"
        // vs. Digital's "H"), which would otherwise change how much of this
        // row's BeginPlot ImPlot reserves for the label gutter and desync
        // this row's plot origin from every other row's. DrawValueAxisLabels
        // below renders the equivalent labels manually instead, into the
        // fixed gutter reserved above.
        ImPlot.SetupAxes(
            ""u8,
            ""u8,
            ImPlotAxisFlags.NoTickLabels | ImPlotAxisFlags.NoGridLines,
            ImPlotAxisFlags.NoTickLabels | ImPlotAxisFlags.NoGridLines);
        if (isAnalog)
        {
            var padding = (channel.AnalogMax - channel.AnalogMin) * AnalogAxisPaddingFraction;
            ImPlot.SetupAxisLimits(ImAxis.Y1, channel.AnalogMin - padding, channel.AnalogMax + padding, ImPlotCond.Always);
        }
        else
        {
            ImPlot.SetupAxisLimits(ImAxis.Y1, -0.1, 1.1, ImPlotCond.Always);
        }

        SetupSharedXAxis(stopped, oldestRetained, axisUpperBound);

        // PlotToPixels only resolves correctly while this plot is active, so
        // the pixel Y for each value-axis label is captured here and drawn
        // later, after EndPlot() - see DrawValueAxisLabels for why the draw
        // itself has to happen outside Begin/EndPlot.
        IReadOnlyList<(double Value, string Label)> valueAxisLabels = isDigital ? DigitalAxisLabels : channel.AnalogTicks;
        Span<float> valueAxisLabelPixelY = stackalloc float[valueAxisLabels.Count];
        for (var i = 0; i < valueAxisLabels.Count; i++)
        {
            valueAxisLabelPixelY[i] = ImPlot.PlotToPixels(0.0, valueAxisLabels[i].Value).Y;
        }

        var limits = ImPlot.GetPlotLimits(ImAxis.X1);
        var visStart = (long)Math.Max(0, Math.Floor(limits.X.Min));
        var visEndExclusive = Math.Min(_recorder.TotalSamples, (long)Math.Ceiling(limits.X.Max));
        var visibleCount = (int)Math.Max(0, visEndExclusive - visStart);

        if (visibleCount > 0)
        {
            Span<double> xs = visibleCount <= 4096 ? stackalloc double[visibleCount] : new double[visibleCount];
            Span<double> ys = visibleCount <= 4096 ? stackalloc double[visibleCount] : new double[visibleCount];
            FillVisibleSamples(channel, visStart, visibleCount, xs, ys);

            if (isDigital)
            {
                DrawDigitalTrace(channel, color, visStart, visibleCount, xs, ys);
            }
            else if (isAnalog)
            {
                DrawAnalogTrace(channel, color, visStart, visibleCount, xs, ys);
            }
            else
            {
                DrawBusTrace(channel, color, visStart, visibleCount, ys);
            }
        }

        ImPlot.EndPlot();

        DrawValueAxisLabels(valueAxisLabels, valueAxisLabelPixelY, labelRegionScreenPos, valueLabelWidth);
    }

    // Draws each value-axis label (Digital's fixed "H"/"L", or an Analog
    // channel's own AnalogTicks - Bus channels have none) at the pixel Y
    // DrawChannelRow already resolved for it via PlotToPixels, into the
    // fixed gutter reserved to the left of that row's BeginPlot. Done after
    // EndPlot() specifically: ImPlot clips its own draw calls to the plot
    // rect, which starts exactly at the shifted-right cursor position
    // DrawChannelRow moved to before calling BeginPlot - so text drawn
    // during Begin/EndPlot at an X to the left of that (i.e. inside the
    // reserved gutter) would be invisible, clipped by the still-active plot
    // clip rect.
    private static void DrawValueAxisLabels(IReadOnlyList<(double Value, string Label)> labels, ReadOnlySpan<float> pixelY, Vector2 regionScreenPos, float regionWidth)
    {
        const float RightPadding = 6f;

        if (labels.Count == 0)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);

        for (var i = 0; i < labels.Count; i++)
        {
            var textSize = ImGui.CalcTextSize(labels[i].Label);
            var pos = new Vector2(
                regionScreenPos.X + regionWidth - RightPadding - textSize.X,
                pixelY[i] - textSize.Y * 0.5f);
            drawList.AddText(pos, textColor, labels[i].Label);
        }
    }

    private static unsafe void DrawDigitalTrace(ScopeChannel channel, Vector4 color, long visStart, int visibleCount, ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        ImPlot.PushStyleColor(ImPlotCol.Line, color);

        fixed (double* xsPtr = xs)
        fixed (double* ysPtr = ys)
        {
            ImPlot.PlotStairs("##data"u8, xsPtr, ysPtr, visibleCount);
        }

        ImPlot.PopStyleColor();

        if (ImPlot.IsPlotHovered())
        {
            var mouse = ImPlot.GetPlotMousePos();
            var index = (int)Math.Round(mouse.X - visStart);
            if (index >= 0 && index < visibleCount)
            {
                var value = ys[index];
                ImGui.SetTooltip($"{channel.Name}: {(value != 0 ? "H" : "L")}");
            }
        }
    }

    // Analog rendering: PlotStairs, same as Digital - the underlying signal
    // really is a discrete step at one sample per master tick (no
    // between-sample interpolation is modelled, docs/apple-ii-ntsc-video-plan.md
    // "Sample rate"), so a step trace is the faithful rendering for the
    // black/white/sync portions. The color-burst sine only has 4
    // samples/cycle at this sample rate (TickCompositeVideo's comment) and
    // so still reads as a jagged staircase rather than a smooth curve, but
    // that's the actual signal, not a rendering artifact - PlotLine's
    // straight-line interpolation was tried first specifically to make the
    // burst look smoother, but that came at the cost of misrepresenting the
    // otherwise-square black/white/sync edges as sloped ramps.
    private static unsafe void DrawAnalogTrace(ScopeChannel channel, Vector4 color, long visStart, int visibleCount, ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        ImPlot.PushStyleColor(ImPlotCol.Line, color);

        fixed (double* xsPtr = xs)
        fixed (double* ysPtr = ys)
        {
            ImPlot.PlotStairs("##data"u8, xsPtr, ysPtr, visibleCount);
        }

        ImPlot.PopStyleColor();

        if (ImPlot.IsPlotHovered())
        {
            var mouse = ImPlot.GetPlotMousePos();
            var index = (int)Math.Round(mouse.X - visStart);
            if (index >= 0 && index < visibleCount)
            {
                ImGui.SetTooltip($"{channel.Name}: {(byte)ys[index]}");
            }
        }
    }

    // Hex-banded bus rendering: one filled/outlined rectangle per run of equal
    // samples, so edges land exactly at value-change points, with the hex value
    // centered in the rectangle when there's room for it. ImPlot has no built-in
    // "bus" mark, so this draws directly into plot pixel space via
    // GetPlotDrawList()/PlotToPixels() - see "Open risks" in the plan doc.
    private static void DrawBusTrace(ScopeChannel channel, Vector4 color, long visStart, int visibleCount, ReadOnlySpan<double> ys)
    {
        const double BandTop = 0.85;
        const double BandBottom = 0.15;

        var nibbles = (channel.BitWidth + 3) / 4;
        var fillColor = ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.35f));
        var borderColor = ImGui.GetColorU32(color);
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
            var pMin = ImPlot.PlotToPixels(visStart + runStart, BandTop);
            var pMax = ImPlot.PlotToPixels(visStart + runEnd, BandBottom);

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
            var index = (int)Math.Floor(mouse.X - visStart);
            if (index >= 0 && index < visibleCount)
            {
                var value = (ulong)ys[index];
                ImGui.SetTooltip($"{channel.Name}: {value.ToString($"X{nibbles}")}");
            }
        }
    }

    private void FillVisibleSamples(ScopeChannel channel, long visStart, int visibleCount, Span<double> xs, Span<double> ys)
    {
        var channelIndex = _channelIndex[channel];
        var buffer = _recorder.GetChannelBuffer(channelIndex);
        var capacity = _recorder.Capacity;

        for (var i = 0; i < visibleCount; i++)
        {
            var absoluteIndex = visStart + i;
            xs[i] = absoluteIndex;
            ys[i] = buffer[(int)(absoluteIndex % capacity)];
        }
    }

    // ImPlot tick-label formatter: converts an x-axis value (absolute sample
    // index, i.e. tick count since recording started) to a time string, with
    // the unit adapted to magnitude since the visible span usually ranges from
    // a handful of microseconds (zoomed in) up to however long the ring buffer
    // retains (zoomed all the way out).
    private unsafe int FormatTimeAxisTick(double sampleIndex, byte* buff, int size, void* userData)
    {
        var text = FormatDuration(sampleIndex / _cyclesPerSecond);
        if (text.Length >= size)
        {
            text = text[..(size - 1)];
        }

        var destination = new Span<byte>(buff, size);
        var written = Encoding.ASCII.GetBytes(text, destination);
        destination[written] = 0;
        return written;
    }

    private static string FormatDuration(double seconds)
    {
        var abs = Math.Abs(seconds);

        if (abs >= 1.0)
        {
            return $"{seconds:0.###} s";
        }

        if (abs >= 0.001)
        {
            return $"{seconds * 1_000:0.###} ms";
        }

        if (abs >= 0.000_001)
        {
            return $"{seconds * 1_000_000:0.###} us";
        }

        return $"{seconds * 1_000_000_000:0.###} ns";
    }

    public override void Dispose()
    {
        base.Dispose();

        _debugger.Ticked -= OnTicked;
    }
}
