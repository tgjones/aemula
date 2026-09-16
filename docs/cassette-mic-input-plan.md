# Cassette deck microphone input — Implementation Plan

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

Let `CassetteDeck` read its playback lead from a *live* host microphone
instead of only from a pre-loaded tape file, so a real acoustic setup works:
a `.wav` (or an encoded `.o`, once
[docs/zx80-tape-loading-plan.md](zx80-tape-loading-plan.md) lands) plays
through a *different* computer's speakers, this computer's microphone picks
it up, and `ZX80System` (or `AppleISystem`) `LOAD`s it exactly as if a real
tape were spinning — because as far as the deck is concerned, a mic held up
to another machine's speaker *is* just a different-looking tape head.

The reverse direction — this emulator's own `SAVE`/monitor-speaker output
being picked up by a *different* computer's microphone — needs no new work:
`CassetteDeck.Audio` (the monitor speaker) already reaches the host's real
speakers today via `Rig.Audio` → `EmulationWindow.PumpAudio`, for any
`TransportMode`. This plan is only about the missing direction: host mic →
`CassetteDeck`.

`CassetteDeck` is shared, format-agnostic infrastructure (used by both the
Apple I's ACI and, once it lands, the ZX80), so this lands once in
`CassetteDeck` itself and both machines get it for free — no per-system code.

## Design

### `IAudioSink` — the mirror of `IAudioSource`

New file, `src/Aemula/Emulation/Output/IAudioSink.cs`. `IAudioSource` already
lets a peripheral hand *output* audio to the host without either side
knowing about SDL; this is the same idea in the other direction, for a
peripheral that wants *input* from a live host device:

```csharp
public interface IAudioSink
{
    // Whether this sink currently wants samples pushed to it. The host only
    // needs to keep a capture device open while at least one sink across
    // the rig is armed - checked once a frame, the same way PumpAudio reads
    // AvailableOutputSamples, so the mic is only ever hot while a deck's Mic
    // control is actually engaged (see "Lazy device lifetime" below).
    bool WantsInput { get; }

    // Push live samples at the given rate. A sink that isn't currently
    // WantsInput is free to just drop them cheaply rather than buffer them,
    // the same way NullAudioSource.Read is a no-op in the other direction.
    void PushSamples(ReadOnlySpan<float> samples, int sampleRate);
}
```

`IPeripheral` gets one new member, directly mirroring `Audio`:

```csharp
/// Live audio this peripheral accepts from the host microphone (a cassette
/// deck's mic-in jack), or null if it takes no live input. The Rig surfaces
/// every non-null one as LineIns.
IAudioSink? LineIn { get; }
```

`Rig` gathers them exactly like it gathers `Audio`, alongside the existing
loop in its constructor:

```csharp
public IReadOnlyList<IAudioSink> LineIns { get; }
```

(a plain list, not mixed the way `Audio` sources are — there's no sensible
way to "mix" two live mic-input sinks, and in practice there's at most one
cassette deck per rig today anyway.)

### `CassetteLineIn`

New file, `src/Aemula/Emulation/Peripherals/Cassette/CassetteLineIn.cs`,
sitting next to `CassettePlayer`/`CassetteRecorder` as the third source
`CassetteDeck` can read its playback lead from. Same resampling math as
`CassettePlayer.NextSample` (linear interpolation is already established as
plenty for a tape-rate carrier against a CPU-cycle consumer rate — see that
class's remarks), but shaped for a *push* producer instead of a pre-loaded
array:

- A small ring buffer of raw host-rate samples (bounded backlog, sized the
  same way `AudioOutput`'s `MaxBacklogSeconds` is — big enough to ride out a
  frame of jitter, small enough that a deck left "listening" with nothing
  connected doesn't accumulate stale audio).
- `PushSamples(samples, sampleRate)` (implements `IAudioSink`): appends to
  the ring, recomputing the resample step if `sampleRate` changes.
- `NextSample()`: pulled once per interface cycle by `CassetteDeck`, exactly
  like `CassettePlayer.NextSample()` — advances a fractional cursor through
  the ring and linearly interpolates. Returns silence on underrun (no data
  pushed yet, or the deck only just started listening) rather than blocking
  or throwing, matching `CassettePlayer`'s "no tape loaded" behavior.
- `WantsInput` (implements `IAudioSink`): plain settable bool, driven by
  `CassetteDeck.Mode`.

Pure and host-agnostic — testable by pushing synthetic sample batches at an
arbitrary rate and asserting `NextSample()` reproduces them resampled,
without touching any real audio device.

### `CassetteDeck` changes

A fourth, mutually-exclusive transport state, alongside `Stopped` /
`Playing` / `Recording`:

```csharp
public enum TransportMode
{
    Stopped,
    Playing,
    Recording,
    Listening, // NEW: playback lead sourced live from the host microphone
}
```

`CassetteDeck` gains a `CassetteLineIn _lineIn` field (constructed alongside
`_player`/`_recorder`, given the same `interfaceSampleRate`), and:

```csharp
public IAudioSink LineIn => _lineIn;

public float ReadPlayback()
{
    _playbackLevel = _mode == TransportMode.Listening
        ? _lineIn.NextSample()
        : _player.NextSample();
    return _playbackLevel;
}
```

The `Mode` setter's existing `_player.IsRunning = value == TransportMode.Playing`
line already pauses the loaded tape's cursor for any mode other than
`Playing`, so switching into `Listening` and back to `Playing` leaves the
tape exactly where it was — no new pause/resume logic needed there. It just
gains one line to arm/disarm the sink:

```csharp
_lineIn.WantsInput = value == TransportMode.Listening;
```

A new control sits next to the existing "Tape" Play/Stop control:

```csharp
new ConsoleControl(
    "Mic",
    "tape-mic",
    ConsoleControl.ControlKind.Latching,
    () => _mode == TransportMode.Listening,
    listening => Mode = listening ? TransportMode.Listening : TransportMode.Stopped,
    offLabel: "Listen",
    onLabel: "Stop"),
```

No tape needs to be inserted to use it — it's a substitute for the earphone
cable, not for the cassette itself. `FormatPosition` gets one new branch so
the counter reads something sensible (e.g. `"Listening…"`) instead of the
file-tape position while in this mode.

### Lazy device lifetime (Aemula.UI)

`EmulationWindow` already opens one SDL playback audio stream per rig,
unconditionally, in `SetRig` (see its class remarks and `PumpAudio`). The mic
path is intentionally *not* symmetric with that: the OS mic-in-use indicator
and permission prompt are a real, user-visible cost, so the capture device
should only be open while something actually wants it — mirroring a real
deck, where you don't plug in the mic-in cable until you're about to use it.

- `EmulationWindow` gets a second, initially-null `SDLAudioStreamPtr _micStream`
  and checks once a frame (in a new `PumpMicInput`, called from
  `RenderFrame` next to `PumpAudio`) whether `_rig.LineIns.Any(s => s.WantsInput)`:
  - Transitioning false → true opens (`SDL.OpenAudioDeviceStream` against
    `SDL_AUDIO_DEVICE_DEFAULT_RECORDING` — like `AudioDeviceDefaultPlayback`,
    this binding doesn't surface the macro, so it's a second named constant
    for that device id's value) and resumes the stream. This is the moment
    the OS permission prompt fires and the indicator lights up.
  - Transitioning true → false destroys the stream (`SDL.DestroyAudioStream`),
    releasing the indicator immediately — e.g. the instant "Mic" is toggled
    back off, not just on eject/system swap.
  - While open, drains it every frame with `SDL.GetAudioStreamData` (the pull
    counterpart of `PutAudioStreamData` in `PumpAudio`) into a reused scratch
    buffer, then calls `PushSamples` on every entry in `_rig.LineIns` (not
    just the ones currently `WantsInput` — cheap enough, and keeps the
    filtering logic in one place: `CassetteLineIn` itself, per its own
    `WantsInput` field, in phase with `CassetteDeck.Mode`).
  - Non-fatal if `OpenAudioDeviceStream` fails or permission is denied — same
    "runs on silently" policy `SetRig` already applies to the playback
    stream — logged the same way, and the deck's mic-in just stays silent
    until it fails less. `WantsInput` staying true when this happened is
    fine: it just means the device open is retried next frame instead of
    latching a permanent failure, matching the rest of this codebase's
    preference for stateless, self-correcting per-frame polling.
  - `SetRig` also needs to close `_micStream` unconditionally on rig swap
    (same shape as its existing playback-stream teardown), regardless of
    `WantsInput`, since the peripheral instance it belonged to is going away.
- Requested capture spec: `Freq = AudioSampleRate` (48 kHz, same constant
  `PumpAudio` already uses), `Channels = 1`, `Format = F32Le` — SDL converts
  from whatever the OS mic's native format actually is, the same way the
  playback stream already converts on the way out.

## Known risk (flag, don't solve here)

Consumer OS/driver mic paths commonly apply noise suppression, echo
cancellation, and AGC tuned for voice — processing actively hostile to a
clean, repetitive ~3.3 kHz tone, which is exactly what a cassette signal is.
This may need the OS's mic input switched to its most "raw"/unprocessed mode
(where the OS exposes one) to load reliably. That's a real acoustic-channel
risk to validate by hand once this lands, not something to design around
speculatively now — note it in the manual test below and revisit only if it
turns out to be a real blocker.

## Testing

- `CassetteLineInTests` (new): push synthetic sample batches at various
  rates (including a rate change mid-stream, and pushing nothing at all —
  asserting silence on underrun) and assert `NextSample()`'s resampled
  output, entirely without a real audio device — same spirit as
  `ZX80TapeEncoderTests`'s isolation from `ZX80System`.
- `CassetteDeckTests` (new or extended, if a file for this doesn't already
  exist): `Mode` transitions in and out of `Listening` correctly arm/disarm
  `LineIn.WantsInput`, leave a loaded tape's position untouched across a
  `Listening` round-trip, and that `ReadPlayback()` reads from the right
  source in each mode.
- No automated test drives real speaker output into a real microphone —
  there's no such hardware in CI, and room acoustics make it inherently
  non-reproducible. That leg only gets a manual smoke test:
  1. Load the ZX80 rig; insert or build a tape (per
     [docs/zx80-tape-loading-plan.md](zx80-tape-loading-plan.md)) and use its
     existing `SaveRecording`/`WavWriter` path — or just any `.wav` — on a
     *second* computer's media player, ready to play but not yet started.
  2. In Aemula, press **Mic** on the cassette deck's status-bar control
     (grants/prompts for mic permission at this point).
  3. Start playback on the second computer, speaker facing this computer's
     microphone, at a reasonable volume.
  4. On the emulated keyboard, `W` then `NEWLINE` (ZX80) to `LOAD`, exactly
     per the existing plan's load instructions.
  5. Watch for the usual "agitated static" load screen and a clean return to
     a program listing; if it never resolves, that's the known risk above
     surfacing — try disabling the OS mic's noise suppression / voice
     isolation before concluding anything is broken in the code.
  6. Press **Mic** again to release the OS indicator.
