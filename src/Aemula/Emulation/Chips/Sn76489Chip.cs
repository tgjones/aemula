using System;

namespace Aemula.Emulation.Chips;

/// <summary>
/// The Texas Instruments SN76489: three square-wave tone generators and a
/// noise generator, each with a 2dB-step attenuator, mixed onto one output.
///
/// The chip divides its clock by 16 before its counters see it, and a tone
/// generator's output toggles each time its 10-bit counter runs out, so a
/// period of N gives a frequency of clock / (32 * N). Data is taken on the
/// falling edge of /WE while /CE is low. The data byte is as it appears on
/// the bus (bit 7 distinguishes a command byte), the opposite order to the
/// chip's own D0-D7 pin names, which the BBC's wiring already accounts for.
/// </summary>
public sealed class Sn76489Chip
{
    private const int NoiseShiftReset = 0x4000;

    // 2dB per step; the last step is off.
    private static readonly float[] Volumes = BuildVolumes();

    private readonly int[] _tonePeriod = new int[3];
    private readonly int[] _toneCounter = new int[3];
    private readonly bool[] _toneOutput = [true, true, true];
    private readonly byte[] _attenuation = [15, 15, 15, 15];

    private int _noiseControl;
    private int _noiseCounter;
    private bool _noiseOutput;
    private int _noiseShift = NoiseShiftReset;

    // The register the next data byte (bit 7 clear) goes to.
    private int _latchedRegister;

    private bool _clock;
    private int _prescaler;
    private bool _weBar = true;

    /// <summary>The data bus, sampled on /WE's falling edge.</summary>
    public byte Data { private get; set; }

    /// <summary>Chip enable, active low.</summary>
    public bool CeBar { private get; set; }

    /// <summary>
    /// Write enable, active low. Taking it low latches <see cref="Data"/>
    /// while /CE is low.
    /// </summary>
    public bool WeBar
    {
        set
        {
            var falling = _weBar && !value;
            _weBar = value;

            if (falling && !CeBar)
            {
                Write(Data);
            }
        }
    }

    /// <summary>The clock input; the chip steps on every sixteenth rising edge.</summary>
    public bool Clock
    {
        set
        {
            var rising = value && !_clock;
            _clock = value;

            if (rising && ++_prescaler == 16)
            {
                _prescaler = 0;
                Step();
            }
        }
    }

    /// <summary>
    /// Counts the internal steps taken (a sixteenth of the clock rate), so a
    /// caller can tell when <see cref="Output"/> has moved.
    /// </summary>
    public uint Steps { get; private set; }

    /// <summary>
    /// The mixed output, from 0 to 4 (all four channels high at full
    /// volume).
    /// </summary>
    public float Output { get; private set; }

    private static float[] BuildVolumes()
    {
        var volumes = new float[16];

        for (var step = 0; step < 15; step++)
        {
            volumes[step] = (float)Math.Pow(10, -0.1 * step);
        }

        return volumes;
    }

    private void Write(byte value)
    {
        if ((value & 0x80) != 0)
        {
            _latchedRegister = (value >> 4) & 7;
            WriteRegister(value & 0x0F, firstByte: true);
        }
        else
        {
            WriteRegister(value & 0x3F, firstByte: false);
        }
    }

    private void WriteRegister(int data, bool firstByte)
    {
        var channel = _latchedRegister >> 1;
        var isAttenuation = (_latchedRegister & 1) != 0;

        if (isAttenuation)
        {
            _attenuation[channel] = (byte)(data & 0x0F);
        }
        else if (channel == 3)
        {
            _noiseControl = data & 7;
            _noiseShift = NoiseShiftReset;
        }
        else if (firstByte)
        {
            _tonePeriod[channel] = (_tonePeriod[channel] & 0x3F0) | data;
        }
        else
        {
            _tonePeriod[channel] = (_tonePeriod[channel] & 0x00F) | (data << 4);
        }
    }

    private void Step()
    {
        var tone2Rose = false;

        for (var channel = 0; channel < 3; channel++)
        {
            if (--_toneCounter[channel] > 0)
            {
                continue;
            }

            // A period of zero counts as the longest, 1024.
            _toneCounter[channel] = _tonePeriod[channel] == 0 ? 0x400 : _tonePeriod[channel];
            _toneOutput[channel] = !_toneOutput[channel];

            if (channel == 2 && _toneOutput[2])
            {
                tone2Rose = true;
            }
        }

        // The noise generator's shift register is clocked by the rising edge
        // of a flip-flop toggled by its own counter, or by tone 2's output.
        var noiseShifts = false;

        if ((_noiseControl & 3) == 3)
        {
            noiseShifts = tone2Rose;
        }
        else if (--_noiseCounter <= 0)
        {
            _noiseCounter = 0x10 << (_noiseControl & 3);
            _noiseOutput = !_noiseOutput;
            noiseShifts = _noiseOutput;
        }

        if (noiseShifts)
        {
            var feedback = (_noiseControl & 4) != 0
                ? ((_noiseShift ^ (_noiseShift >> 3)) & 1)
                : (_noiseShift & 1);
            _noiseShift = (_noiseShift >> 1) | (feedback << 14);
        }

        var output = 0f;

        for (var channel = 0; channel < 3; channel++)
        {
            if (_toneOutput[channel])
            {
                output += Volumes[_attenuation[channel]];
            }
        }

        if ((_noiseShift & 1) != 0)
        {
            output += Volumes[_attenuation[3]];
        }

        Output = output;
        Steps++;
    }
}
