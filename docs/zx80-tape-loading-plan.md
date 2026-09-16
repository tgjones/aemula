# ZX80 tape loading (single-segment `.o` files) — Implementation Plan

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

Load a standard, single-segment ZX80 tape image — a raw `.o` memory-dump
file, the format existing emulators use for a ZX80 `SAVE` — into
`ZX80System` through the *existing* cassette hardware, exactly the way a
real ZX80 loads from a real cassette: by playing back audio and running the
ROM's own `LOAD` routine, not by poking bytes into RAM.

First concrete target: `/Users/timjones/Downloads/MakingTheMostOfYourZX80(ComputerPublications)/1
- Decision Maker - 1 (Making the most of your ZX80, Page 7).o` (212 bytes —
comfortably inside the stock 1KB RAM, no RAM pack needed).

Explicitly out of scope for this plan (deferred to a later one): the
two-segment "loader + fast-loader" tape layout some larger/commercial
programs use (see the Kong investigation this plan followed from) and the
16K RAM-pack expansion that class of program needs.

Note this is *not* because those tapes use some other pulse encoding —
disassembling Kong's second-stage loader (relocated to `$7F90`) turned up
byte-for-byte identical bit-reading code and timing constants (`0x94`,
`0x1A`, `0x56`, the same `RRA`/`RLA`/`RLA` idiom) to the ROM's own `LOAD`
routine. It's a relocated copy of the ROM's bit-reader, reused so it can
target a fixed RAM address the standard `LOAD` command has no way to
specify — not a different protocol. So `ZX80TapeEncoder` below is written
as *the* ZX80 tape format, full stop, with no per-game parameterization —
and there's a real chance it plays Kong's second file correctly too, once
something exists to give it 16K to land in and drive the two-file sequence.
That's still deferred here because the RAM pack and the two-stage
LOAD/RUN/LOAD sequencing are separate, unverified work, not because the
encoder itself would need to change.

## What a `.o` file is

Confirmed by inspection (both this file and the earlier Kong files share the
same structure) and cross-checked against the
[Nocash Sinclair ZX documentation](https://www.problemkaputt.de/zxdocs.htm):
a `.o` file is a raw capture of ZX80 RAM from `$4000` (where system
variables always start) through to the address stored in the system
variable at `$400A` — i.e. `LEN = (400Ah) - 4000h` — with no header, no
filename, and no end-of-file marker of any kind. This is *exactly* what the
ROM's own `SAVE` routine writes to tape; a `.o` file is that same byte
stream with the audio-encoding step already stripped out. Loading it back
means putting the audio-encoding step back, then letting the real `LOAD`
routine (already fully emulated — no new ROM code path needed) do the rest.

## Tape pulse format

Sourced from the Nocash ZX docs and cross-checked against our own
`zx80.rom`'s actual `LOAD` routine (disassembled at `$0206`–`$024D` using
the project's existing `Z80Disassembler` — it visibly waits on `IN A,
(0xFE)`, uses the `RRA`/`RLA`/`RLA` idiom to steer the sampled EAR bit into
carry, and counts pulses via `DJNZ`/`DEC` loops against constants that match
the timing below):

- **Bit encoding, MSB first, no start/stop bits**: a `0` bit is 4 pulses; a
  `1` bit is 9 pulses.
- **Each pulse**: 150µs high, 150µs low (300µs period, ≈3.3kHz).
- **Inter-bit gap**: 1300µs of silence between one bit's pulse train and the
  next.
- **Leading silence**: at least 0.5s before the first bit (the ROM is
  waiting for silence-then-a-transition, not a fixed pilot tone — there
  isn't one). This plan uses 1s, comfortably over the minimum.
- **End of load**: there is no marker to encode — the ROM stops reading once
  its own running pointer reaches the `$400A` length it read out of the
  early system-variable bytes. So the encoder's job is simply: emit the
  leading silence, then every byte of the `.o` file in order, and stop.

This is quiet, deliberate confirmation of something the earlier Kong
investigation left open: the *standard* ROM `LOAD` path (which is all this
plan needs) is fully pinned down by a primary source plus our own ROM
disassembly, independent of the harder open question of Kong's custom
loader.

## Design

### `ZX80TapeEncoder`

New file, `src/Aemula/Emulation/Systems/ZX80/ZX80TapeEncoder.cs`: a static
`byte[] → float[]` converter implementing the format above. Internal sample
rate: 48kHz, matching `CassetteRecorder.SampleRate` (the rate this codebase
already uses for cassette audio elsewhere), so the two are trivially
interchangeable and a synthesized tape can be dumped to a real, playable
WAV via the existing `WavWriter` if that's ever useful. `CassettePlayer`
resamples whatever rate it's handed to the consumer's own cycle rate by
linear interpolation regardless, so 48kHz against a 3.3kHz carrier leaves
enormous headroom — the same margin `CassetteRecorder`'s own doc comment
already calls out for the Apple I's ACI.

Pure function, no dependency on `ZX80System` or any chip — testable in
complete isolation against known byte patterns (e.g. confirm a `0x00` byte
produces eight 4-pulse trains, a `0xFF` byte eight 9-pulse trains, with the
right sample counts for 150µs/300µs/1300µs at 48kHz).

### Wiring into the existing cassette bay

`CassetteDeck` (`Emulation/Peripherals/Cassette/CassetteDeck.cs`) is shared
with the Apple I and is deliberately format-agnostic. Rather than bolt on a
loose optional filter/delegate pair, it gets a proper type for "a tape
format it can read" and a `params` list of them:

```csharp
public sealed record CassetteDeckFormat(
    MediaFileFilter Filter,
    Func<MediaImage, (float[] Samples, int SampleRate)> Decode);

public CassetteDeck(double interfaceSampleRate, params CassetteDeckFormat[] additionalFormats)
```

`Decode` is only ever called for a `MediaImage` whose extension already
matched `Filter.Pattern`, so unlike the earlier sketch it doesn't need to
return `null`/signal "not mine." Internally `CassetteDeck` defines its own
built-in WAV format the same shape as any other:

```csharp
private static readonly CassetteDeckFormat WavFormat = new(
    new MediaFileFilter("Cassette audio", "wav"),
    image =>
    {
        var wav = WavReader.Read(image.OpenRead());
        return (wav.Samples, wav.SampleRate);
    });
```

and combines it with whatever's passed in (`_formats = [WavFormat, ..
additionalFormats]`) so there's one unified array rather than a special-cased
WAV path plus a bolt-on extra. The media-bay insert callback picks the
format whose `Filter.Pattern` matches the inserted file's extension
(falling back to `WavFormat` if somehow nothing matches — the same
unconditional-WAV behavior the bay has always had for an unrecognized
pick), and the bay's dialog filters become `_formats.Select(f => f.Filter)`
plus the existing "All files" catch-all. The Apple I passes no additional
formats and is completely unaffected.

`ZX80System.Cassette.cs`'s `BuildCassettePeripheralRequests` then supplies
the one ZX80-specific format:

```csharp
new CassetteDeck(
    context.System.CyclesPerSecond,
    new CassetteDeckFormat(
        new MediaFileFilter("ZX80 tape image", "o"),
        image => (ZX80TapeEncoder.Encode(image.Data), ZX80TapeEncoder.SampleRate)))
```

This keeps the whole feature framed as what it actually is — still a tape,
in the same slot, played on the same transport controls — rather than
inventing a new "snapshot" or "ROM image" media concept. The cassette
`MediaBay`'s dialog will offer both `.wav` and `.o` for a ZX80 rig; nothing
about the Apple I's cassette bay changes.

## Testing

- `ZX80TapeEncoderTests` (new,
  `Aemula.Tests/Emulation/Systems/ZX80TapeEncoderTests.cs`): unit-test the
  pulse counts and sample-level timing directly against the spec above, for
  both bit values and for multi-byte sequences (checking the inter-bit and
  inter-byte silence too, since the format doesn't distinguish them).
- A system-level load test (new or added to `ZX80SystemCassetteTests.cs`):
  boot `ZX80System`, insert an encoded tape, drive the `W` `NEWLINE` keys via
  `OnKeyEvent` (same mechanism `ZX80SystemKeyboardTests.cs` already
  exercises), press PLAY on the deck, tick until loading completes, and
  assert the loaded RAM bytes at `$4000` match the source bytes exactly.

  The actual "Decision Maker" `.o` file (typed-in from a 1980s computer
  book) does **not** get checked into the repo for this — build a small
  synthetic `.o` by hand in the test instead: a minimal but valid system-
  variables block (enough that the ROM's `LOAD` routine can compute `LEN`
  from `$400A` correctly) followed by a trivial known program body. The
  real book file stays something you load manually per the instructions
  below, not a committed fixture.

## Instructions to load the tape (for you to follow once this lands)

This is the real, unmodified ZX80 procedure — confirmed directly from
Sinclair's own 1980 *ZX80 Operating Manual* (`W`/`R` key mappings, screen
behavior during load) — nothing about the UI changes what a real user would
have done:

1. Open the ZX80 rig's cassette media bay and choose the "Decision Maker"
   `.o` file (it'll show up as a "ZX80 tape image" alongside the existing
   `.wav` filter).
2. Press **PLAY** on the tape deck control in the status bar.
3. On the ZX80's keyboard, press **W** then **NEWLINE** (ENTER). There's no
   filename to type — the ZX80 doesn't have them. This types `LOAD` (the
   ZX80's single-keystroke keyword entry — `W` is the `LOAD` keyword in
   K-cursor mode) and starts the ROM's tape-read routine.
4. Watch the screen: it goes grey/black, then "agitated" grey static while
   the ROM reads pulses. When it clears back to a normal program listing,
   loading finished successfully.
5. Press **R** then **NEWLINE** to `RUN` the now-loaded program.
6. Press **STOP** on the tape deck once you're done with it.

If loading doesn't finish cleanly, the manual's own advice still applies:
**SHIFT+SPACE** is `BREAK`, which aborts a stuck load.
