using System;
using System.Numerics;
using Aemula.Emulation.Output;
using Aemula.Emulation.Output.Ntsc;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;
using Hexa.NET.SDL3;

namespace Aemula.UI;

// This namespace nests under the root Aemula namespace, where the older,
// unrelated Aemula.Television already lives (see docs/television-plan.md's
// "Naming collision, explicitly out of scope" note) - plain enclosing-
// namespace lookup would find that one first, ahead of a plain `using
// Aemula.Emulation.Output;` placed above the namespace declaration (which is
// compilation-unit-scoped, and loses to an ancestor namespace's own member -
// see TelevisionTests.cs's remarks on the same issue), so it's aliased here
// instead, inside the namespace body, where it actually takes priority.
using Television = Aemula.Emulation.Output.Television;

// Phases 5-7 of docs/television-plan.md: a basic texture-upload render
// (Phase 5), wired into a real system (Phase 6), plus the Saleae-style
// niceties (Phase 7) - a dot-position crosshair at Television.CurrentColumn/
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
// Same overall GPU-texture-upload *shape* as ScreenDisplayWindow (allocate
// a transfer buffer + texture, map/upload/copy each frame, draw via
// ImGui.Image, release on Dispose), but a deliberately independent
// implementation - no shared base class or composition with it. Per the
// plan doc's "TelevisionWindow" section, ScreenDisplayWindow is slated for
// removal once this class replaces it, so tying the two together now would
// just create a removal headache later for no benefit today.
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
    private static readonly Vector4 CurrentSampleBandColor = new(1f, 1f, 1f, 0.18f);

    // How many raw samples either side of the hovered one to show in its
    // waveform tooltip - see DrawHoveredSampleWaveform's remarks on why this
    // reads straight out of SampleBuffer rather than a dedicated capture
    // buffer. 6 either side is ~3 full subcarrier cycles (4 samples/cycle at
    // this decoder's 4x-fsc input rate) - enough to see the carrier's actual
    // shape, and comfortably wider than the 5-raw-sample window
    // NtscYiqDecoder's own comb filter/quadrature demod reach back over to
    // decode any one sample.
    private const int HoverWaveformRadius = 6;

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

    // Minimum I/Q axis range for the vectorscope (DrawHoveredSampleVectorscope) -
    // a fixed range (originally tried at the "legal" NTSC I/Q gamut, +-152/
    // +-133 on this decoder's 0-255 black-to-white scale, per the YIQ->RGB
    // matrix's own coefficients - see NtscYiqDecoder.Process) turned out
    // not to actually bound every real (I, Q) this decoder produces: unlike
    // a real receiver, nothing here clamps chroma to the legal gamut, and a
    // sharp luma edge (e.g. a text character's edge - exactly the kind of
    // thing that produces genuine NTSC composite-artifact color) can drive
    // the comb filter's chroma residual well past it, landing the point
    // outside a fixed-range plot's clip rect entirely - a hovered sample
    // whose color swatch is clearly showing *something* rendering as
    // nothing but a truncated line to the plot's edge. This is now only a
    // floor: DrawHoveredSampleVectorscope scales the range up per-hover to
    // whatever the actual sample needs, so the point is always visible -
    // the floor just keeps a near-zero-chroma sample (sync/blanking/gray)
    // from zooming in so far the plot looks like empty noise.
    private const double MinVectorscopeRange = 60.0;

    private readonly Television _television;

    private SDLGPUDevicePtr _graphicsDevice;
    private SDLGPUTransferBufferPtr _transferBuffer;
    private SDLGPUTexturePtr _texture;
    private ImTextureRef _textureBinding;

    private uint _textureWidth, _textureHeight;

    // The transfer buffer's own allocated size, tracked separately from
    // DisplayBuffer's *current* dimensions - see CreateGpuResourcesForCurrentSize's
    // remarks on why this can't just be recomputed live from DisplayBuffer
    // each frame the way _textureWidth/_textureHeight can.
    private uint _transferBufferSizeInBytes;

    // Saleae-style toggle: crop out sync/blanking/color burst entirely and
    // show just the picture, the same view Phase 5/6 always showed. Defaults
    // on so opening this window looks the same as it did before Phase 7.
    // Independent of _showRegionOverlay below - a checked region can still
    // be interesting to see even while cropped (e.g. a VSYNC-classified
    // sample can land inside what would otherwise read as the active-video
    // column range - see NtscSyncSeparator.CurrentSyncRegion's remarks on
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

    // Takes the whole Television instance, not just its DisplayBuffer
    // (unlike ScreenDisplayWindow) - the dot-position/region overlays added
    // in Phase 7 need CurrentColumn/CurrentRow/IsActiveVideo from the live
    // decoder, not just the pixels it already produced from them.
    public TelevisionWindow(Television television)
    {
        _television = television;
    }

    private SampleBuffer SampleBuffer => _television.SampleBuffer;

    public override void CreateGraphicsResources(SDLGPUDevicePtr graphicsDevice)
    {
        base.CreateGraphicsResources(graphicsDevice);

        _graphicsDevice = graphicsDevice;

        CreateGpuResourcesForCurrentSize();
    }

    // Allocates both the transfer buffer *and* the texture for
    // SampleBuffer's current dimensions, releasing whatever was there
    // before. Unlike ScreenDisplayWindow (which only ever recreates its
    // texture on a size change, and sizes its transfer buffer once, at
    // construction), this recreates *both* together: Television.Decode
    // resizes SampleBuffer in place whenever the raster oscillators'
    // detected line/frame timing changes (see Television.cs) - a normal,
    // expected occurrence for this decoder, not a rare edge case - and a
    // transfer buffer allocated for the old (typically smaller, nominal)
    // size would silently overflow once PrepareOverride below tries to
    // copy a larger SampleBuffer into it.
    private void CreateGpuResourcesForCurrentSize()
    {
        if (!_transferBuffer.IsNull)
        {
            SDL.ReleaseGPUTransferBuffer(_graphicsDevice, _transferBuffer);
        }

        if (!_texture.IsNull)
        {
            SDL.ReleaseGPUTexture(_graphicsDevice, _texture);
        }

        _textureWidth = SampleBuffer.Width;
        _textureHeight = SampleBuffer.Height;
        _transferBufferSizeInBytes = _textureWidth * _textureHeight * RgbaByte.SizeInBytes;

        _transferBuffer = SDL.CreateGPUTransferBuffer(
            _graphicsDevice,
            new SDLGPUTransferBufferCreateInfo(
                SDLGPUTransferBufferUsage.Upload,
                _transferBufferSizeInBytes));

        _texture = SDL.CreateGPUTexture(
            _graphicsDevice,
            new SDLGPUTextureCreateInfo
            {
                Type = SDLGPUTextureType.Texturetype2D,
                Format = SDLGPUTextureFormat.R8G8B8A8Unorm,
                Usage = (uint)SDLGPUTextureUsageFlags.Sampler,
                Width = _textureWidth,
                Height = _textureHeight,
                NumLevels = 1,
                SampleCount = SDLGPUSampleCount.Samplecount1,
                LayerCountOrDepth = 1,
            });

        unsafe
        {
            _textureBinding = new ImTextureRef(null, new ImTextureID(_texture));
        }
    }

    protected override void PrepareOverride(EmulatorTime time, SDLGPUCommandBufferPtr commandBuffer)
    {
        if (SampleBuffer.Width != _textureWidth || SampleBuffer.Height != _textureHeight)
        {
            CreateGpuResourcesForCurrentSize();
        }

        // A plain memcpy (what this did back when Television exposed a
        // DisplayBuffer of RgbaByte, one per pixel, laid out identically to
        // the GPU texture) no longer works now that each raster position is
        // a whole Sample (Color plus Region, and room to grow - see that
        // struct) - only Color is what the texture wants, so this copies
        // just that field out, one sample at a time.
        unsafe
        {
            var mapped = (RgbaByte*)SDL.MapGPUTransferBuffer(_graphicsDevice, _transferBuffer, false);
            var samples = SampleBuffer.Data;
            for (var i = 0; i < samples.Length; i++)
            {
                mapped[i] = samples[i].Color;
            }
        }

        SDL.UnmapGPUTransferBuffer(_graphicsDevice, _transferBuffer);

        var copyPass = SDL.BeginGPUCopyPass(commandBuffer);

        SDL.UploadToGPUTexture(
            copyPass,
            new SDLGPUTextureTransferInfo(_transferBuffer, pixelsPerRow: _textureWidth, rowsPerLayer: _textureHeight),
            new SDLGPUTextureRegion(_texture, w: _textureWidth, h: _textureHeight, d: 1),
            false);

        SDL.EndGPUCopyPass(copyPass);
    }

    // A real broadcast picture's active area is conventionally 4:3, but
    // this texture is one raw sample per column and one scanline per row -
    // absolutely not the same physical size as each other, since Television's
    // horizontal sampling rate packs a scanline's active-video samples
    // (Television.ActiveVideoLengthSamples) into the same physical width a
    // real set devotes to a whole 4:3-shaped picture only activeLineCount
    // lines tall (the detected vertical active-line count - see
    // ComputeVerticalActiveRange). Rendered at native 1 sample:1 line
    // square-pixel scaling, this comes out badly squashed into a thin
    // horizontal band instead of anything resembling a picture (both
    // quantities are standard-specific - NTSC-only for now, same seam as
    // Television.Standard - which is exactly why the width figure is read
    // from Television rather than known here directly; this stays correct
    // with no changes here once a second standard exists). This is purely
    // a display-time correction (the same "non-square pixel" adjustment
    // real video tooling applies when showing a broadcast-format capture
    // on a square-pixel screen) - it stretches only how large ImGui.Image
    // draws the texture, not SampleBuffer's actual data, which stays at
    // native sample/line resolution for Phase 7's overlays (and any other
    // consumer that needs raw positions).
    private float VerticalStretchFactor(float activeLineCount) =>
        (_television.ActiveVideoLengthSamples / activeLineCount) / (4f / 3f);

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
        // The vertical active-line range needed both for "Active video
        // only"'s crop below and for VerticalStretchFactor's aspect-ratio
        // math (needed unconditionally, crop or not) - computed once per
        // frame and reused for both, rather than scanning SampleBuffer
        // twice. See ComputeVerticalActiveRange's own remarks.
        var (verticalActiveStart, verticalActiveCount) = ComputeVerticalActiveRange();

        // "Active video only" shows exactly what Phase 5/6 always showed:
        // just the picture, cropped out of the full raster via ImGui.Image's
        // own uv0/uv1 (a plain texture-sampling crop - SampleBuffer's actual
        // data is untouched either way). Unchecked instead shows the
        // *whole* raster - sync, blanking, color burst, and vertical
        // blanking/VSYNC lines included.
        Vector2 uv0, uv1;
        float displayedWidthSamples;
        float displayedHeightSamples;
        if (_activeVideoOnly)
        {
            var activeStart = _television.ActiveVideoStartSamples;
            var activeEnd = activeStart + _television.ActiveVideoLengthSamples;
            uv0 = new Vector2(activeStart / _textureWidth, (float)verticalActiveStart / _textureHeight);
            uv1 = new Vector2(activeEnd / _textureWidth, (float)(verticalActiveStart + verticalActiveCount) / _textureHeight);
            displayedWidthSamples = _television.ActiveVideoLengthSamples;
            displayedHeightSamples = verticalActiveCount;
        }
        else
        {
            uv0 = Vector2.Zero;
            uv1 = Vector2.One;
            displayedWidthSamples = _textureWidth;
            displayedHeightSamples = _textureHeight;
        }

        var availableSize = ImGui.GetContentRegionAvail();
        var finalSize = CalculateSizeFittingAspectRatio(
            new Vector2(displayedWidthSamples, displayedHeightSamples * VerticalStretchFactor(verticalActiveCount)),
            availableSize);

        ImGui.Image(_textureBinding, finalSize, uv0, uv1);

        // ImGui.Image is the item CalculateSizeFittingAspectRatio just sized -
        // its on-screen rect is what every overlay below needs to convert a
        // texture-space (column, row) into a screen-space pixel.
        var imageMin = ImGui.GetItemRectMin();
        var imageMax = ImGui.GetItemRectMax();
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

    // The vertical counterpart to Television.ActiveVideoStartSamples/
    // ActiveVideoLengthSamples - unlike those, this doesn't need its own
    // self-calibrated formula, because Television.ClassifyCurrentSample's
    // live vertical-blanking check (see that method's remarks) already
    // makes Sample.Region trustworthy vertically as well as horizontally,
    // the same "read Region straight out of SampleBuffer" approach
    // DrawRegionOverlays already uses rather than reconstructing timing
    // separately. Finds the longest contiguous block of rows whose
    // ActiveVideo sample count clears half of ActiveVideoLengthSamples -
    // comfortably separates full picture rows (the whole
    // ActiveVideoLengthSamples-worth) from blanking rows (0, or a partial,
    // self-correcting count right at a vertical-blanking region's edge -
    // see Television's own remarks on why that edge case exists and is
    // acceptable). Called unconditionally every frame (not just while
    // "Active video only" is checked) - VerticalStretchFactor's aspect-
    // ratio math needs the active line count regardless of crop state.
    private (int StartRow, int RowCount) ComputeVerticalActiveRange()
    {
        var width = (int)_textureWidth;
        var height = (int)_textureHeight;
        if (width <= 0 || height <= 0)
        {
            return (0, height);
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
        // just to skip this frame's scan and let the next one pick it back
        // up once they're back in sync, rather than reading past the end.
        if (samples.Length != width * height)
        {
            return (0, height);
        }

        var activeThreshold = _television.ActiveVideoLengthSamples * 0.5f;

        var bestStart = 0;
        var bestCount = 0;
        var runStart = -1;

        for (var row = 0; row <= height; row++)
        {
            var isActiveRow = false;
            if (row < height)
            {
                var activeCount = 0;
                var rowOffset = row * width;
                for (var column = 0; column < width; column++)
                {
                    if (samples[rowOffset + column].Region == RasterRegion.ActiveVideo)
                    {
                        activeCount++;
                    }
                }
                isActiveRow = activeCount > activeThreshold;
            }

            if (isActiveRow)
            {
                if (runStart < 0)
                {
                    runStart = row;
                }
            }
            else if (runStart >= 0)
            {
                var runCount = row - runStart;
                if (runCount > bestCount)
                {
                    bestStart = runStart;
                    bestCount = runCount;
                }
                runStart = -1;
            }
        }

        return bestCount > 0 ? (bestStart, bestCount) : (0, height);
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

        var column = (int)(u * _textureWidth);
        var row = (int)(v * _textureHeight);

        if (column < 0 || column >= _textureWidth || row < 0 || row >= _textureHeight)
        {
            return;
        }

        var samples = SampleBuffer.Data;

        // Same transient staleness guard as ComputeVerticalActiveRange's
        // own remarks - SampleBuffer may have been resized again since
        // PrepareOverride last synced _textureWidth/_textureHeight from it.
        if (samples.Length != (int)_textureWidth * (int)_textureHeight)
        {
            return;
        }

        var index = row * (int)_textureWidth + column;
        var sample = samples[index];

        ImGui.BeginTooltip();

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

        DrawHoveredSampleWaveform(samples, index, sample);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawHoveredSampleVectorscope(sample);

        ImGui.EndTooltip();
    }

    // The raw composite signal around the hovered sample, plus three
    // reference sines overlaid on it showing how NtscYiqDecoder actually got
    // from that raw waveform to the hovered sample's decoded color:
    //
    //   - Carrier: the color-burst PLL's own recovered subcarrier phase (see
    //     NtscColorBurstPll.CurrentPhaseRadians) - "here's what this
    //     decoder believes 0 degrees of color phase looks like right now",
    //     independent of any one sample's content.
    //   - I/Q component: the hovered sample's own decoded I/Q, each
    //     modulated back through that same phase (further rotated onto the
    //     I axis - see NtscYiqDecoder.BurstToIAxisRotationRadians) to show
    //     the two quadrature waveforms whose *correlation against the raw
    //     signal* is what produced those I/Q values in the first place -
    //     i.e. this is the demodulation math run in reverse, for exactly
    //     the sample under the cursor.
    //
    // Reads neighboring SampleBuffer entries as a de facto rolling log of
    // the raw signal (see Sample's own remarks) rather than TelevisionWindow
    // keeping a separate capture buffer - consecutive raster positions are
    // consecutive Television.Decode calls, so SampleBuffer.Data already
    // *is* that history, going back as far as the current frame's worth of
    // samples has been decoded.
    private static unsafe void DrawHoveredSampleWaveform(ReadOnlySpan<Sample> samples, int centerIndex, Sample centerSample)
    {
        var start = Math.Max(0, centerIndex - HoverWaveformRadius);
        var end = Math.Min(samples.Length - 1, centerIndex + HoverWaveformRadius);
        var count = end - start + 1;
        if (count < 2)
        {
            return;
        }

        Span<double> rawX = stackalloc double[count];
        Span<double> rawY = stackalloc double[count];
        Span<double> phases = stackalloc double[count];

        var rawMin = double.MaxValue;
        var rawMax = double.MinValue;
        var rawSum = 0.0;
        for (var k = 0; k < count; k++)
        {
            var s = samples[start + k];
            rawX[k] = start + k - centerIndex;
            rawY[k] = s.RawSample;
            phases[k] = s.CarrierPhaseRadians;
            rawMin = Math.Min(rawMin, rawY[k]);
            rawMax = Math.Max(rawMax, rawY[k]);
            rawSum += rawY[k];
        }

        // The three reference sines don't carry their own DC level (chroma
        // is inherently zero-mean) - the window's own average raw level
        // stands in for "the local luma baseline they'd actually ride on",
        // an approximation (not per-position luma, which would need each
        // sample's own black/white levels too - see the discussion this
        // tooltip came out of) that's good enough to show phase/shape
        // relationships without over-building for a diagnostic view.
        var baseline = rawSum / count;

        // Held fixed across the whole window rather than recomputed per
        // position - see this method's own remarks above on why.
        var chromaAmplitude = Math.Sqrt(centerSample.I * centerSample.I + centerSample.Q * centerSample.Q);
        var carrierAmplitude = chromaAmplitude > 1e-6 ? chromaAmplitude : NominalCarrierAmplitude;

        var maxAmplitude = Math.Max(carrierAmplitude, Math.Max(Math.Abs(centerSample.I), Math.Abs(centerSample.Q)));
        var yMin = Math.Min(rawMin, baseline - maxAmplitude);
        var yMax = Math.Max(rawMax, baseline + maxAmplitude);
        var yPad = Math.Max(1.0, (yMax - yMin) * 0.1);
        yMin -= yPad;
        yMax += yPad;

        var fineCount = (count - 1) * HoverWaveformSubdivisionsPerSample + 1;
        Span<double> fineX = stackalloc double[fineCount];
        Span<double> carrierY = stackalloc double[fineCount];
        Span<double> iY = stackalloc double[fineCount];
        Span<double> qY = stackalloc double[fineCount];

        for (var m = 0; m < fineCount; m++)
        {
            var kf = (double)m / HoverWaveformSubdivisionsPerSample;
            var k = Math.Min((int)kf, count - 2);
            var t = kf - k;

            fineX[m] = rawX[0] + kf;

            // Fixed 90-degrees-per-real-sample slope (the 4x-fsc input
            // contract - see docs/television-plan.md), anchored at each
            // stored discrete phase rather than lerping between the two
            // stored values directly - that would need unwrapping across
            // any line-boundary phase-offset nudge, and the true
            // instantaneous rate never actually changes (a nudge only
            // shifts *future* phase by a constant - see
            // NtscColorBurstPll's own flywheel remarks).
            var carrierPhase = phases[k] + t * (Math.PI / 2.0);
            var iAxisPhase = carrierPhase + NtscYiqDecoder.BurstToIAxisRotationRadians;

            carrierY[m] = baseline + carrierAmplitude * Math.Cos(carrierPhase);
            iY[m] = baseline + centerSample.I * Math.Cos(iAxisPhase);
            qY[m] = baseline + centerSample.Q * Math.Sin(iAxisPhase);
        }

        if (ImPlot.BeginPlot(
            "##waveform"u8,
            new Vector2(360, 160),
            ImPlotFlags.NoLegend | ImPlotFlags.NoMenus | ImPlotFlags.NoMouseText))
        {
            ImPlot.SetupAxes(
                ""u8,
                ""u8,
                ImPlotAxisFlags.NoTickLabels | ImPlotAxisFlags.NoGridLines,
                ImPlotAxisFlags.NoTickLabels | ImPlotAxisFlags.NoGridLines);
            ImPlot.SetupAxisLimits(ImAxis.X1, rawX[0] - 0.5, rawX[count - 1] + 0.5, ImPlotCond.Always);
            ImPlot.SetupAxisLimits(ImAxis.Y1, yMin, yMax, ImPlotCond.Always);

            // Soft band marking exactly which one of the several samples
            // shown is "the" hovered one - drawn first so every trace below
            // renders on top of it.
            var bandMin = ImPlot.PlotToPixels(-0.5, yMax);
            var bandMax = ImPlot.PlotToPixels(0.5, yMin);
            ImPlot.GetPlotDrawList().AddRectFilled(bandMin, bandMax, ImGui.GetColorU32(CurrentSampleBandColor));

            // Stair-stepped, like LogicAnalyzerWindow's own Analog trace -
            // this really is a discrete, one-value-per-sample signal (see
            // that class's DrawAnalogTrace remarks), so a step trace is the
            // faithful rendering, unlike the three continuous reconstructions
            // below.
            fixed (double* xPtr = rawX)
            fixed (double* yPtr = rawY)
            {
                ImPlot.PushStyleColor(ImPlotCol.Line, RawSignalColor);
                ImPlot.PlotStairs("Raw"u8, xPtr, yPtr, count);
                ImPlot.PopStyleColor();
            }

            fixed (double* xPtr = fineX)
            fixed (double* yPtr = carrierY)
            {
                ImPlot.PushStyleColor(ImPlotCol.Line, CarrierColor);
                ImPlot.PlotLine("Carrier"u8, xPtr, yPtr, fineCount);
                ImPlot.PopStyleColor();
            }

            fixed (double* xPtr = fineX)
            fixed (double* yPtr = iY)
            {
                ImPlot.PushStyleColor(ImPlotCol.Line, IComponentColor);
                ImPlot.PlotLine("I"u8, xPtr, yPtr, fineCount);
                ImPlot.PopStyleColor();
            }

            fixed (double* xPtr = fineX)
            fixed (double* yPtr = qY)
            {
                ImPlot.PushStyleColor(ImPlotCol.Line, QComponentColor);
                ImPlot.PlotLine("Q"u8, xPtr, yPtr, fineCount);
                ImPlot.PopStyleColor();
            }

            ImPlot.EndPlot();
        }

        DrawLegendEntry("Raw signal", RawSignalColor);
        DrawLegendEntry("Color carrier (recovered phase)", CarrierColor);
        DrawLegendEntry("I component (I·cos)", IComponentColor);
        DrawLegendEntry("Q component (Q·sin)", QComponentColor);
    }

    // Vectorscope-style view of exactly the (I, Q) pair NtscYiqDecoder
    // demodulated the hovered sample's chroma into - the standard NTSC
    // engineering tool for "what phase/amplitude produced this hue": a dot
    // plotted in the I/Q plane, colored to the sample's own decoded RGB, at
    // a distance from the origin proportional to saturation and an angle
    // (from the I axis) corresponding to hue.
    private static void DrawHoveredSampleVectorscope(Sample sample)
    {
        ImGui.TextUnformatted("I/Q vectorscope"u8);

        if (ImPlot.BeginPlot(
            "##vectorscope"u8,
            new Vector2(160, 160),
            ImPlotFlags.NoLegend | ImPlotFlags.NoMenus | ImPlotFlags.NoMouseText | ImPlotFlags.Equal))
        {
            ImPlot.SetupAxes(
                ""u8,
                ""u8,
                ImPlotAxisFlags.NoTickLabels | ImPlotAxisFlags.NoGridLines,
                ImPlotAxisFlags.NoTickLabels | ImPlotAxisFlags.NoGridLines);
            // Scaled to this sample's own magnitude (with a floor - see
            // MinVectorscopeRange) rather than a fixed range - nothing in
            // this decoder clamps I/Q to the "legal" NTSC gamut a fixed
            // range would assume, so a fixed range could clip a real point
            // outside the plot entirely (see that const's remarks).
            var magnitude = Math.Sqrt(sample.I * sample.I + sample.Q * sample.Q);
            var range = Math.Max(MinVectorscopeRange, magnitude * 1.25);

            ImPlot.SetupAxisLimits(ImAxis.X1, -range, range, ImPlotCond.Always);
            ImPlot.SetupAxisLimits(ImAxis.Y1, -range, range, ImPlotCond.Always);

            var plotDrawList = ImPlot.GetPlotDrawList();
            var axisColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);

            ImPlot.PushPlotClipRect();

            plotDrawList.AddLine(
                ImPlot.PlotToPixels(-range, 0),
                ImPlot.PlotToPixels(range, 0),
                axisColor);
            plotDrawList.AddLine(
                ImPlot.PlotToPixels(0, -range),
                ImPlot.PlotToPixels(0, range),
                axisColor);

            var originPos = ImPlot.PlotToPixels(0, 0);
            var pointPos = ImPlot.PlotToPixels(sample.I, sample.Q);
            var pointColor = ImGui.GetColorU32(new Vector4(sample.Color.R / 255f, sample.Color.G / 255f, sample.Color.B / 255f, 1f));

            plotDrawList.AddLine(originPos, pointPos, axisColor);
            plotDrawList.AddCircleFilled(pointPos, 5f, pointColor);
            plotDrawList.AddCircle(pointPos, 5f, ImGui.GetColorU32(ImGuiCol.Text));

            ImPlot.PopPlotClipRect();

            ImPlot.EndPlot();
        }

        ImGui.TextWrapped($"Luma: {sample.Luma:0.#}   I: {sample.I:0.#}   Q: {sample.Q:0.#}");
    }

    // Sidebar contents, stacked vertically: the two toggles, a status
    // readout (the raster oscillators' current period estimates - see
    // NtscRasterOscillators - and whether the color-burst PLL found a real
    // burst on the most recently completed line, see NtscColorBurstPll - a
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
    // timing constants - see docs/television-plan.md's Phase 7 and
    // RasterRegion's own remarks on why an earlier, nominal-timing-based
    // version of this was replaced.
    //
    // Scans every row (not one "representative" row standing in for all of
    // them, the way an earlier version of this did) because VSYNC genuinely
    // doesn't behave like the other four regions: HSYNC/color-burst/
    // blanking/active-video repeat at the same column range on every normal
    // line, but a VSYNC pulse suppresses the horizontal oscillator's normal
    // per-line column wraparound entirely (no HSYNC edges occur for it to
    // lock onto while the pulse is happening - see
    // NtscSyncSeparator.CurrentSyncRegion's remarks), so the columns a VSYNC
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
        var width = (int)_textureWidth;
        var height = (int)_textureHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var samples = SampleBuffer.Data;

        // Same transient staleness guard as ComputeVerticalActiveRange's
        // own remarks - SampleBuffer may have been resized again since
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
        var u = _television.CurrentColumn / (float)_textureWidth;
        var v = _television.CurrentRow / (float)_textureHeight;

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

    private static Vector2 CalculateSizeFittingAspectRatio(
        in Vector2 boundsSize,
        in Vector2 viewportSize)
    {
        // Figure out the ratio.
        var ratioX = viewportSize.X / boundsSize.X;
        var ratioY = viewportSize.Y / boundsSize.Y;

        // Use whichever multiplier is smaller.
        var ratio = ratioX < ratioY ? ratioX : ratioY;

        return boundsSize * ratio;
    }

    public override void Dispose()
    {
        base.Dispose();

        SDL.ReleaseGPUTexture(_graphicsDevice, _texture);
        SDL.ReleaseGPUTransferBuffer(_graphicsDevice, _transferBuffer);
    }
}
