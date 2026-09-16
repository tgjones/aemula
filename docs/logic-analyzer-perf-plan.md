# Logic Analyzer rendering performance — Implementation Plan

## Execution notes

When told to execute this plan: work through the phases below autonomously,
committing once each phase is done and working rather than batching
everything into one commit or stopping for approval between phases. Code
comments must stand on their own — explain the *why* directly in the
comment, never by citing this document or a phase number, since this file
is deleted once the plan lands (same convention as every other landed plan
in this repo). If anything encountered during implementation calls for
deviating from what's written here, stop and ask rather than improvising
past it.

## Goal

`LogicAnalyzerWindow` should stay smooth at any zoom level, including fully
zoomed out over the whole ring buffer (`LogicAnalyzerRecorder.DefaultCapacity`
= 131,072 samples/channel — several frames' worth on any of this repo's
systems) across every recorded channel at once. Today it visibly slows down
the more of the buffer is visible, because the amount of work done every
single frame scales with the *number of visible samples*, not with the
number of pixels available to show them in.

## Root cause

`DrawChannelRow` (`src/Aemula.UI/LogicAnalyzer/LogicAnalyzerWindow.cs`)
does two things per channel, per frame, proportional to `visibleCount` (the
number of raw samples between the current view's min/max):

1. **`FillVisibleSamples` heap-allocates two `double[visibleCount]` arrays**
   whenever `visibleCount > 4096` (the `stackalloc` cutoff). Zoomed out to
   the full 131,072-sample buffer, that's ~2MB per channel per frame, times
   every channel, every frame — enough sustained garbage to visibly stall
   on GC alone.
2. **Every one of those samples is hand-carried through to a draw call** —
   `PlotStairs` for Digital/Analog, or an `AddRectFilled`/`AddRect` pair per
   *run* for Bus. When most of a pixel column maps to dozens or hundreds of
   raw samples, ImPlot/ImGui are being asked to transform and rasterize far
   more geometry than the screen can even distinguish — the vast majority
   of it is invisible sub-pixel detail.

Both costs are proportional to buffer depth, not to screen width. The fix
is to make per-frame rendering work proportional to the plot's pixel width
instead, once there's more than roughly one sample per pixel — matching
your idea directly: figure out from the zoom level that most samples are
invisible, and stop sending them to ImPlot at all.

## Design

### Threshold: decimate only once zoomed out past ~1 sample/pixel

`samplesPerPixel = visibleCount / plotWidthPixels`. Below a small threshold
(`DecimationThresholdSamplesPerPixel`, start at 2.0 and tune by eye during
manual testing) every raw sample still gets its own pixel column's worth of
resolution, so the **existing exact per-sample path stays exactly as it is
today** — no change to what zoomed-in views look like. Above the threshold,
switch to a decimated path whose output size is bounded by pixel width
(`bucketCount`, clamped to a small fixed max like 4096 as a safety net for
extreme window widths) instead of by `visibleCount`. This bound holds
regardless of how much of the 131,072-sample buffer is on screen, which is
what makes "buttery smooth at any zoom level" achievable at all.

### Pure reduction logic lives with the recorder, not the window

The reduction itself — given the ring buffer, a visible sample range, and a
target bucket count, produce one aggregate per bucket — has no ImGui/ImPlot
dependency. Put it in a new `src/Aemula/UI/LogicAnalyzer/SampleDecimator.cs`
(same project/namespace as `LogicAnalyzerRecorder`, which already models the
ring buffer), as static methods operating on `ReadOnlySpan<ulong>` +
indices. That keeps it unit-testable via `Aemula.Tests` (which already
references `Aemula.csproj`, unlike `Aemula.UI.csproj` — see project layout)
without needing a new test project or an ImGui context. `LogicAnalyzerWindow`
calls into it and stays focused on translating the reduced buckets into
ImPlot/ImGui draw calls, same division of labor the codebase already uses
between `LogicAnalyzerRecorder` (model) and `LogicAnalyzerWindow` (view).

Two reduction shapes are needed:

- **Min/max envelope** (Digital + Analog): for each bucket, the raw
  sample's min and max value over that bucket's sample range. This is the
  standard audio-waveform/oscilloscope "envelope" technique — it's exactly
  what you want for a value that has real ordering, since it preserves
  brief spikes/toggles that a plain "pick one representative sample" would
  alias away. At the exact-path threshold boundary every bucket contains
  ≤1 raw sample, so min==max everywhere and the envelope degenerates
  continuously into the same line the exact path draws — no visual pop at
  the threshold.
- **Constant-or-mixed** (Bus): for each bucket, either "every raw sample in
  range was this one value" or a `MIXED` flag. Bus values are arbitrary
  64-bit patterns, so there's no safe sentinel value to smuggle "mixed"
  through the same array — use a parallel `bool[] isMixed` rather than a
  magic number.

Both reductions take the raw sample range as `(long visStart, int count)`
against the ring buffer's `(ulong[] buffer, int capacity)`, and should fast
path the common case where that range doesn't wrap the ring buffer (direct
indexing, no `% capacity` per sample) — worth doing now since this code is
being rewritten anyway, even though it's a secondary win next to bounding
the output size.

### Digital/Analog: render the envelope with `PlotShaded`

Add a `DrawEnvelopeTrace` alongside the existing `DrawDigitalTrace`/
`DrawAnalogTrace`, used only above the decimation threshold: call
`SampleDecimator` into two small stack-allocated `bucketCount`-sized spans
(min/max), scale them the same way `ScaleAnalogSamples` already does for
Analog (now cheap, since it's operating on `bucketCount` elements instead
of `visibleCount`), and plot with `ImPlot.PlotShaded` (fill between min and
max) plus a line on one edge — visually this reads as a thin trace during
quiet/steady stretches and a filled "busy" band wherever the signal is
toggling faster than screen resolution can show individually, which is the
honest picture. Below the threshold, `DrawDigitalTrace`/`DrawAnalogTrace`
keep doing exactly what they do today.

Hover tooltips currently index into the `ys` array that was sent to
ImPlot; once that array is a decimated envelope, its indices no longer
correspond 1:1 to sample offsets. Replace the tooltip lookup with a small
direct read of the single nearest raw sample straight from the recorder's
ring buffer (cheap — one lookup, not a scan), independent of whichever
array was actually drawn.

### Bus: decimate before the existing run-merging, not instead of it

`DrawBusTrace` already merges adjacent equal-value samples into one
rectangle — that's the right idea, it just currently scans all
`visibleCount` raw samples to find the runs. Change it to run its existing
merge loop over the decimated `(value, isMixed)` bucket arrays instead of
raw samples: a run of constant, non-mixed buckets draws exactly as today
(filled rect + centered hex text if it fits). A run of `MIXED` buckets
draws a visually distinct band (e.g. the same fill at a different
alpha/hatch, no text — text wouldn't be meaningful for "several different
values occurred here" anyway, and the existing width-based text-skip logic
already has to handle "no room for text"). This bounds `DrawBusTrace`'s
per-frame rect count to `bucketCount` regardless of how many actual value
transitions happened in the visible range, which is the other half of the
current worst case (a fast-toggling bus channel zoomed out can otherwise
generate one rect per transition).

Bus tooltips: constant buckets already carry their value, no re-read
needed; mixed buckets report "(changing)" rather than a misleading single
hex value.

### Eliminate the remaining per-frame heap allocation

`FillVisibleSamples`'s `new double[visibleCount]` fallback only fires
today when `visibleCount > 4096`. Once decimation caps the *decimated*
path's arrays at `bucketCount`, the only place this can still fire is the
*exact* path on an unusually wide plot (`plotWidthPixels` large enough that
`threshold × plotWidthPixels > 4096`) — rare, but worth closing properly
rather than leaving a residual GC source. Replace it with a small reusable
scratch buffer (`ArrayPool<double>.Shared.Rent`/`Return`, or a fixed
generous `stackalloc` cap) so neither path ever heap-allocates per frame.

## Phased plan

**Phase 0 — `SampleDecimator` + unit tests**

New `src/Aemula/UI/LogicAnalyzer/SampleDecimator.cs`: min/max envelope
reduction and constant-or-mixed reduction, both taking the ring buffer
directly (mirroring `LogicAnalyzerRecorder.GetChannelBuffer`'s shape) plus
`visStart`/`count`/`bucketCount`, with the non-wrapping fast path. Unit
tests in `Aemula.Tests` covering: bucket boundaries (including
`count` not evenly divisible by `bucketCount`), min/max correctness against
a hand-built sample sequence, mixed-vs-constant detection, `bucketCount >=
count` (should degenerate to one raw sample per bucket), and ring-buffer
wraparound.

**Phase 1 — Digital/Analog envelope rendering**

Wire the threshold check into `DrawChannelRow`; add `DrawEnvelopeTrace`
using `PlotShaded`; fix up the hover-tooltip direct-read. Verify visually
that the exact-path/decimated-path boundary doesn't pop.

**Phase 2 — Bus bucket rendering**

Rework `DrawBusTrace`'s run-merge loop to operate over decimated buckets,
with the mixed-band visual and tooltip handling described above.

**Phase 3 — Kill the remaining allocation**

Replace `FillVisibleSamples`'s `new double[]` fallback with a pooled/capped
scratch buffer; apply the same non-wrapping fast-path indexing there.

**Phase 4 — Manual verification**

Launch the UI against a system recording many channels (pick one with a
wide pin set already wired into a `LogicAnalyzerWindow`), run it long
enough to fill the ring buffer, and confirm: smooth interaction at every
zoom level including fully-zoomed-out; the zoomed-in exact rendering is
unchanged from before this plan; decimated envelope/bus bands look
plausible; tooltips make sense in both regimes; behavior while the
debugger is still running (view pinned to the live edge every frame) is
equally smooth, not just while stopped.

## Testing

Phase 0's reduction logic gets real unit tests, since it's pure and
already sitting in the one project `Aemula.Tests` references. Everything
downstream of that (Phases 1-3) is ImGui/ImPlot rendering with no
meaningful boundary to unit test — verify those manually per Phase 4,
per this repo's existing practice for `Aemula.UI` changes.

## Explicitly out of scope

A precomputed multi-resolution "mip" structure (incrementally maintained
downsampled buffers updated alongside `LogicAnalyzerRecorder.Sample()`,
avoiding even the linear per-frame scan) was considered and set aside for
now: at 131,072 samples/channel, a flat per-frame scan is a tight loop over
plain arrays with no allocation, cheap enough on its own — the actual cost
today is entirely in what gets handed to ImPlot/ImGui afterward, not in the
scan. Revisit only if a future capacity increase (orders of magnitude
past today's default) or a very large channel count ever makes the scan
itself measurable.
