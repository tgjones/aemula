using System;
using System.Numerics;
using Aemula.Emulation.Output;
using Aemula.Emulation.Output.Composite;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;
using Hexa.NET.SDL3;

namespace Aemula.UI;

// A basic texture-upload render, wired into a real system, plus the
// Saleae-style niceties - a dot-position crosshair at Television.CurrentColumn/
// CurrentRow, translucent overlays naming the HSYNC/VSYNC/blanking/color-
// burst regions of the raster (independently toggleable from a crop down to
// just the picture - see DrawSidebar), a legend for those overlay colors,
// and a status readout. The Television instance this renders gets fed
// samples elsewhere, live, during whatever system's emulation loop produced
// them (e.g. AppleIISystem.TickCompositeVideo calls Television.Decode
// directly, the same way any other signal propagates through the chips/
// systems that consume it) - this window only ever reads Television's own
// public properties, once per UI frame, and has no idea what's feeding it.
//
public sealed class TelevisionWindow : DebuggerWindow
{
    // Saleae-style translucent region colors - deliberately distinct hues
    // (rather than shades of one color) so overlapping-in-time-but-not-
    // in-name regions (e.g. HSYNC and color burst, both "not picture" but
    // very different things) read as different at a glance, the same way a
    // logic analyzer color-codes distinct signal states. Alpha is kept low
    // so the (dim, grayscale - see Television.Decode's remarks) real pixels
    // underneath still show through.
    private static readonly Vector4 HSyncOverlayColor = new(1.0f, 0.85f, 0.2f, 0.35f);
    private static readonly Vector4 ColorBurstOverlayColor = new(0.2f, 0.9f, 0.9f, 0.4f);
    private static readonly Vector4 BlankingOverlayColor = new(0.55f, 0.55f, 0.55f, 0.3f);
    private static readonly Vector4 VSyncOverlayColor = new(1.0f, 0.3f, 0.15f, 0.4f);

    // Per-sample hover tooltip (DrawHoveredSampleTooltip and friends) -
    // distinct trace colors for the raw signal vs. the three reference sines
    // overlaid on it, plus a soft band marking exactly which raw sample is
    // "the" hovered one among the several shown either side of it for
    // context.
    private static readonly Vector4 RawSignalColor = new(0.82f, 0.82f, 0.82f, 1f);
    private static readonly Vector4 CarrierColor = new(0.2f, 0.9f, 0.9f, 1f);
    private static readonly Vector4 IComponentColor = new(1f, 0.55f, 0.15f, 1f);
    private static readonly Vector4 QComponentColor = new(0.65f, 0.4f, 1f, 1f);
    private static readonly Vector4 LumaColor = new(0.95f, 0.88f, 0.45f, 1f);
    private static readonly Vector4 ChromaColor = new(0.95f, 0.5f, 0.7f, 1f);
    private static readonly Vector4 CurrentSampleBandColor = new(1f, 1f, 1f, 0.18f);

    // The window of the hovered scanline shown in the per-stage waveform
    // rows (DrawHoveredSampleWaveform), in samples before/after the hovered
    // one. Lopsided on purpose: ChromaDecoder is causal, so a sample's color
    // is derived from the raw samples at and *before* it (the 5-tap comb
    // filter plus the 4-sample I/Q average reach back ~8 samples) and nothing
    // after it. 15 samples is ~4 subcarrier cycles at this decoder's 4x-fsc
    // input rate.
    private const int HoverWaveformSamplesBefore = 10;
    private const int HoverWaveformSamplesAfter = 4;

    // Pixel width shared by the line overview and every waveform row, so the
    // rows line up in a column.
    private const float HoverWaveformWidth = 360f;
    private const float HoverOverviewHeight = 40f;

    // Below this I/Q magnitude (on the 0-255 black-to-white scale) a sample
    // has no meaningful hue - the angle of a near-zero vector is noise - so
    // the hue bar shows no marker for it.
    private const double MinHueSaturation = 2.0;

    // Fixed y ranges for the stage rows, so the same signal level always
    // draws at the same height from one hover to the next (an auto-ranged
    // row would hide exactly the amplitude differences between samples this
    // view exists to show). Raw is the full byte range of the composite
    // input; chroma/I/Q are signed and symmetric on the decoder's 0-255
    // black-to-white scale. A chroma excursion past the latter just clips at
    // the row's edge - the decoder doesn't clamp chroma to the legal gamut
    // (see ChromaDecoder), so this is possible but atypical.
    private const double RawRange = 255.0;
    private const double ChromaRange = 128.0;

    // Pixel budget of the magnified raster neighborhood at the top of the
    // hover tooltip (DrawHoveredSampleMagnifier). Width matches the waveform
    // plot below it; height is kept small because a tooltip taller than the
    // screen can't be scrolled (it follows the mouse). The row count (odd, so
    // the hovered sample sits centered) sets how much of the raster is shown:
    // more rows means smaller cells. The column count is derived from the
    // picture's aspect - see that method.
    private const float HoverMagnifierWidth = 360f;
    private const float HoverMagnifierHeight = 150f;
    private const int HoverMagnifierRows = 9;

    // How many interpolated points to draw per raw-sample interval for the
    // reference sine overlays (Carrier/I/Q below) - the raw signal itself is
    // drawn as a stair-step (see DrawAnalogTrace's remarks on why that's the
    // faithful rendering of a genuinely discrete signal), but these three
    // are reconstructions of a continuous underlying sinusoid, and look like
    // one only if drawn with more resolution than the 4-samples/cycle raw
    // data has.
    private const int HoverWaveformSubdivisionsPerSample = 12;

    // Fallback amplitude for the Carrier reference sine when the hovered
    // sample has no real chroma to size it off of (sqrt(I^2+Q^2) is ~0 for
    // sync/blanking/grayscale content) - just big enough that the reference
    // phase is still visibly a sine rather than a flat line.
    private const double NominalCarrierAmplitude = 10.0;

    private readonly Television _television;

    // The plain texture-upload + aspect-correct blit - the part EmulationWindow
    // shares with this class. This class only adds the overlays on top of it.
    private readonly TelevisionTextureView _textureView;

    // Saleae-style toggle: crop out sync/blanking/color burst entirely and
    // show just the picture, the same view this window always showed before
    // the region-overlay/crosshair niceties were added. Defaults on so
    // opening this window looks the same as it always did.
    // Independent of _showRegionOverlay below - a checked region can still
    // be interesting to see even while cropped (e.g. a VSYNC-classified
    // sample can land inside what would otherwise read as the active-video
    // column range - see SyncSeparator.CurrentSyncRegion's remarks on
    // why a long sync pulse suppresses normal per-line column wraparound -
    // so cropping doesn't make the overlay meaningless the way it might seem
    // to at first).
    private bool _activeVideoOnly = true;

    // Saleae-style toggle: translucent bands over the HSYNC/color-burst/
    // blanking/VSYNC parts of the raster - see DrawRegionOverlays. Off by
    // default (opt-in diagnostic), independent of _activeVideoOnly above.
    private bool _showRegionOverlay;

    // Toggle for the crosshair at Television.CurrentColumn/CurrentRow - see
    // DrawDotPositionMarker. Off by default: it moves every frame while the
    // debugger runs, which reads as distracting noise more often than it's
    // actually being consulted - an opt-in diagnostic like _showRegionOverlay,
    // not something that should occupy the picture by default.
    private bool _showPositionMarker;

    public override string DisplayName => "Television";

    public override Pane PreferredPane => Pane.Center;

    // Takes the whole Television instance, not just its pixels - the
    // dot-position/region overlays need CurrentColumn/CurrentRow/
    // IsActiveVideo from the live decoder.
    public TelevisionWindow(Television television)
    {
        _television = television;
        _textureView = new TelevisionTextureView(television);

        // This window's per-sample hover tooltip is the only consumer of the
        // Sample diagnostic fields (Region/RawSample/CarrierPhaseRadians/
        // Luma/I/Q), which Television skips populating unless asked - see
        // Television.CaptureSampleDiagnostics. The window object outlives any
        // one open/close, so this is left on for the process's lifetime
        // rather than toggled with visibility.
        _television.CaptureSampleDiagnostics = true;
    }

    private SampleBuffer SampleBuffer => _television.SampleBuffer;

    // The overlay/tooltip code below maps texture-space (column, row) onto
    // screen pixels, so it needs the same texture dimensions the shared view
    // uploaded this frame.
    private uint TextureWidth => _textureView.TextureWidth;
    private uint TextureHeight => _textureView.TextureHeight;

    public override void CreateGraphicsResources(SDLGPUDevicePtr graphicsDevice)
    {
        base.CreateGraphicsResources(graphicsDevice);

        _textureView.CreateGraphicsResources(graphicsDevice);
    }

    protected override void PrepareOverride(EmulatorTime time, SDLGPUCommandBufferPtr commandBuffer)
    {
        _textureView.Prepare(commandBuffer);
    }

    // Fixed sidebar width (controls + status readout + legend), scaled by
    // font size rather than a raw pixel count so it stays proportional
    // across different UI scales - the same reasoning LogicAnalyzerWindow's
    // labelColumnWidth uses.
    private float SidebarWidth => ImGui.GetFontSize() * 15f;

    protected override void DrawOverride(EmulatorTime time)
    {
        // Left: the image itself (plus its overlays, drawn on top). Right:
        // controls/status/legend, stacked vertically - see DrawSidebar. A
        // negative child size is ImGui's own idiom for "fill everything
        // except the last N pixels", which is what leaves exactly
        // SidebarWidth free for the second child below.
        ImGui.BeginChild("##image"u8, new Vector2(-SidebarWidth, 0f));
        DrawImageAndOverlays();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##sidebar"u8, Vector2.Zero, ImGuiChildFlags.Borders);
        DrawSidebar();
        ImGui.EndChild();
    }

    private void DrawImageAndOverlays()
    {
        // The picture itself, aspect-corrected and (with _activeVideoOnly)
        // cropped to just the active raster - the part shared with
        // EmulationWindow. "Active video only" shows exactly what this window
        // always showed before the region overlays were added; unchecked
        // shows the *whole* raster - sync, blanking, color burst, and
        // vertical blanking/VSYNC lines included. The returned placement is
        // what every overlay below needs to convert a texture-space
        // (column, row) into a screen-space pixel.
        var placement = _textureView.DrawImage(_activeVideoOnly);
        var imageMin = placement.ImageMin;
        var imageMax = placement.ImageMax;
        var uv0 = placement.Uv0;
        var uv1 = placement.Uv1;
        var drawList = ImGui.GetWindowDrawList();

        // Independent of the crop above (see _showRegionOverlay's remarks) -
        // restricted to whatever's actually visible right now via uv0/uv1,
        // so this never wastes time (or draws off-screen rects) for columns
        // the crop has hidden.
        if (_showRegionOverlay)
        {
            DrawRegionOverlays(drawList, imageMin, imageMax, uv0, uv1);
        }

        if (_showPositionMarker)
        {
            DrawDotPositionMarker(drawList, imageMin, imageMax, uv0, uv1);
        }

        DrawHoveredSampleTooltip(imageMin, imageMax, uv0, uv1);
    }

    // Saleae-style hover readout: whatever SampleBuffer position the mouse
    // is currently over (inverse of ColumnToScreenX/RowToScreenY's screen-
    // space mapping, further inverted back through uv0/uv1 to account for
    // the active-video crop - see DrawImageAndOverlays), shown as a tooltip
    // rather than drawn directly on the image so it doesn't obscure the very
    // sample it's describing. ImGui.IsItemHovered() here still refers to the
    // ImGui.Image call above - none of DrawRegionOverlays/DrawDotPositionMarker
    // create a new "last item", they only draw onto drawList directly.
    private void DrawHoveredSampleTooltip(Vector2 imageMin, Vector2 imageMax, Vector2 uv0, Vector2 uv1)
    {
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        var mousePos = ImGui.GetMousePos();
        var u = uv0.X + (mousePos.X - imageMin.X) / (imageMax.X - imageMin.X) * (uv1.X - uv0.X);
        var v = uv0.Y + (mousePos.Y - imageMin.Y) / (imageMax.Y - imageMin.Y) * (uv1.Y - uv0.Y);

        var column = (int)(u * TextureWidth);
        var row = (int)(v * TextureHeight);

        if (column < 0 || column >= TextureWidth || row < 0 || row >= TextureHeight)
        {
            return;
        }

        var samples = SampleBuffer.Data;

        // SampleBuffer can be resized again (by the emulation thread, live,
        // mid-Decode) any time after PrepareOverride last captured
        // _textureWidth/_textureHeight from it - if that's happened since,
        // Data is no longer width*height samples long, and indexing into it
        // with those now-stale dimensions would run past its end. A purely
        // transient, one-frame mismatch (PrepareOverride re-syncs
        // _textureWidth/_textureHeight from SampleBuffer's current size
        // every frame - see its own remarks) - simplest correct response is
        // just to skip this frame's read and let the next one pick it back
        // up once they're back in sync, rather than reading past the end.
        if (samples.Length != (int)TextureWidth * (int)TextureHeight)
        {
            return;
        }

        var index = row * (int)TextureWidth + column;
        var sample = samples[index];

        ImGui.BeginTooltip();

        DrawHoveredSampleMagnifier(samples, column, row);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Same swatch-then-label technique as DrawLegendEntry, just with a
        // hex readout as the label instead of a region name.
        var swatchSize = ImGui.GetTextLineHeight();
        var drawList = ImGui.GetWindowDrawList();
        var cursorScreenPos = ImGui.GetCursorScreenPos();

        drawList.AddRectFilled(
            cursorScreenPos,
            new Vector2(cursorScreenPos.X + swatchSize, cursorScreenPos.Y + swatchSize),
            ImGui.GetColorU32(new Vector4(sample.Color.R / 255f, sample.Color.G / 255f, sample.Color.B / 255f, 1f)));

        ImGui.SetCursorScreenPos(new Vector2(cursorScreenPos.X + swatchSize + ImGui.GetStyle().ItemSpacing.X, cursorScreenPos.Y));
        ImGui.TextUnformatted($"#{sample.Color.R:X2}{sample.Color.G:X2}{sample.Color.B:X2}");

        ImGui.TextWrapped($"Scanline: {row}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawHoveredSampleWaveform(samples, row, column);

        ImGui.EndTooltip();
    }

    // Zoomed-in view of the raster around the hovered sample: one flat-
    // colored cell per SampleBuffer entry (nearest-neighbor, no filtering),
    // so what's shown is exactly the decoded samples the main image's
    // (bilinear-filtered, rescaled) screen pixels are blended from. The
    // hovered sample gets a border. Cells are taller than wide by the same
    // factor DrawImage stretches rows by, so the grid has the aspect of the
    // picture being hovered; neighbors off the edge of the raster are left
    // as the tooltip's own background. The grid is HoverMagnifierRows tall in
    // HoverMagnifierHeight pixels; the cell width follows from that height
    // and the stretch factor, and as many (odd) columns as fit in
    // HoverMagnifierWidth are shown.
    private void DrawHoveredSampleMagnifier(ReadOnlySpan<Sample> samples, int centerColumn, int centerRow)
    {
        var width = (int)TextureWidth;
        var height = (int)TextureHeight;

        var (_, activeRowCount) = _television.ComputeActiveVideoRowRange();
        var stretch = activeRowCount > 0 ? _television.ComputeVerticalStretchFactor(activeRowCount) : 1f;

        var cellHeight = HoverMagnifierHeight / HoverMagnifierRows;
        var cellWidth = cellHeight / stretch;
        var columns = Math.Max(1, (int)(HoverMagnifierWidth / cellWidth)) | 1;
        var gridSize = new Vector2(columns * cellWidth, HoverMagnifierHeight);

        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        for (var gridRow = 0; gridRow < HoverMagnifierRows; gridRow++)
        {
            var sampleRow = centerRow + gridRow - HoverMagnifierRows / 2;
            if (sampleRow < 0 || sampleRow >= height)
            {
                continue;
            }

            for (var gridColumn = 0; gridColumn < columns; gridColumn++)
            {
                var sampleColumn = centerColumn + gridColumn - columns / 2;
                if (sampleColumn < 0 || sampleColumn >= width)
                {
                    continue;
                }

                var color = samples[sampleRow * width + sampleColumn].Color;
                var min = new Vector2(origin.X + gridColumn * cellWidth, origin.Y + gridRow * cellHeight);
                drawList.AddRectFilled(
                    min,
                    new Vector2(min.X + cellWidth, min.Y + cellHeight),
                    ImGui.GetColorU32(new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, 1f)));
            }
        }

        // Two-tone (dark outside, white inside) so the border reads against
        // any cell color.
        var hoveredMin = new Vector2(
            origin.X + (columns / 2) * cellWidth,
            origin.Y + (HoverMagnifierRows / 2) * cellHeight);
        var hoveredMax = new Vector2(hoveredMin.X + cellWidth, hoveredMin.Y + cellHeight);
        drawList.AddRect(hoveredMin, hoveredMax, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 1f)), 0f, ImDrawFlags.None, 3f);
        drawList.AddRect(hoveredMin, hoveredMax, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), 0f, ImDrawFlags.None, 1.5f);

        ImGui.Dummy(gridSize);
    }

    // How the hovered sample's color came out of the raw signal, as a stack
    // of rows sharing one x axis (one column per raster sample), one decoder
    // stage per row, top to bottom:
    //
    //   Raw     - the composite signal as received.
    //   Luma    - the 1:2:1 comb filter's output, brightness only.
    //   Chroma  - raw minus luma: the leftover subcarrier wiggle. The thin
    //             cyan sine is the color-burst-locked reference: a chroma
    //             wiggle in phase with it is the burst's own hue, and its
    //             phase shift from it is what sets this sample's hue.
    //   I / Q   - the chroma demodulated against that reference (hue and
    //             saturation as two numbers).
    //   Color   - the decoded RGB each of those becomes.
    //
    // Above the stack, a whole-scanline overview (sync, burst, picture) with
    // a marker showing where the detail rows sit on the line.
    //
    // Reads neighboring SampleBuffer entries as a de facto rolling log of the
    // decoder's per-sample outputs (see Sample's own remarks) rather than
    // keeping a separate capture buffer - consecutive raster positions are
    // consecutive Television.Decode calls.
    private void DrawHoveredSampleWaveform(ReadOnlySpan<Sample> samples, int row, int column)
    {
        var width = (int)TextureWidth;
        var line = samples.Slice(row * width, width);
        var center = line[column];

        // Rows butt up closer together than ImGui's default spacing so the
        // stack reads as one figure.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 4f));

        DrawLineOverview(line, column);

        var first = Math.Max(0, column - HoverWaveformSamplesBefore);
        var last = Math.Min(width - 1, column + HoverWaveformSamplesAfter);
        var count = last - first + 1;

        Span<double> raw = stackalloc double[count];
        Span<double> luma = stackalloc double[count];
        Span<double> chroma = stackalloc double[count];
        Span<double> iValues = stackalloc double[count];
        Span<double> qValues = stackalloc double[count];
        Span<double> phases = stackalloc double[count];

        for (var k = 0; k < count; k++)
        {
            var s = line[first + k];
            raw[k] = s.RawSample;
            luma[k] = s.Luma;
            chroma[k] = s.Chroma;
            iValues[k] = s.I;
            qValues[k] = s.Q;
            phases[k] = s.CarrierPhaseRadians;
        }

        // x is measured in samples relative to the hovered one.
        var xMin = first - column - 0.5;
        var xMax = last - column + 0.5;

        var (hue, saturation) = HueAndSaturation(center);

        if (BeginWaveformRow("##raw"u8, 56f, xMin, xMax, 0, RawRange, new RowLabel($"Raw {center.RawSample}", RawSignalColor)))
        {
            DrawHoveredSampleBand(0, RawRange);
            PlotSteps("Raw"u8, RawSignalColor, raw, first - column);
            EndWaveformRow();
        }

        // Luma is on the fixed 0-255 black-to-white scale (not auto-ranged)
        // so its level reads as an absolute brightness.
        if (BeginWaveformRow("##luma"u8, 28f, xMin, xMax, 0, 255, new RowLabel($"Luma {center.Luma:0}", LumaColor)))
        {
            DrawHoveredSampleBand(0, 255);
            PlotSteps("Luma"u8, LumaColor, luma, first - column);
            EndWaveformRow();
        }

        if (BeginWaveformRow(
            "##chroma"u8, 48f, xMin, xMax, -ChromaRange, ChromaRange,
            new RowLabel("Chroma", ChromaColor),
            new RowLabel($"Sat {saturation:0}", ChromaColor),
            new RowLabel("Burst ref", CarrierColor)))
        {
            DrawHoveredSampleBand(-ChromaRange, ChromaRange);
            DrawBurstReference(phases, center, ChromaRange, first - column);
            PlotSteps("Chroma"u8, ChromaColor, chroma, first - column);
            EndWaveformRow();
        }

        if (BeginWaveformRow(
            "##iq"u8, 48f, xMin, xMax, -ChromaRange, ChromaRange,
            new RowLabel($"I {center.I:0.#}", IComponentColor),
            new RowLabel($"Q {center.Q:0.#}", QComponentColor)))
        {
            DrawHoveredSampleBand(-ChromaRange, ChromaRange);
            PlotSteps("I"u8, IComponentColor, iValues, first - column);
            PlotSteps("Q"u8, QComponentColor, qValues, first - column);
            EndWaveformRow();
        }

        if (BeginWaveformRow("##color"u8, 20f, xMin, xMax, 0, 1, new RowLabel("Color", ImGui.GetColorU32(ImGuiCol.Text))))
        {
            DrawColorStrip(line, first, count, column);
            EndWaveformRow();
        }

        DrawHueBar(hue, saturation);

        ImGui.PopStyleVar();
    }

    // The whole hovered scanline at a glance: raw signal as a min/max
    // envelope per screen pixel (a scanline is ~900 samples, more than there
    // are pixels to draw them in, and the 4-samples-per-cycle carrier is
    // unreadable at this scale - the envelope shows chroma as a thickened
    // band instead), over translucent bands for the non-picture regions
    // (HSYNC and the color burst labeled, since those are the parts a
    // reader most needs to recognize), plus a box marking the window the
    // detail rows below show.
    private static void DrawLineOverview(ReadOnlySpan<Sample> line, int column)
    {
        var width = line.Length;
        var size = new Vector2(GraphWidth, HoverOverviewHeight);
        BeginRowChrome(HoverOverviewHeight, new RowLabel("Line", ImGui.GetColorU32(ImGuiCol.Text)));
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        var pixelsPerSample = size.X / width;

        var runStart = 0;
        while (runStart < width)
        {
            var region = line[runStart].Region;
            var runEnd = runStart + 1;
            while (runEnd < width && line[runEnd].Region == region)
            {
                runEnd++;
            }

            if (region != RasterRegion.ActiveVideo)
            {
                var x0 = min.X + runStart * pixelsPerSample;
                var x1 = min.X + runEnd * pixelsPerSample;
                drawList.AddRectFilled(new Vector2(x0, min.Y), new Vector2(x1, max.Y), ImGui.GetColorU32(RegionOverlayColor(region)));

                var label = region switch
                {
                    RasterRegion.HSync => "HSYNC",
                    RasterRegion.ColorBurst => "burst",
                    _ => null,
                };
                if (label != null && ImGui.CalcTextSize(label).X <= x1 - x0)
                {
                    drawList.AddText(new Vector2(x0 + 1f, min.Y), ImGui.GetColorU32(ImGuiCol.Text), label);
                }
            }

            runStart = runEnd;
        }

        var signalColor = ImGui.GetColorU32(RawSignalColor);
        for (var px = 0; px < (int)size.X; px++)
        {
            var c0 = Math.Min(width - 1, (int)(px / pixelsPerSample));
            var c1 = Math.Min(width, Math.Max(c0 + 1, (int)((px + 1) / pixelsPerSample)));

            byte lo = 255, hi = 0;
            for (var c = c0; c < c1; c++)
            {
                lo = Math.Min(lo, line[c].RawSample);
                hi = Math.Max(hi, line[c].RawSample);
            }

            var x = min.X + px + 0.5f;
            drawList.AddLine(
                new Vector2(x, max.Y - hi / 255f * size.Y),
                new Vector2(x, max.Y - lo / 255f * size.Y - 1f),
                signalColor);
        }

        var windowMinX = min.X + Math.Max(0, column - HoverWaveformSamplesBefore) * pixelsPerSample;
        var windowMaxX = Math.Max(
            windowMinX + 3f,
            min.X + Math.Min(width, column + HoverWaveformSamplesAfter + 1) * pixelsPerSample);
        drawList.AddRect(new Vector2(windowMinX, min.Y), new Vector2(windowMaxX, max.Y), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)));

        ImGui.Dummy(size);
    }

    // Width of the label column at the left of every stage row, and what's
    // left of the row's total width for the graph itself.
    private static float GutterWidth => ImGui.GetFontSize() * 5.5f;
    private static float GraphWidth => HoverWaveformWidth - GutterWidth;

    // The part of a stage row that isn't the graph: a faint panel behind the
    // whole row (so adjacent rows read as separate blocks), the row's label
    // (one or two lines, vertically centered) in a gutter at the left, and a
    // divider between gutter and graph. Leaves the cursor at the graph's
    // top-left, ready for the graph to be drawn there.
    private readonly record struct RowLabel(string Text, uint Color)
    {
        public RowLabel(string text, Vector4 color)
            : this(text, ImGui.GetColorU32(color))
        {
        }
    }

    private static void BeginRowChrome(float height, RowLabel line1, RowLabel? line2 = null, RowLabel? line3 = null)
    {
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var gutter = GutterWidth;

        drawList.AddRectFilled(start, start + new Vector2(HoverWaveformWidth, height), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.06f)));

        var lineHeight = ImGui.GetTextLineHeight();
        var lineCount = line3 != null ? 3 : line2 != null ? 2 : 1;
        var textPos = new Vector2(start.X + 4f, start.Y + (height - lineHeight * lineCount) * 0.5f);
        drawList.AddText(textPos, line1.Color, line1.Text);
        if (line2 is { } second)
        {
            drawList.AddText(textPos + new Vector2(0f, lineHeight), second.Color, second.Text);
        }

        if (line3 is { } third)
        {
            drawList.AddText(textPos + new Vector2(0f, lineHeight * 2f), third.Color, third.Text);
        }

        drawList.AddLine(
            new Vector2(start.X + gutter - 1f, start.Y),
            new Vector2(start.X + gutter - 1f, start.Y + height),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.25f)));

        ImGui.SetCursorScreenPos(start + new Vector2(gutter, 0f));
    }

    // The hovered sample's hue on a strip of every hue (the I/Q plane's angle
    // around the circle, at a fixed luma and saturation), so you can see
    // which hue the demodulated chroma landed on. No marker for a sample with
    // no meaningful hue (see MinHueSaturation).
    private static void DrawHueBar(double hue, double saturation)
    {
        const float height = 16f;
        const int segments = 72;
        const float luma = 150f;
        const float chroma = 70f;

        BeginRowChrome(height, new RowLabel(
            saturation < MinHueSaturation ? "Hue none" : $"Hue {hue * 180 / Math.PI:0}\u00b0",
            ImGui.GetColorU32(ImGuiCol.Text)));

        var min = ImGui.GetCursorScreenPos();
        var width = GraphWidth;
        var drawList = ImGui.GetWindowDrawList();

        for (var segment = 0; segment < segments; segment++)
        {
            var angle = (segment + 0.5) / segments * 2 * Math.PI;
            var i = chroma * Math.Cos(angle);
            var q = chroma * Math.Sin(angle);

            // The same YIQ -> RGB matrix ChromaDecoder uses.
            var r = Math.Clamp(luma + 0.956 * i + 0.621 * q, 0, 255) / 255;
            var g = Math.Clamp(luma - 0.272 * i - 0.647 * q, 0, 255) / 255;
            var b = Math.Clamp(luma - 1.106 * i + 1.703 * q, 0, 255) / 255;

            drawList.AddRectFilled(
                new Vector2(min.X + width * segment / segments, min.Y),
                new Vector2(min.X + width * (segment + 1) / segments, min.Y + height),
                ImGui.GetColorU32(new Vector4((float)r, (float)g, (float)b, 1f)));
        }

        if (saturation >= MinHueSaturation)
        {
            var x = min.X + (float)(hue / (2 * Math.PI)) * width;
            drawList.AddLine(new Vector2(x, min.Y), new Vector2(x, min.Y + height), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 1f)), 4f);
            drawList.AddLine(new Vector2(x, min.Y), new Vector2(x, min.Y + height), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), 2f);
        }

        ImGui.Dummy(new Vector2(width, height));
    }

    // One decoder-stage row: a chrome-less plot with a fixed x/y range, so
    // every row shares the same pixel geometry and the hovered sample lines
    // up vertically across all of them. ImPlot's own frame (padding plus
    // FrameBg around the plot area) is stripped so the rows butt up against
    // each other and the whole height is plot. Pair with EndWaveformRow.
    private static bool BeginWaveformRow(
        ReadOnlySpan<byte> id,
        float height,
        double xMin,
        double xMax,
        double yMin,
        double yMax,
        RowLabel line1,
        RowLabel? line2 = null,
        RowLabel? line3 = null)
    {
        BeginRowChrome(height, line1, line2, line3);

        ImPlot.PushStyleVar(ImPlotStyleVar.Padding, Vector2.Zero);
        ImPlot.PushStyleVar(ImPlotStyleVar.BorderSize, 0f);
        ImPlot.PushStyleColor(ImPlotCol.FrameBg, Vector4.Zero);
        ImPlot.PushStyleColor(ImPlotCol.Bg, Vector4.Zero);

        if (!ImPlot.BeginPlot(id, new Vector2(GraphWidth, height), ImPlotFlags.CanvasOnly | ImPlotFlags.NoInputs))
        {
            ImPlot.PopStyleColor(2);
            ImPlot.PopStyleVar(2);
            return false;
        }

        ImPlot.SetupAxes("", "", ImPlotAxisFlags.NoDecorations, ImPlotAxisFlags.NoDecorations);
        ImPlot.SetupAxisLimits(ImAxis.X1, xMin, xMax, ImPlotCond.Always);
        ImPlot.SetupAxisLimits(ImAxis.Y1, yMin, yMax, ImPlotCond.Always);
        return true;
    }

    private static void EndWaveformRow()
    {
        ImPlot.EndPlot();
        ImPlot.PopStyleColor(2);
        ImPlot.PopStyleVar(2);
    }

    // Soft band marking exactly which sample is "the" hovered one - drawn
    // before the traces so they render on top of it.
    private static void DrawHoveredSampleBand(double yMin, double yMax)
    {
        ImPlot.GetPlotDrawList().AddRectFilled(
            ImPlot.PlotToPixels(-0.5, yMax),
            ImPlot.PlotToPixels(0.5, yMin),
            ImGui.GetColorU32(CurrentSampleBandColor));
    }

    // Stair-stepped, like LogicAnalyzerWindow's own Analog trace - every
    // value here is a discrete, one-per-sample quantity, so a step trace is
    // the faithful rendering. Each value is held across the sample's own
    // [-0.5, +0.5) cell (ImPlot's stairs hold each point to the *right* of
    // its x), which is what makes the hovered-sample band line up with it.
    private static unsafe void PlotSteps(ReadOnlySpan<byte> label, Vector4 color, ReadOnlySpan<double> values, int firstRelativeX)
    {
        Span<double> xs = stackalloc double[values.Length + 1];
        Span<double> ys = stackalloc double[values.Length + 1];
        for (var k = 0; k < values.Length; k++)
        {
            xs[k] = firstRelativeX + k - 0.5;
            ys[k] = values[k];
        }

        xs[values.Length] = firstRelativeX + values.Length - 0.5;
        ys[values.Length] = values[values.Length - 1];

        fixed (double* xPtr = xs)
        fixed (double* yPtr = ys)
        {
            ImPlot.PushStyleColor(ImPlotCol.Line, color);
            ImPlot.PlotStairs(label, xPtr, yPtr, xs.Length);
            ImPlot.PopStyleColor();
        }
    }

    // The color-burst-locked reference sine drawn behind the chroma trace:
    // the PLL's own recovered subcarrier phase (see
    // ColorBurstPll.CurrentPhaseRadians), i.e. "what the burst itself
    // looks like right now", continuing through this part of the line. Sized
    // to the hovered sample's chroma amplitude so the two are directly
    // comparable (a chroma wiggle in step with it is the burst's hue; one
    // shifted along x is a different hue).
    private static unsafe void DrawBurstReference(ReadOnlySpan<double> phases, Sample center, double yRange, int firstRelativeX)
    {
        var count = phases.Length;
        if (count < 2)
        {
            return;
        }

        var chromaAmplitude = Math.Sqrt(center.I * center.I + center.Q * center.Q);
        var amplitude = Math.Min(yRange * 0.9, chromaAmplitude > 1e-6 ? chromaAmplitude : NominalCarrierAmplitude);

        var fineCount = (count - 1) * HoverWaveformSubdivisionsPerSample + 1;
        Span<double> fineX = stackalloc double[fineCount];
        Span<double> fineY = stackalloc double[fineCount];
        for (var m = 0; m < fineCount; m++)
        {
            var kf = (double)m / HoverWaveformSubdivisionsPerSample;
            var k = Math.Min((int)kf, count - 2);

            // Fixed 90-degrees-per-real-sample slope (the 4x-fsc input
            // contract), anchored at each stored discrete phase rather than
            // lerping between the two stored values directly - that would
            // need unwrapping across any line-boundary phase-offset nudge.
            fineX[m] = firstRelativeX + kf;
            fineY[m] = amplitude * Math.Cos(phases[k] + (kf - k) * (Math.PI / 2.0));
        }

        fixed (double* xPtr = fineX)
        fixed (double* yPtr = fineY)
        {
            ImPlot.PushStyleColor(ImPlotCol.Line, CarrierColor with { W = 0.7f });
            ImPlot.PlotLine("Burst reference"u8, xPtr, yPtr, fineCount);
            ImPlot.PopStyleColor();
        }
    }

    // Each sample's decoded color as one cell, in the same x columns as the
    // rows above, with the same two-tone hovered-sample border the
    // magnifier uses - the end of the pipeline the rows above lead to.
    private static void DrawColorStrip(ReadOnlySpan<Sample> line, int first, int count, int column)
    {
        var drawList = ImPlot.GetPlotDrawList();
        ImPlot.PushPlotClipRect();

        for (var k = 0; k < count; k++)
        {
            var x = first - column + k;
            var color = line[first + k].Color;
            drawList.AddRectFilled(
                ImPlot.PlotToPixels(x - 0.5, 1),
                ImPlot.PlotToPixels(x + 0.5, 0),
                ImGui.GetColorU32(new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, 1f)));
        }

        var hoveredMin = ImPlot.PlotToPixels(-0.5, 1);
        var hoveredMax = ImPlot.PlotToPixels(0.5, 0);
        drawList.AddRect(hoveredMin, hoveredMax, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 1f)), 0f, ImDrawFlags.None, 3f);
        drawList.AddRect(hoveredMin, hoveredMax, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), 0f, ImDrawFlags.None, 1.5f);

        ImPlot.PopPlotClipRect();
    }

    // Hue as the angle of the demodulated (I, Q) pair measured from the +I
    // axis, saturation as its length - the polar form of the same two
    // numbers the I and Q rows plot.
    private static (double HueRadians, double Saturation) HueAndSaturation(Sample sample)
    {
        var hue = Math.Atan2(sample.Q, sample.I);
        if (hue < 0)
        {
            hue += 2 * Math.PI;
        }

        return (hue, Math.Sqrt(sample.I * sample.I + sample.Q * sample.Q));
    }

    // Sidebar contents, stacked vertically: the two toggles, a status
    // readout (the raster oscillators' current period estimates - see
    // RasterOscillators - and whether the color-burst PLL found a real
    // burst on the most recently completed line, see ColorBurstPll - a
    // quick "is this decoding a sane, in-lock signal" glance, the same
    // spirit as LogicAnalyzerWindow's own zoom readout), and the region
    // overlay's color legend, shown only while that overlay actually has
    // something on screen to explain.
    private void DrawSidebar()
    {
        ImGui.Checkbox("Active video only"u8, ref _activeVideoOnly);
        ImGui.Checkbox("Region overlay"u8, ref _showRegionOverlay);
        ImGui.Checkbox("Position marker"u8, ref _showPositionMarker);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped($"Samples/line: {_television.DetectedSamplesPerLine:0.#}");
        ImGui.TextWrapped($"Lines/frame: {_television.DetectedLinesPerFrame:0.#}");
        ImGui.TextWrapped($"Color burst: {(_television.ColorBurstLocked ? "locked" : "not detected")}");

        // The same raster position DrawDotPositionMarker's crosshair is
        // drawn at, spelled out as text - CurrentRow is the scanline,
        // CurrentColumn the sample's horizontal position within it (both
        // already exposed by Television for exactly this kind of readout,
        // rather than something this window would need to derive itself).
        ImGui.TextWrapped($"Scanline: {_television.CurrentRow}");
        ImGui.TextWrapped($"Horizontal position: {_television.CurrentColumn}");

        if (_showRegionOverlay)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            DrawRegionOverlayLegend();
        }
    }

    // One color swatch (drawn directly, the same technique
    // LogicAnalyzerWindow's own channel color bar uses, rather than any
    // built-in ImGui "swatch" widget) plus label per non-ActiveVideo
    // RasterRegion. Swatches are drawn fully opaque, unlike the overlay
    // itself (see HSyncOverlayColor's remarks on why *that's* translucent) -
    // a legend key needs to read clearly regardless of what's behind it.
    private void DrawRegionOverlayLegend()
    {
        ImGui.TextUnformatted("Legend"u8);

        DrawLegendEntry("HSYNC", HSyncOverlayColor);
        DrawLegendEntry("Color burst", ColorBurstOverlayColor);
        DrawLegendEntry("Blanking", BlankingOverlayColor);
        DrawLegendEntry("VSYNC", VSyncOverlayColor);
    }

    private static void DrawLegendEntry(string label, Vector4 color)
    {
        var swatchSize = ImGui.GetTextLineHeight();

        var drawList = ImGui.GetWindowDrawList();
        var cursorScreenPos = ImGui.GetCursorScreenPos();

        drawList.AddRectFilled(
            cursorScreenPos,
            new Vector2(cursorScreenPos.X + swatchSize, cursorScreenPos.Y + swatchSize),
            ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 1f)));

        ImGui.SetCursorScreenPos(new Vector2(cursorScreenPos.X + swatchSize + ImGui.GetStyle().ItemSpacing.X, cursorScreenPos.Y));
        ImGui.TextUnformatted(label);
    }

    // Colored, translucent bands over the HSYNC/color-burst/blanking/VSYNC
    // parts of the raster (see RasterRegion), so a reader can see at a
    // glance where in the signal each part of the image comes from - the
    // same idea as a logic analyzer labeling regions of a waveform.
    //
    // Reads each position's Region straight out of SampleBuffer - the same
    // value Television.Decode stored there from the pipeline's own live
    // classification (see Television.ClassifyCurrentSample's remarks) -
    // rather than this class (or anything else) re-deriving it from NTSC's
    // timing constants - see RasterRegion's own remarks on why an earlier,
    // nominal-timing-based version of this was replaced.
    //
    // Scans every row (not one "representative" row standing in for all of
    // them, the way an earlier version of this did) because VSYNC genuinely
    // doesn't behave like the other four regions: HSYNC/color-burst/
    // blanking/active-video repeat at the same column range on every normal
    // line, but a VSYNC pulse suppresses the horizontal oscillator's normal
    // per-line column wraparound entirely (no HSYNC edges occur for it to
    // lock onto while the pulse is happening - see
    // SyncSeparator.CurrentSyncRegion's remarks), so the columns a VSYNC
    // pulse actually gets written at are wherever the oscillator's own
    // free-run happened to be, not any fixed, predictable range. An earlier
    // version of this special-cased VSYNC by checking only column 0 of each
    // row on the assumption a VSYNC pulse spans a whole row's width - it
    // doesn't, and that missed real VSYNC pulses entirely depending on where
    // column 0 happened to land relative to them. Scanning every column of
    // every row is the fix: more samples read (a quarter-million or so, for
    // NTSC) but still trivial next to one UI frame's budget, and it can't
    // miss a real pulse regardless of where it landed.
    private void DrawRegionOverlays(ImDrawListPtr drawList, Vector2 imageMin, Vector2 imageMax, Vector2 uv0, Vector2 uv1)
    {
        var width = (int)TextureWidth;
        var height = (int)TextureHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var samples = SampleBuffer.Data;

        // Same transient staleness guard as DrawHoveredSampleTooltip's own
        // remarks - SampleBuffer may have been resized again since
        // PrepareOverride last synced _textureWidth/_textureHeight from it.
        if (samples.Length != width * height)
        {
            return;
        }

        // Restricted to whatever's actually visible through the current
        // crop (see DrawImageAndOverlays) - no point reading, let alone
        // drawing, samples the crop has already hidden.
        var columnStart = Math.Clamp((int)Math.Floor(uv0.X * width), 0, width);
        var columnEnd = Math.Clamp((int)Math.Ceiling(uv1.X * width), 0, width);
        var rowStart = Math.Clamp((int)Math.Floor(uv0.Y * height), 0, height);
        var rowEnd = Math.Clamp((int)Math.Ceiling(uv1.Y * height), 0, height);

        float ColumnToScreenX(int column) =>
            imageMin.X + ((float)column / width - uv0.X) / (uv1.X - uv0.X) * (imageMax.X - imageMin.X);

        float RowToScreenY(int row) =>
            imageMin.Y + ((float)row / height - uv0.Y) / (uv1.Y - uv0.Y) * (imageMax.Y - imageMin.Y);

        for (var row = rowStart; row < rowEnd; row++)
        {
            var rowOffset = row * width;
            var column = columnStart;

            while (column < columnEnd)
            {
                var region = samples[rowOffset + column].Region;

                var runStart = column;
                do
                {
                    column++;
                }
                while (column < columnEnd && samples[rowOffset + column].Region == region);

                if (region != RasterRegion.ActiveVideo)
                {
                    var x0 = ColumnToScreenX(runStart);
                    var x1 = ColumnToScreenX(column);
                    var y0 = RowToScreenY(row);
                    var y1 = RowToScreenY(row + 1);
                    drawList.AddRectFilled(new Vector2(x0, y0), new Vector2(x1, y1), ImGui.GetColorU32(RegionOverlayColor(region)));
                }
            }
        }
    }

    private static Vector4 RegionOverlayColor(RasterRegion region) => region switch
    {
        RasterRegion.HSync => HSyncOverlayColor,
        RasterRegion.ColorBurst => ColorBurstOverlayColor,
        RasterRegion.VSync => VSyncOverlayColor,
        _ => BlankingOverlayColor,
    };

    // Saleae-style crosshair at Television.CurrentColumn/CurrentRow - the
    // exact raster position the decoder just produced a pixel for, updated
    // live every UI frame.
    private void DrawDotPositionMarker(ImDrawListPtr drawList, Vector2 imageMin, Vector2 imageMax, Vector2 uv0, Vector2 uv1)
    {
        var u = _television.CurrentColumn / (float)TextureWidth;
        var v = _television.CurrentRow / (float)TextureHeight;

        // The current position only has somewhere to draw if it falls within
        // whatever's currently on screen - e.g. while "Active video only" is
        // checked, the decoder spends most of its time on sync/blanking
        // samples that simply aren't part of the cropped view.
        if (u < uv0.X || u > uv1.X || v < uv0.Y || v > uv1.Y)
        {
            return;
        }

        var screenX = imageMin.X + (u - uv0.X) / (uv1.X - uv0.X) * (imageMax.X - imageMin.X);
        var screenY = imageMin.Y + (v - uv0.Y) / (uv1.Y - uv0.Y) * (imageMax.Y - imageMin.Y);

        const float Radius = 6f;
        var color = ImGui.GetColorU32(ImGuiCol.Text);

        drawList.AddLine(new Vector2(screenX - Radius, screenY), new Vector2(screenX + Radius, screenY), color, 1.5f);
        drawList.AddLine(new Vector2(screenX, screenY - Radius), new Vector2(screenX, screenY + Radius), color, 1.5f);
        drawList.AddCircle(new Vector2(screenX, screenY), Radius, color, 0, 1.5f);
    }

    public override void Dispose()
    {
        base.Dispose();

        _textureView.Dispose();
    }
}
