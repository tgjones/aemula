# Audio Output — Implementation Plan

## Goal

Give Aemula a working audio path, end to end: emulated systems produce sound,
a shared `IAudioSource` contract in `Aemula.Emulation.Output` (alongside
`Television`) exposes it as a pull-able 48 kHz stream, and
`Aemula.UI/EmulationWindow` feeds that to a real SDL audio device (the
placeholder in `EmulationWindow.RenderFrame` is the hook point).

No system in the repo currently emits audio anywhere. **Scope for this plan is
Apple II and Atari 2600 only** — the two systems whose sound hardware is
already modelled or trivially reachable. NES, BBC Micro, and Space Invaders are
explicitly out of scope (see the end of this doc); the plumbing built here is
designed so they can be added later without touching the core abstractions.

This plan models two genuinely different pieces of physical hardware, not
one:

- **Atari 2600 / TIA** emits a real, continuously-meaningful audio signal
  that leaves the chip — the audio equivalent of composite video, which this
  codebase already treats as the fidelity target for TIA's picture output
  (see `Atari2600System.CompositeVideo.cs`'s own remarks on why composite,
  not full RF, is the modelled signal). `AudioOutput` (below) is that
  signal's receiver-side model.
- **Apple II** has no audio-out signal at all. `$C030` drives a transistor
  wired straight to a physical speaker cone soldered to the board — the
  "signal" *is* the sound, there's nothing that ever leaves the machine as a
  voltage. `Speaker` (below) models the actuator itself, not a receiver.

## Execution protocol

When this plan is executed:

- **Run autonomously.** Work phase by phase without stopping for approval
  between phases.
- **Use sub-agents for the bulk of each phase** so the main context isn't
  consumed by file reads, builds, and test runs. The main agent stays the
  coordinator: it dispatches a phase to a sub-agent, reviews the diff and the
  sub-agent's report, commits, and moves on.
- **Commit after every phase**, once that phase builds and its targeted tests
  pass. One phase = one commit. Use a descriptive message; do not reference
  this plan doc or "phase N" in the message or in code comments (planning docs
  are deleted after landing — bake any rationale into the code comment
  itself).
- **Stop and ask the user** before deviating from this plan in any way: a
  design change, a scope change, a phase that turns out bigger than described,
  a blocked dependency, or anything the plan didn't anticipate. Resume
  autonomously once they answer.
- **Do not launch, click, or screenshot the app.** Verification is headless
  tests plus targeted `--treenode-filter` runs for each touched test class
  (never the full suite — it takes ~40 min). The user verifies audible output
  themselves after a phase lands.

## Current state audit

### `Aemula.Emulation.Output` (the target namespace)

- `Television` — the model to mirror. Pure C#, no UI/SDL dependency. Callers
  push one sample per fixed-rate tick via `Decode(byte)`; it runs the whole
  DSP pipeline internally and writes results into a public `SampleBuffer`.
  The UI side (`TelevisionTextureView`, `TelevisionWindow`) owns everything
  device-specific. The audio types below split the same way.
- `EmulatedSystem` base class exposes `public Television Television { get; } =
  new()`. Every system feeds it. Tooling (headless runner, benchmarks) reads
  frame progress off it generically.

### Sound hardware in the two in-scope systems

| System | Modelled today | Gap to audible output |
|---|---|---|
| **Atari 2600 / TIA** | Full. `TiaAudioChannel` (`Emulation/Chips/Tia/TiaAudioChannel.cs`), two channels, clocked twice per scanline (`TiaChip.cs` ~L1083/L1101), ~31.4 kHz. `TiaChip.Aud0`/`Aud1` 1-bit pins; `TiaAudioChannel.Sample` is a volume-scaled byte whose doc comment already says "for a future mixer that sums both channels". | Nothing reads the channels at system level. Need a summing/DC-block stage in `Atari2600System` → `AudioOutput.WriteSample`. **Small.** |
| **Apple II** | `SpeakerBit` (`AppleIISystem.GameIO.cs` L59) — a `Ttl7474Chip` flip-flop toggled on every `$C03X` access. 1-bit, sampled per master tick (14.318 MHz). The field comment literally says "A host audio backend would band-limit and resample this; no audio path is wired up yet (as with Television before it)". | Wire the flip-flop's `Q` to a new `Speaker` pin. **Small–medium**, mostly in the new `Speaker` type's BLEP synthesis rather than in `AppleIISystem` itself. |

### UI / SDL

- `Aemula.UI/Program.cs`: `SDL.Init` requests `Video | Gamepad` only — needs
  `| Audio`. Main loop is single-threaded: `system.RunForDuration(delta)` where
  `delta` is the wall-clock frame time **clamped to 17 ms** (there's a
  `// TODO: Not right` on the clamp). So emulation advances at roughly real
  time, frame-paced; on a hitch it under-advances. Present mode is
  `SDLGPUPresentMode.Mailbox` (no hard vsync lock).
- `EmulationWindow.RenderFrame` (L71) has the audio placeholder comment.
- `EmulationWindow` already owns per-system device resources
  (`TelevisionTextureView`) created in `SetSystem` and freed in `Dispose` —
  the audio stream fits the same lifecycle.
- **SDL3 audio API is fully bound** in `Hexa.NET.SDL3` 1.2.17:
  `OpenAudioDeviceStream`, `PutAudioStreamData`, `ResumeAudioStreamDevice`,
  `GetAudioStreamQueued`, `GetAudioStreamAvailable`, `CreateAudioStream`,
  `SetAudioStreamFrequencyRatio`, `SDLAudioSpec`. `SDL_AudioSpec.freq` is a
  plain, unvalidated `int` — SDL will happily accept an arbitrary declared
  rate and resample internally to whatever the physical device actually
  negotiates (real hardware tops out well under 1 MHz regardless). Considered
  and rejected running the device at something like the Apple II's own
  maximum speaker-toggle rate — see the `Speaker` section below for why.
- Tests use TUnit (`[Test]`, `await Assert.That(...)`); see
  `Aemula.Tests/Emulation/Output/TelevisionTests.cs`.

## Proposed design

### `IAudioSource` (new, `src/Aemula/Emulation/Output/IAudioSource.cs`)

The one contract `EmulationWindow` consumes. Two genuinely different physical
components sit behind it — a periodic line signal (`AudioOutput`) and an
edge-driven transducer (`Speaker`) — but the consumer side neither knows nor
cares which:

```
public interface IAudioSource
{
    float MasterVolume { get; set; }

    int AvailableOutputSamples { get; }

    // Fills up to destination.Length resampled OutputSampleRate (48 kHz)
    // samples; returns the count actually produced (short on underrun).
    int Read(Span<float> destination);

    // Drift trim from the consumer's buffer-depth feedback loop (see
    // EmulationWindow below). Multiplies the effective output rate by
    // (1 + trim); |trim| stays tiny (< ~0.02).
    void SetResampleTrim(double trim);

    void Reset();
}
```

`EmulatedSystem` exposes it uniformly:

```
public virtual IAudioSource Audio => NullAudioSource.Instance;
```

A system with real sound overrides `Audio` to return its own field (see
Atari 2600 / Apple II wiring below) — a plain overridden property backed by a
field, not a factory method called from the base constructor, so there's no
virtual-dispatch-before-derived-construction hazard.

`NullAudioSource` (new, tiny, `Emulation/Output/NullAudioSource.cs`) is a
stateless singleton `IAudioSource` that reports zero available samples and
fills `Read` with silence. Every system without sound — everything but the
two here, including NES/BBC/Space Invaders whenever they're picked up later —
needs no special-casing anywhere in the UI.

### `AudioOutput` — for a real audio-out signal (Atari 2600)

`IAudioSource`, pure C#, no SDL reference. Mono, `float` samples nominally in
`[-1, 1]`. Mirrors `Television`: a `WriteSample` front door, all DSP internal.

```
public sealed class AudioOutput : IAudioSource
{
    // inputSampleRate: the rate the owning system calls WriteSample at
    // (e.g. TIA's ~31.4 kHz).
    public AudioOutput(double inputSampleRate);

    public const int OutputSampleRate = 48_000;   // what Read produces

    public double InputSampleRate { get; }

    // The Decode analogue: one input sample from the system's audio tick.
    void WriteSample(float sample);

    // IAudioSource
    public float MasterVolume { get; set; }
    public int AvailableOutputSamples { get; }
    public int Read(Span<float> destination);
    public void SetResampleTrim(double trim);
    public void Reset();
}
```

Internal responsibilities:

1. **DC blocker** — one-pole high-pass on the input. A duty-dependent DC
   offset would otherwise thump on every timbre change.
2. **Anti-alias low-pass** — windowed-sinc FIR, cutoff a little below
   `min(InputSampleRate, OutputSampleRate) / 2`, before rate conversion.
3. **Fractional resampler** — `InputSampleRate * (1 + trim)` → 48 kHz. Linear
   or cubic (Catmull–Rom) interpolation over the filtered stream. A small
   input ring buffer decouples `WriteSample` (called from inside
   `RunForDuration`) from `Read` (called once per rendered frame).
4. **Underrun/overrun policy** — `Read` returns short on underrun; the input
   ring caps its backlog (drop oldest) so a paused-then-resumed emulator
   doesn't dump a huge latency spike.

### `Speaker` — for a directly-driven transducer (Apple II)

`IAudioSource`, new (`Emulation/Output/Speaker.cs`). Reusable by any future
system with the same "one pin straight to a physical speaker cone" hardware —
PC speaker, Sinclair-style beepers — not Apple-II-specific, same reuse story
as `Television`.

Models the actuator itself rather than a signal a receiver decodes. Its
public contract is a pin, matching how every chip in this codebase exposes
its pins (`Ttl7474Chip.Clk1`, `TiaChip.Col`, whose own doc comment already
notes "the setter samples... on every change") — a property whose *setter*
reacts to the edge, not a periodic-sample method:

```
public sealed class Speaker : IAudioSource
{
    // tickRate: the rate Tick() below is called at (e.g. Apple II's
    // 14,318,180 Hz master clock) - used only to place edges in time, never
    // to sample periodically.
    public Speaker(double tickRate);

    // The pin. Setter is a no-op unless the value actually changes; on a
    // real transition it inserts a band-limited step (BLEP) into the
    // internal 48 kHz output buffer at the exact fractional-sample position
    // Tick()'s running counter is at right now.
    public bool Level { get; set; }

    // Called once per tickRate-clock (e.g. once per master tick) - advances
    // Speaker's own free-running position, the same free-running-counter
    // pattern AppleIISystem._masterTickCounter already uses for burst phase.
    // Pins don't carry timestamps anywhere else in this codebase either;
    // position is implicit in call order, driven by the system's own tick
    // loop, exactly like every chip here.
    public void Tick();

    // IAudioSource
    public float MasterVolume { get; set; }
    public int AvailableOutputSamples { get; }
    public int Read(Span<float> destination);
    public void SetResampleTrim(double trim);
    public void Reset();
}
```

This is a better hardware match than sampling the pin on a fixed schedule and
decimating (the plan's original approach): the real signal genuinely is a
sparse sequence of edges — most of the 14.318 MHz timeline carries no
information at all, since the bit only moves on a `$C03X` access — and BLEP
is exactly the technique for reconstructing a correct band-limited waveform
from sparse edges rather than a periodic sample stream. It's cheaper too:
`Level` is only ever touched when `ToggleSpeaker()` actually runs, not on
some fixed high-frequency schedule.

`AppleIISystem.ToggleSpeaker()` connects it exactly like any other pin-to-pin
wire in this codebase:

```csharp
private void ToggleSpeaker()
{
    _speakerFlipFlop.D1 = _speakerFlipFlop.Qn1;
    _speakerFlipFlop.Clk1 = false;
    _speakerFlipFlop.Clk1 = true;
    _speaker.Level = _speakerFlipFlop.Q1;
}
```

with `_speaker.Tick()` called once per master tick from the same place
`TickCompositeVideo` already runs.

`Read`/`SetResampleTrim` still need the same near-unity fractional
resampling and buffer-depth drift correction `AudioOutput` does (the
emulator's wall-clock pacing drifts against the 48 kHz device the same way
regardless of which `IAudioSource` is feeding it) — but BLEP output is
already band-limited and already at the output rate, so `Speaker` doesn't
need `AudioOutput`'s DC-blocker/anti-alias-FIR/arbitrary-ratio-resample
stage on top. Share only the small piece both genuinely need (an output ring
buffer with drift-trimmed pull-`Read`) as a small internal helper, rather
than composing a whole `AudioOutput` inside `Speaker` or duplicating its
larger resampler.

**Why the SDL device itself doesn't need a higher rate:** we considered
opening the SDL stream at something like the Apple II's maximum theoretical
speaker-toggle rate instead of 48 kHz, since `SDL_AudioSpec.freq` is just a
number SDL doesn't validate against real hardware. Rejected: `Speaker`'s
whole point is that BLEP synthesis already reconstructs a correct,
band-limited signal directly at the output rate from sparse edges — there's
no naive high-rate intermediate stream to preserve. Declaring a much higher
source rate would only mean pushing far more samples through managed code
for SDL's own resampler to immediately discard, with no accuracy gained.
Both `AudioOutput` and `Speaker` share one `OutputSampleRate` (48 kHz), and
`EmulationWindow` opens exactly one stream at that rate regardless of which
system is loaded.

### `EmulationWindow` + SDL device

- `SetSystem`: open one playback stream —
  `SDL.OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK,
  spec{ format = F32, channels = 1, freq = 48000 }, null, null)` — then
  `SDL.ResumeAudioStreamDevice`. SDL resamples device-side if the hardware
  rate differs. Recreate on system swap (mirrors `_textureView`). This code
  only ever talks to `EmulatedSystem.Audio` as `IAudioSource` — it does not
  know or care whether the loaded system is backed by `AudioOutput` or
  `Speaker`.
- `RenderFrame` (replacing the L71 placeholder): each frame, top the stream up
  to a target latency (~60 ms ≈ 2880 samples). `queued =
  SDL.GetAudioStreamQueued(stream) / sizeof(float)`; `need = targetSamples -
  queued`; if `need > 0`, `Audio.Read` into a scratch buffer and
  `SDL.PutAudioStreamData`. Pad with silence if `Read` comes up short.
- **Drift correction**: proportional control on the same `queued` figure —
  `Audio.SetResampleTrim(k * (queued - targetSamples) / targetSamples)` with a
  small `k` and a clamp. Keeps latency near target as the wall-clock-paced
  emulator drifts against the 48 kHz device. (`SDL_SetAudioStreamFrequencyRatio`
  is an alternative lever if trimming inside `IAudioSource` proves noisy.)
- `Dispose`: `SDL.DestroyAudioStream`.
- `Program.Main`: add `SDLInitFlags.Audio` to `SDL.Init`.
- Menu: a **View ▸ Mute** toggle plus a volume slider driving
  `Audio.MasterVolume`. Small, and gives the user a control while iterating.

### Assessment of the proposed design

The original proposal (samples → an `Emulation.Output` type like
`Television.Decode`; `EmulationWindow` → SDL device) is the right shape and
is kept. What changed through discussion:

- **Two physical categories, not one type.** `AudioOutput` (periodic line
  signal) and `Speaker` (edge-driven transducer) model genuinely different
  hardware; forcing Apple II through a decimate-and-average `AudioOutput`
  path would have been modelling the wrong thing, the way a bitmap would be
  the wrong model for a CRT beam.
- **One shared consumer contract (`IAudioSource`)** keeps `EmulationWindow`
  and the SDL-facing code identical regardless of which physical category a
  given system needs — the uniformity the original proposal wanted is
  preserved by the interface, not by forcing every system through one
  concrete type.
- **Resampling/DSP lives in the `Output` types**, not the UI — matches
  `Television` doing all its DSP in core, and keeps it headless-testable.
- **Drift.** The emulator is paced by wall clock, not the audio clock, so
  produced vs. consumed sample rates drift regardless of source type. A
  buffer-depth feedback loop (`SetResampleTrim`) is part of the shared
  contract, not bolted onto one implementation.
- **Push model, single thread.** Open the SDL stream *without* a callback and
  feed it from the main loop each frame. No audio thread, no locks.
- **Mono.** TIA's two channels sum to one RF pin; the Apple II speaker is one
  bit. Stereo is a later concern.
- **One shared 48 kHz device rate** for both systems (see the `Speaker`
  section above) — no per-system SDL configuration.
- **"System has no audio yet"** is `NullAudioSource`, not a rate of zero — so
  it works uniformly for `AudioOutput`-shaped and `Speaker`-shaped future
  systems alike.

## Open decisions (confirm before or during execution)

1. **Master-volume UI** — assumed mute toggle **plus** a volume slider. Say if
   just a mute toggle is preferred.
2. **BLEP kernel** for `Speaker` (minBLEP table vs. a short polynomial BLEP,
   table size/order) — an implementation-time DSP tuning detail, decided
   during Phase 2, not a blocker.
3. **How much `Speaker` and `AudioOutput` literally share code** for the
   output ring buffer / drift-trimmed `Read` (small shared internal helper vs.
   two independent small implementations) — an implementation-time call, not
   a blocker.

## Phases

Each phase is one commit. Targeted test runs only.

### Phase 1 — `IAudioSource` + `AudioOutput` core

- Add `IAudioSource.cs`, `NullAudioSource.cs`, and
  `src/Aemula/Emulation/Output/AudioOutput.cs` per the API above: DC
  blocker, anti-alias FIR, fractional resampler, input ring buffer, `Read`,
  `SetResampleTrim`, `MasterVolume`, `Reset`.
- Tests `src/Aemula.Tests/Emulation/Output/AudioOutputTests.cs`:
  - A 1 kHz sine written at 31 400 Hz reads back at 48 kHz as ~1 kHz
    (Goertzel/zero-cross check), amplitude preserved within tolerance.
  - Feeding above Nyquist attenuates rather than aliasing down.
  - `Read` on an empty buffer returns 0 and writes silence.
  - `SetResampleTrim(+x)` measurably raises the output-sample count per second
    of input.
  - DC blocker: a constant input decays to ~0.
  - `NullAudioSource.Instance.Read` always returns 0 / silence.
- No system or UI changes yet.
- Verify: `AudioOutputTests` pass.

### Phase 2 — `Speaker` core (BLEP synthesis)

- Add `src/Aemula/Emulation/Output/Speaker.cs`: `Level` pin property,
  `Tick()`, BLEP edge insertion into a 48 kHz output ring buffer, drift-trim
  and `Read` (sharing code with `AudioOutput`'s buffer per decision 3).
- Tests `SpeakerTests.cs`:
  - A single edge produces a click whose energy/shape matches the BLEP
    kernel, with no unexpected artifacts beyond its known pre/post-ring
    window.
  - A periodic `Level` toggle at a known tick interval reproduces the
    expected fundamental frequency at 48 kHz (same style of check as
    `AudioOutputTests`' sine test).
  - No edges ever set → `Read` is all silence.
  - Two edges landing in the same output sample compose correctly.
  - `SetResampleTrim` behaves the same directionally as `AudioOutput`'s.
- No system or UI changes yet.
- Verify: `SpeakerTests` pass.

### Phase 3 — System + UI plumbing (still silent)

- `EmulatedSystem`: `public virtual IAudioSource Audio => NullAudioSource.Instance;`
- `EmulationWindow`: open/resume the SDL stream in `SetSystem`, drain
  `Audio.Read` → `PutAudioStreamData` in `RenderFrame` with the target-latency
  top-up and the drift-trim feedback loop, destroy in `Dispose`. Consumes
  `EmulatedSystem.Audio` purely as `IAudioSource`.
- `Program.Main`: `SDLInitFlags.Audio`.
- `EmulationWindow` menu: **View ▸ Mute** + volume slider (per decision 1).
- Delete the L71 placeholder comment; the code is now the thing it described.
- Verify: everything still builds; existing UI/tests unaffected; the
  (still audio-less) systems fall through to `NullAudioSource`. The user
  confirms "still launches, silent, no errors" out of band.

### Phase 4 — Atari 2600 (first audible output)

- New file `src/Aemula/Emulation/Systems/Atari2600/Atari2600System.Audio.cs`
  (partial class): a private `AudioOutput _audio = new(CyclesPerSecond /
  114.0);` field, `override IAudioSource Audio => _audio;`, and a
  `TickAudio()` called from `Atari2600System.Tick` at TIA's real two
  per-scanline audio ticks (matching `TiaChip`'s own ~31.4 kHz clocking).
- Sum the two channels: `(_tia.Audio0Sample + _tia.Audio1Sample) / 30f`
  (expose the channels' `Sample` from `TiaChip` if not already reachable),
  map to `[-1, 1]`, `_audio.WriteSample`. Let `AudioOutput`'s DC blocker
  centre it.
- Tests `Atari2600AudioTests.cs`: AUDC 4 (divide-by-two pure tone) at a known
  AUDF produces the expected fundamental at `AudioOutput`'s output; AUDV 0 is
  silent; AUDC 0 is silent.
- Verify: `Atari2600AudioTests` + existing `TiaAudioTests` pass. User confirms
  audible.

### Phase 5 — Apple II speaker

- New file `AppleIISystem.Audio.cs` (partial): a private `Speaker _speaker =
  new(14_318_180);` field, `override IAudioSource Audio => _speaker;`,
  `_speaker.Tick()` called once per master tick alongside
  `TickCompositeVideo`, and `_speaker.Level = _speakerFlipFlop.Q1;` added to
  `ToggleSpeaker()`.
- Tests `AppleIIAudioTests.cs`: a tight `$C030` toggle loop at a known
  instruction cadence produces a tone at the expected pitch; no toggles →
  silence.
- Verify: `AppleIIAudioTests` pass; existing Apple II tests unaffected. User
  confirms the classic speaker click/beep.

### Phase 6 — Docs & cleanup

- `src/Aemula/Emulation/Output/README.md`: add an "Audio" section mirroring
  the existing "Television" one — `IAudioSource`, the `AudioOutput`/`Speaker`
  split and why, the drift-trim approach, the two systems' rates.
- Sweep for stray TODOs the work resolved (the Apple II `SpeakerBit` "no audio
  path is wired up yet" comment in particular).
- Verify: touched test classes green.

## Out of scope

- **NES, BBC Micro, Space Invaders audio.** NES needs a full APU (100%
  stubbed today) and would be an `AudioOutput`-shaped source (a real mixer/DAC
  output pin); BBC Micro needs a new SN76489 PSG chip, also `AudioOutput`-
  shaped; Space Invaders is sample-based with no assets in the repo. Each can
  be its own plan later — `IAudioSource` is all the hook any of them need,
  with no API change.
- Stereo output.
- Fixing `Program.cs`'s `deltaTimeSpan` clamp (`// TODO: Not right`) — the
  audio path is made robust to it via buffering + drift trim rather than
  depending on a fix.
- Audio recording/export, per-channel mute, an audio scope/visualiser window.
