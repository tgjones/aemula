# Television overscan (visible aperture) — plan

**Status: not started, and no longer a fix - an authenticity improvement.**

Written up 2026-09-14 alongside the horizontal-AFC change, which was done
first. That change (plus the row-scale latch it turned up - see
`NtscRasterOscillators`) **removed the symptom that prompted this**: the ZX80's
dark bar at the left edge is gone from the decoded raster entirely, because
the sync pulse it was made of is no longer written into a row that had already
been drawn. Nothing below is needed to hide an artifact any more.

What remains is the original, independent point: a real receiver does not show
the viewer the whole active raster, and this one does. That is still worth
modelling, but it is now a fidelity improvement competing on its own merits,
not a fix - and the case for it is weaker than it looked, because the thing it
would have hidden turned out to be a decoder defect worth fixing properly.

## What this is not

It is *not* a correction to the decoder's active-video window.
`Television.ActiveVideoStartSamples` places active video one sync-width after
the sync trailing edge, and that is exactly RS-170A: back porch (4.7µs) is
defined as equal to the sync pulse (4.7µs), and `NtscTiming` already
documents that breakdown. The signal model is right. Nothing in
`ActiveVideoStartSamples`, `ActiveVideoLengthSamples`, `IsActiveVideo`,
`Sample.Region`, the burst window or `ComputeActiveVideoRowRange` should
change.

## What it is

**Overscan** — a display property, not a signal property. A real CRT
deliberately deflects the beam wider and taller than the visible aperture, so
that picture-size drift (mains voltage, warm-up, ageing components) never
exposes a black edge at the mask. The tube mask hides the remainder.

This is why the broadcast safe-area conventions exist at all: action-safe is
the inner 90% (5% per edge) and title-safe the inner 80% (10% per edge), both
codified precisely *because* consumer receivers threw away 5-10% per edge.

It is also the only thing that hides an out-of-spec signal's sync pulse from
the viewer. Sync tip sits 40 IRE below blanking, so a real receiver's video
amplifier drives the CRT into cutoff there - it renders as black, not as
nothing. On a white background (the ZX80's, for instance) that is a visible
black notch. Overscan is what puts it behind the bezel.

## Decisions already taken

- **5% per edge**, i.e. the action-safe inner 90%. The conservative end of
  real consumer-set overscan and the figure the safe-area convention is built
  on. Horizontally that is ~32 samples on an 828-sample ZX80 line.
- **Applied in both the UI and screenshots.** `ScreenshotWriter` currently
  documents itself as producing "what a real TV would show"; keeping the two
  paths in agreement preserves that, and means eyeballing a headless run
  matches the window.
- **Vertical measured against nominal field geometry**, not against
  `ComputeActiveVideoRowRange`'s content-detected row range. A real tube's
  aperture is a fixed fraction of the field, so a system that draws fewer
  lines than a full field should lose the same absolute number of rows as one
  that fills it - not a proportional share of whatever it happens to draw.

## Shape of the change

Follow `ComputeVerticalStretchFactor`'s precedent exactly: it is documented as
"purely a display-time correction ... not something that touches
`SampleBuffer`'s actual data, which stays at native sample/line resolution for
region overlays". Overscan is the same kind of thing.

- A `Television` member (settable fraction + a method returning the cropped
  window in sample/row space) so the two consumers share one implementation
  and cannot drift.
- `ScreenshotWriter.Write` and `TelevisionTextureView.ComputeActivePlacement`
  consume it. The latter already does a pure UV crop with the raw raster
  untouched, which is the right mechanism - this narrows that window.
- `SampleBuffer` and `Sample.Region` untouched, so the region overlays and the
  debugger keep seeing the full raster.
- The fraction must be settable to 0, so signal-level diagnostic work (the
  kind that found the ZX80 sync overhang in the first place) can still see
  everything.

## Watch out for

- **Vertical against nominal field geometry needs care.** The decoder's
  systems have genuinely odd line counts, which is why the existing code
  detects active rows from content rather than assuming geometry. Work out
  what "nominal active field" means per detected field length before
  cropping, or a short-field system will lose real picture rows.
- **Every system's screenshot framing changes.** The television tests for
  Apple I/II, NES, Atari 2600, Space Invaders and ZX80 all need re-checking,
  and any asserted CRCs regenerating.
- **Aspect math.** `ComputeVerticalStretchFactor` takes the active line count
  and divides `ActiveVideoLengthSamples` by it. If the crop narrows both axes
  the stretch factor has to be computed from the same post-crop numbers, or
  the picture's aspect ratio will shift.

## Out of scope

The *cosmetics* of a real set - a drawn bezel, a rounded-corner mask, tube
curvature, barrel/pincushion distortion, corner focus falloff. Those are a
separate presentation layer. Overscan is the geometry, and it is what makes
the framing period-accurate; the bezel art is decoration on top and should
not be mixed into the same change.
