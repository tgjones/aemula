using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Aemula.Debugging;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;

namespace Aemula.UI.Oscilloscope;

/// <summary>
/// Logic-analyzer-style debugger window: one merged tree/waveform view, channel
/// name to the left of its own row's trace, grouped under non-collapsible
/// headers (visual organization only). All channels are always recorded and
/// shown - no per-channel hide toggle. See docs/oscilloscope-plan.md for the
/// phased plan.
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

    private static readonly Vector4 TransparentColor = new(0f, 0f, 0f, 0f);

    private const double ZoomFactor = 1.5;
    private const double MinWindowWidthSamples = 2.0;

    // Headroom added around each Analog channel's own AnalogMin/AnalogMax
    // (see ScopeChannel) so its trace doesn't clip against the plot edge -
    // a generic rendering choice, not specific to any one signal.
    private const double AnalogAxisPaddingFraction = 0.05;

    // Target on-screen spacing between shared gridlines - both the timescale
    // row's own tick labels and DrawSharedGridlines' vertical lines derive
    // from the same ComputeGridTicks call, so they always land at the same
    // pixel X.
    private const float TargetGridSpacingPixels = 100f;

    // Width of the Saleae-style color swatch drawn at the left edge of each
    // channel row's name cell, in the channel's own trace color.
    private const float ChannelColorBarWidth = 4f;

    private readonly Debugger _debugger;
    private readonly IReadOnlyList<ScopeChannelNode> _roots;
    private readonly ScopeRecorder _recorder;
    private readonly Dictionary<ScopeChannel, int> _channelIndex;
    private readonly double _cyclesPerSecond;

    // Shared x-axis view range (absolute sample index units), backing every row's
    // (and the timescale ruler's) linked axis while stopped - see class remarks.
    private double _viewMin;
    private double _viewMax;
    private bool _wasStopped;

    // Pixel bounds of the timescale ruler's own plot, captured each frame in
    // DrawTimescaleRow - every channel row's BeginPlot fills the same Waveform
    // column width at the same X shift, so this rect is authoritative for
    // every row's plot area without re-deriving it from column widths/padding.
    // Used after EndTable() to draw the shared vertical gridlines - see
    // DrawSharedGridlines.
    private float _plotLeft;
    private float _plotRight;
    private float _plotTop;

    // Saleae-style zoom readout/control, kept in sync with _viewMin/_viewMax (see
    // DrawOverride) but editable independently via the toolbar's +/- buttons and
    // textbox.
    private double _millisecondsPer100Px;
    private string _zoomInputBuffer = string.Empty;
    private bool _zoomInputWasActive;

    public override string DisplayName => "Oscilloscope";

    public override Pane PreferredPane => Pane.Bottom;

    public OscilloscopeWindow(Debugger debugger, IReadOnlyList<ScopeChannelNode> channels)
    {
        _debugger = debugger;
        _roots = channels;
        _recorder = new ScopeRecorder(channels);
        _cyclesPerSecond = debugger.System.CyclesPerSecond;
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

        var gridTicks = ComputeGridTicks(_viewMin, _viewMax, plotWidthPixels);

        // Every row's own ImPlot frame/background/border is pushed transparent
        // for the whole table - the shared gridlines drawn behind the table
        // (see DrawSharedGridlines) are what read as the row grid now, instead
        // of each row rendering its own separate boxed plot.
        ImPlot.PushStyleColor(ImPlotCol.FrameBg, TransparentColor);
        ImPlot.PushStyleColor(ImPlotCol.Bg, TransparentColor);
        ImPlot.PushStyleColor(ImPlotCol.Border, TransparentColor);

        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        if (!ImGui.BeginTable(
            "##oscilloscope_table"u8,
            2,
            ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
            ImGui.GetContentRegionAvail()))
        {
            ImPlot.PopStyleColor(3);
            drawList.ChannelsMerge();
            return;
        }

        ImGui.TableSetupColumn("Channel"u8, ImGuiTableColumnFlags.WidthFixed, labelColumnWidth);
        ImGui.TableSetupColumn("Waveform"u8, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1);

        DrawTimescaleRow(stopped, oldestRetained, axisUpperBound, valueLabelWidth, gridTicks);

        foreach (var root in _roots)
        {
            DrawChannelNode(root, stopped, oldestRetained, axisUpperBound, valueLabelWidth);
        }

        var tableMax = ImGui.GetItemRectMax();

        ImGui.EndTable();

        ImPlot.PopStyleColor(3);

        // Drawn into the split-off background channel so it renders behind
        // every row's content (plot traces, group labels) merged above it,
        // regardless of the order the two were actually issued in.
        drawList.ChannelsSetCurrent(0);
        DrawSharedGridlines(drawList, gridTicks, tableMax.Y);
        drawList.ChannelsMerge();
    }

    // Picks "nice" (1/2/5 * 10^n) gridline spacing in sample-index units,
    // aiming for roughly one gridline per TargetGridSpacingPixels of plot
    // width - mirrors the classic axis-tick "nice number" algorithm so
    // spacing looks like a normal ruler instead of arbitrary fractional
    // sample counts.
    private static double[] ComputeGridTicks(double viewMin, double viewMax, double plotWidthPixels)
    {
        var range = viewMax - viewMin;
        if (range <= 0 || plotWidthPixels <= 0)
        {
            return [];
        }

        var targetTickCount = Math.Max(1.0, plotWidthPixels / TargetGridSpacingPixels);
        var roughStep = range / targetTickCount;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
        var normalized = roughStep / magnitude;

        double niceNormalized;
        if (normalized < 1.5)
        {
            niceNormalized = 1;
        }
        else if (normalized < 3)
        {
            niceNormalized = 2;
        }
        else if (normalized < 7)
        {
            niceNormalized = 5;
        }
        else
        {
            niceNormalized = 10;
        }

        var step = niceNormalized * magnitude;
        if (step <= 0 || double.IsNaN(step) || double.IsInfinity(step))
        {
            return [];
        }

        var first = Math.Ceiling(viewMin / step) * step;

        var ticks = new List<double>();
        for (var tick = first; tick <= viewMax && ticks.Count < 1000; tick += step)
        {
            ticks.Add(tick);
        }

        return ticks.ToArray();
    }

    // Draws one vertical line per shared gridline tick, spanning from the
    // bottom of the frozen timescale row (_plotTop) down to the bottom of the
    // table's own rendered rect (tableBottom - i.e. the visible channel-row
    // viewport, whether or not it's scrolled) - a single shared line per tick
    // instead of every row drawing its own short, separately-boxed segment.
    // Called with the window draw list's background channel current (see
    // DrawOverride) so it lands behind every row's own content.
    private void DrawSharedGridlines(ImDrawListPtr drawList, double[] ticks, float tableBottom)
    {
        if (ticks.Length == 0 || tableBottom <= _plotTop || _plotRight <= _plotLeft)
        {
            return;
        }

        var color = ImGui.GetColorU32(ImGuiCol.TableBorderLight);
        var range = _viewMax - _viewMin;

        drawList.PushClipRect(new Vector2(_plotLeft, _plotTop), new Vector2(_plotRight, tableBottom), true);

        foreach (var tick in ticks)
        {
            var t = (tick - _viewMin) / range;
            var x = _plotLeft + (float)(t * (_plotRight - _plotLeft));
            drawList.AddLine(new Vector2(x, _plotTop), new Vector2(x, tableBottom), color);
        }

        drawList.PopClipRect();
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
    private unsafe void DrawTimescaleRow(bool stopped, double oldestRetained, double axisUpperBound, float valueLabelWidth, double[] gridTicks)
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
        ImPlot.SetupAxisLimits(ImAxis.Y1, 0, 1, ImPlotCond.Always);

        SetupSharedXAxis(stopped, oldestRetained, axisUpperBound);

        // Explicit ticks (rather than ImPlot's own auto-generated ones via
        // SetupAxisFormat) so the labels shown here land at exactly the same
        // sample-index positions as DrawSharedGridlines' vertical lines.
        if (gridTicks.Length > 0)
        {
            Span<double> tickValues = stackalloc double[gridTicks.Length];
            var tickLabels = new string[gridTicks.Length];
            for (var i = 0; i < gridTicks.Length; i++)
            {
                tickValues[i] = gridTicks[i];
                tickLabels[i] = FormatDuration(gridTicks[i] / _cyclesPerSecond);
            }

            fixed (double* tickValuesPtr = tickValues)
            {
                ImPlot.SetupAxisTicks(ImAxis.X1, tickValuesPtr, gridTicks.Length, tickLabels);
            }
        }

        ImPlot.EndPlot();

        // Captured here rather than re-derived from column widths/padding
        // after EndTable() - see _plotLeft/_plotRight/_plotTop remarks.
        var plotRectMin = ImGui.GetItemRectMin();
        var plotRectMax = ImGui.GetItemRectMax();
        _plotLeft = plotRectMin.X;
        _plotRight = plotRectMax.X;
        _plotTop = plotRectMax.Y;
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

    private void DrawChannelNode(ScopeChannelNode node, bool stopped, double oldestRetained, double axisUpperBound, float valueLabelWidth)
    {
        switch (node)
        {
            case ScopeChannel channel:
                DrawChannelRow(channel, stopped, oldestRetained, axisUpperBound, valueLabelWidth);
                break;

            case ScopeChannelGroup group:
                var groupRowHeight = ImGui.GetTextLineHeightWithSpacing() * 1.6f;

                ImGui.TableNextRow(ImGuiTableRowFlags.None, groupRowHeight);

                // Full-width shaded band (RowBg0 spans every column, including
                // the otherwise-empty Waveform column) so this reads as a
                // section header for the channels beneath it, now that groups
                // are plain labels rather than collapsible tree nodes.
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.TableHeaderBg));

                ImGui.TableNextColumn();

                var groupTextHeight = ImGui.GetTextLineHeight();
                var groupCellScreenPos = ImGui.GetCursorScreenPos();
                // Same left offset a channel row's name gets past its color bar
                // (see DrawChannelRow), so this label lines up with the channel
                // names underneath it despite having no bar of its own.
                ImGui.SetCursorScreenPos(new Vector2(
                    groupCellScreenPos.X + ChannelColorBarWidth + ImGui.GetStyle().CellPadding.X,
                    groupCellScreenPos.Y + (groupRowHeight - groupTextHeight) * 0.5f));
                ImGui.TextUnformatted(group.Name);

                foreach (var child in group.Children)
                {
                    DrawChannelNode(child, stopped, oldestRetained, axisUpperBound, valueLabelWidth);
                }
                break;
        }
    }

    private unsafe void DrawChannelRow(ScopeChannel channel, bool stopped, double oldestRetained, double axisUpperBound, float valueLabelWidth)
    {
        var rowHeight = ImGui.GetTextLineHeightWithSpacing() * 3.5f;

        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

        // Deterministic per-channel color, distinct from every other channel's own
        // independent plot (each row is its own BeginPlot, so ImPlot's normal
        // per-plot color cycling would otherwise hand every row the same first
        // color) - GetColormapColor wraps by channel count, so this stays stable
        // and theme-independent regardless of how many channels are recorded.
        // Computed up front since it's also used for this row's Saleae-style
        // color bar (see below), drawn before the channel name.
        var color = ImPlot.GetColormapColor(_channelIndex[channel], ImPlotColormap.Deep);

        ImGui.TableNextColumn();

        var cellScreenPos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(
            cellScreenPos,
            new Vector2(cellScreenPos.X + ChannelColorBarWidth, cellScreenPos.Y + rowHeight),
            ImGui.GetColorU32(color));

        var textHeight = ImGui.GetTextLineHeight();
        ImGui.SetCursorScreenPos(new Vector2(
            cellScreenPos.X + ChannelColorBarWidth + ImGui.GetStyle().CellPadding.X,
            cellScreenPos.Y + (rowHeight - textHeight) * 0.5f));
        ImGui.Text(channel.Name);

        ImGui.TableNextColumn();

        var isDigital = channel.Kind == ScopeChannelKind.Digital;
        var isAnalog = channel.Kind == ScopeChannelKind.Analog;

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
        // itself has to happen outside Begin/EndPlot. Analog gets exactly two
        // labels, at its own real-world min/max (e.g. "0 V"/"2 V") - see
        // ScopeChannel.Analog remarks - rather than a caller-supplied list of
        // arbitrary anchor points.
        IReadOnlyList<(double Value, string Label)> valueAxisLabels = isDigital
            ? DigitalAxisLabels
            : isAnalog
                ? [
                    (channel.AnalogMin, FormatUnitValue(channel.AnalogMin, channel.AnalogUnit)),
                    (channel.AnalogMax, FormatUnitValue(channel.AnalogMax, channel.AnalogUnit)),
                  ]
                : [];
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
                ScaleAnalogSamples(channel, ys);
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
                ImGui.SetTooltip($"{channel.Name}: {FormatUnitValue(ys[index], channel.AnalogUnit)}");
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

    // Analog's raw sample is always an 8-bit byte (see ScopeChannel.Analog),
    // linearly rescaled here into the channel's own real-world
    // [AnalogMin, AnalogMax] range so the trace, Y-axis, and value-axis
    // labels all agree on one coordinate system.
    private static void ScaleAnalogSamples(ScopeChannel channel, Span<double> ys)
    {
        var scale = (channel.AnalogMax - channel.AnalogMin) / 255.0;
        for (var i = 0; i < ys.Length; i++)
        {
            ys[i] = channel.AnalogMin + ys[i] * scale;
        }
    }

    private static string FormatUnitValue(double value, string unit)
    {
        var text = value.ToString("0.##", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(unit) ? text : $"{text} {unit}";
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
