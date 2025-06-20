using System;
using System.Runtime.CompilerServices;

namespace Aemula.Systems.Nes.Ppu;

public class Ricoh2C02
{
    // Memory

    private readonly byte[] _paletteMemory;

    // Pins

    public readonly WriteOnlyPin Res;
    public readonly WriteOnlyPin Clk;
    public readonly WriteOnlyPin Dbe;
    public readonly ReadOnlyPin Int;

    public ushort VOut
    {
        get
        {
            if (VidSyncL)
            {
                return 48;
            }

            if (VidSyncH)
            {
                return 312;
            }

            if (VidBurstL)
            {
                return 148;
            }

            if (VidBurstH)
            {
                return 524;
            }

            if (VidLuma0H)
            {
                return 616;
            }

            if (VidLuma3L)
            {
                return 880;
            }

            if (VidLuma3H)
            {
                return 1100;
            }

            return 0;
        }
    }

    // Internal storage

    private bool _pixelClock;
    public bool PixelClock
    {
        get => _pixelClock;
        set
        {
            if (value == _pixelClock)
            {
                return;
            }

            _pixelClock = value;

            if (value)
            {
                // Rising edge.
                switch (HPos)
                {
                    case 257:
                        if (VPos >= 245 && VPos <= 247)
                        {
                            _state = State.SyncHigh;
                            StopEmittingColor();
                            VidSyncL = false;
                            VidSyncH = true;
                        }
                        break;

                    case 271:
                        if (VPos <= 241)
                        {
                            _state = State.SyncHigh;
                            StopEmittingColor();
                            VidSyncH = true;
                        }
                        break;

                    case 280:
                        _state = State.SyncLow;
                        VidSyncH = false;
                        VidSyncL = true;
                        break;

                    case 305:
                        if (VPos <= 243 || (VPos >= 247 && VPos <= 261))
                        {
                            _state = State.SyncHigh;
                            VidSyncL = false;
                            VidSyncH = true;
                        }
                        break;

                    case 309:
                        if (VPos <= 243 || (VPos >= 247 && VPos <= 261))
                        {
                            _state = State.ColorBurst;
                            VidSyncH = false;
                        }
                        break;

                    case 324:
                        if (VPos <= 243 || (VPos >= 247 && VPos <= 261))
                        {
                            _state = State.SyncHigh;
                            _vidBurstH = false;
                            _vidBurstL = false;
                            VidSyncH = true;
                        }
                        break;

                    case 329:
                        // Start emitting grayscale version of background color
                        if (VPos <= 240 || VPos == 261)
                        {
                            VidSyncH = false;
                            _state = State.RenderBackgroundGrayscale;
                        }
                        break;

                    case 330:
                        if (VPos <= 240 || VPos == 261)
                        {
                            _state = State.RenderBackground;
                        }
                        break;
                }
            }
            else
            {
                // Falling edge.
                HPos++;

                if (HPos == 341)
                {
                    VPos++;
                    HPos = 0;

                    if (VPos == 262)
                    {
                        VPos = 0;
                    }
                }
            }
        }
    }

    private enum State
    {
        RenderBackgroundGrayscale,
        RenderBackground,
        RenderNormal,
        ColorBurst,
        SyncHigh,
        SyncLow,
    }

    private State _state;

    private byte _pixelClockCounter;

    private byte _colorGeneratorClockCounter;
    
    private bool _vidLuma0H, _vidLuma0L;
    private bool _vidLuma1H, _vidLuma1L;
    private bool _vidLuma2H, _vidLuma2L;
    private bool _vidLuma3H, _vidLuma3L;

    private bool _vidBurstH, _vidBurstL;

    public ushort HPos { get; private set; }
    public ushort VPos { get; private set; }
    public bool VidBurstH => _vidBurstH;
    public bool VidBurstL => _vidBurstL;
    public bool VidLuma0H => _vidLuma0H;
    public bool VidLuma0L => _vidLuma0L;
    public bool VidLuma1H => _vidLuma1H;
    public bool VidLuma1L => _vidLuma1L;
    public bool VidLuma2H => _vidLuma2H;
    public bool VidLuma2L => _vidLuma2L;
    public bool VidLuma3H => _vidLuma3H;
    public bool VidLuma3L => _vidLuma3L;
    public bool VidSyncH { get; private set; }
    public bool VidSyncL { get; private set; }

    public Ricoh2C02(PinValue initialClock = PinValue.Low, bool initialPixelClock = false, byte initialPixelClockCounter = 0, ushort initialHPos = 0, ushort initialVPos = 0)
    {
        _pixelClock = initialPixelClock;
        _pixelClockCounter = initialPixelClockCounter;
        HPos = initialHPos;
        VPos = initialVPos;

        //_state = State.RenderBackground;
        _state = State.ColorBurst;

        _colorGeneratorClockCounter = 8;

        _paletteMemory = new byte[32];

        Int = new ReadOnlyPin();

        Res = new WriteOnlyPin(
            new PinHandler(PinHandlerType.Always, OnResChanged));

        Clk = new WriteOnlyPin(
            new PinHandler(PinHandlerType.Always, OnClkChanged));

        Clk.Value = initialClock;

        Dbe = new WriteOnlyPin(
            new PinHandler(PinHandlerType.Always, OnDbeChanged));
    }

    private void OnResChanged(PinValue value)
    {
        Int.Value = PinValue.High;
        // TODO
    }

    private void OnClkChanged(PinValue value)
    {
        if (Res.Value == PinValue.Low)
        {
            return;
        }

        if (value == PinValue.High)
        {
            // Rising edge.
            _pixelClockCounter++;
            if (_pixelClockCounter == 3)
            {
                _pixelClockCounter = 1;
                PixelClock = !PixelClock;
            }
        }
        else
        {
            // Falling edge.
        }

        UpdateOutput();

        _colorGeneratorClockCounter++;
        if (_colorGeneratorClockCounter == 12)
        {
            _colorGeneratorClockCounter = 0;
        }
    }

    private void OnDbeChanged(PinValue value)
    {
        // TODO
    }

    public void SetPaletteMemory(byte offset, byte value)
    {
        _paletteMemory[offset] = value;
        UpdateOutput();
    }

    private void UpdateOutput()
    {
        switch (_state)
        {
            case State.RenderBackgroundGrayscale:
                EmitColor(_paletteMemory[0] & 0xF0);
                break;

            case State.RenderBackground:
                EmitColor(_paletteMemory[0]);
                break;

            case State.ColorBurst:
                // Seems to be phase 3? But https://www.nesdev.org/wiki/NTSC_video says it should be phase 8.
                EmitColor(0x06, ref _vidBurstH, ref _vidBurstL);
                break;
        }
    }

    private void EmitColor(int color)
    {
        StopEmittingColor();

        var nibbleHi = color & 0xF0;
        var nibbleLo = color & 0x0F;

        switch (nibbleLo)
        {
            case 0x0E:
            case 0x0F:
                throw new NotImplementedException();
        }

        switch (nibbleHi)
        {
            case 0x00:
                EmitColor(nibbleLo, ref _vidLuma0H, ref _vidLuma0L);
                break;

            case 0x10:
                EmitColor(nibbleLo, ref _vidLuma1H, ref _vidLuma1L);
                break;

            case 0x20:
                EmitColor(nibbleLo, ref _vidLuma2H, ref _vidLuma2L);
                break;

            case 0x30:
                EmitColor(nibbleLo, ref _vidLuma3H, ref _vidLuma3L);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(color), $"Color 0x{color:X2} is not supported");
        }
    }

    private void EmitColor(int nibbleLo, ref bool lumaH, ref bool lumaL)
    {
        switch (nibbleLo)
        {
            case 0x0:
                lumaH = true;
                break;

            case 0xD:
                lumaL = true;
                break;

            default:
                var x = _colorGeneratorClockCounter - nibbleLo;
                var x_min = 0;
                var x_max = 12;
                var shiftedPhase = (((x - x_min) % (x_max - x_min)) + (x_max - x_min)) % (x_max - x_min) + x_min;
                lumaH = shiftedPhase < 6;
                lumaL = !lumaH;
                break;
        }
    }

    private void StopEmittingColor()
    {
        _vidLuma0H = false;
        _vidLuma0L = false;
        _vidLuma1H = false;
        _vidLuma1L = false;
        _vidLuma2H = false;
        _vidLuma2L = false;
        _vidLuma3H = false;
        _vidLuma3L = false;
    }
}

public enum PinValue
{
    Low,
    High,
}

public interface IPin
{

}

public interface IWriteablePin : IPin
{
    void Set(PinValue value);
}

public interface IReadablePin : IPin
{
    PinValue Get();
}

public sealed record PinHandler(PinHandlerType Type, Action<PinValue> Callback);

public enum PinHandlerType
{
    Always,
    TransitionLowToHigh,
    TransitionHighToLow,
}

public sealed class WriteOnlyPin(params PinHandler[] handlers)
    : IWriteablePin
{
    internal PinValue Value;

    public void Set(PinValue value)
    {
        CallHandlers(PinHandlerType.Always, value);

        if (value == Value)
        {
            return;
        }

        Value = value;

        if (value == PinValue.High)
        {
            CallHandlers(PinHandlerType.TransitionLowToHigh, value);
        }
        else
        {
            CallHandlers(PinHandlerType.TransitionHighToLow, value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CallHandlers(PinHandlerType type, PinValue value)
    {
        foreach (var handler in handlers)
        {
            if (handler.Type == type)
            {
                handler.Callback(value);
            }
        }
    }
}

public sealed class ReadOnlyPin
    : IReadablePin
{
    internal PinValue Value;

    public PinValue Get() => Value;
}

public readonly struct ReadWritePin(Func<PinValue> getValue, Action<PinValue> setValue) 
    : IWriteablePin, IReadablePin
{
    public PinValue Get() => getValue();
    public void Set(PinValue value) => setValue(value);
}
