# Television

## Information

* [Raster Graphics Handbook](https://ece-classes.usc.edu/ee459/library/documents/RS170.pdf)
* [NTSC Studio Timing: Principles and Applications](https://www.technicalaudio.com/pdf/Grass_Valley/Grass_Valley_NTSC_Studio_Timing.pdf)
* NTSC demystified
  * [Part 1 - B&W video and sync](https://sagargv.blogspot.com/2011/04/ntsc-demystified-part-1-b-video-and.html)
  * [Part 3 - Nuances and numbers](https://sagargv.blogspot.com/2011/04/ntsc-demystified-nuances-and-numbers.html)
  * [Part 7 - Color palette demo with simplified progressive and interlace scanning](https://sagargv.blogspot.com/2014/07/ntsc-demystified-color-demo-with.html)
* [Useful notes on NTSC](https://www.ntsc-tv.com/index.html)

## Other implementations

* [crtsim](https://github.com/reenigne/reenigne/blob/master/crtsim/crtsim.cpp)
* [Jake Turner's NTSC decoder](https://github.com/Zorro666/NTSC)
* [Software composite video modulation/demodulation experiments](https://github.com/svofski/CRT)
* [NTSC-CRT](https://github.com/LMP88959/NTSC-CRT)

# Audio

The audio types here play the same role for sound that `Television` plays for
the picture: a pure-C# receiver/actuator model with no UI or SDL dependency,
fed one sample (or one edge) at a time from the same tick that produces it,
running its whole DSP pipeline internally, and exposing a pull-able 48 kHz
stream for the UI to hand to a device. All resampling and filtering lives in
core so it stays headless-testable.

## `IAudioSource`

The one contract the UI consumes (`EmulationWindow` opens a single 48 kHz SDL
playback stream and, each frame, tops it up to a target latency by pulling
`Read` and pushing the result with `PutAudioStreamData`). It is deliberately
agnostic about what is behind it:

* `Read(Span<float>)` fills resampled 48 kHz mono float, returning the count
  produced (short on underrun; the tail is silence).
* `SetResampleTrim(double)` is the drift-correction lever. The emulator is
  paced by wall clock, not by the audio device, so produced and consumed
  sample rates drift apart over minutes regardless of source. `EmulationWindow`
  runs a proportional loop on the device's queued depth and calls this with a
  tiny correction (`|trim|` well under 0.02) to hold latency near target.
* `MasterVolume`, `Reset`, `AvailableOutputSamples`.

`EmulatedSystem` exposes `virtual IAudioSource Audio => NullAudioSource.Instance`.
A system with sound overrides it with a field-backed property; every silent
system (everything but the Atari 2600 and Apple II today) falls through to the
stateless silent singleton, so nothing above the interface branches on "does
this system have audio?".

## Two physical categories

The two systems in scope model genuinely different hardware, so there are two
concrete `IAudioSource` implementations rather than one:

* **`AudioOutput`** - for a real, continuously-meaningful audio-out signal, the
  audio equivalent of composite video. The **Atari 2600 / TIA** sums its two
  tone channels onto the single line feeding the RF modulator;
  `Atari2600System.Audio.cs` reproduces that sum one value per TIA audio clock
  (~31.4 kHz, `OSC / 114`, sampled off `TiaChip.AudioClocked`) and pushes it in
  through `WriteSample`. `AudioOutput` then runs a one-pole DC blocker, a
  63-tap Blackman-windowed-sinc anti-alias low-pass, and a Catmull-Rom
  fractional resampler over an input ring buffer whose backlog is capped so a
  paused-then-resumed emulator does not dump a latency spike.

* **`Speaker`** - for a directly-driven transducer with no signal at all. The
  **Apple II**'s `$C030` soft switch clocks a flip-flop whose Q drives a
  transistor wired straight to a speaker cone soldered to the board; the
  "signal" is nothing but the instants that pin flipped. `Speaker` models the
  actuator: `Level` is the pin (its setter reacts to the edge), `Tick()`
  advances a free-running position at the 14.318 MHz master clock, and every
  transition is spliced into the 48 kHz output as a band-limited step (BLEP -
  a short Blackman-windowed-sinc step at 32 sub-sample phases). BLEP output is
  already band-limited and already at the output rate, so `Speaker` skips
  `AudioOutput`'s anti-alias FIR and arbitrary-ratio resampler. It does keep a
  gentle ~20 Hz one-pole DC blocker: a real cone is a sprung mass that relaxes
  to rest and every downstream path AC-couples, so a held drive level must bleed
  away to silence rather than sit as a permanent pedestal.

Both share the output side - a near-unity fractional read cursor with the same
`SetResampleTrim` drift trim and backlog cap - because the wall-clock-vs-device
drift is the same problem regardless of source type. Both produce mono
`OutputSampleRate` (48 kHz), and `EmulationWindow` opens exactly one device at
that rate whatever system is loaded.

## Out of scope

NES (needs a full APU), BBC Micro (needs an SN76489 PSG), and Space Invaders
(sample-based, no assets in the repo) have no audio path yet. Each would be an
`AudioOutput`-shaped source and needs no change to `IAudioSource` to add.
Stereo is also a later concern - TIA's two channels sum to one pin and the
Apple II speaker is one bit.
