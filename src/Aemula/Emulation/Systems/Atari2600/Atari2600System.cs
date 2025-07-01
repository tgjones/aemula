using System.Collections.Generic;
using System.IO;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6532;
using Aemula.Emulation.Chips.Tia;
using Aemula.Emulation.Systems.Atari2600.Debugging;
using Aemula.UI;
using static Aemula.BitUtility;

namespace Aemula.Emulation.Systems.Atari2600;

public sealed class Atari2600System : EmulatedSystem
{
    // 3.58 MHZ
    public override ulong CyclesPerSecond => 3580000;

    private readonly BinaryWriter _ntscWriter;

    private readonly Mos6507 _cpu;
    private readonly Mos6532Chip _riot;
    private readonly TiaChip _tia;
    private readonly Television _television;

    private byte _tiaCycle;

    private ushort _lastPC;

    private Cartridge _cartridge;

    internal Mos6507 Cpu => _cpu;
    //internal VideoOutput VideoOutput => _videoOutput;

    public Atari2600System()
    {
        _cpu = new Mos6507();
        _riot = new Mos6532Chip();
        _tia = new TiaChip();

        _television = new Television();

        // TODO: Remove this - it sets B&W pin to Color.
        _riot.Pins.DB = 0b1000;

        if (File.Exists("ntsc.tv"))
        {
            File.Delete("ntsc.tv");
        }

        _ntscWriter = new BinaryWriter(File.OpenWrite("ntsc.tv"));
    }

    public override void Reset()
    {
        _cpu.Pins.Res = true;
    }

    internal byte ReadByteDebug(ushort address)
    {
        if ((address & 0x1000) != 0)
        {
            if (_cartridge != null)
            {
                return _cartridge.ReadByteDebug(address);
            }
            else
            {
                return 0;
            }
        }
        else if ((address & 0x1280) == 0x80)
        {
            return _riot.ReadByteDebug(address);
        }
        else
        {
            return 0;
        }
    }

    internal void WriteByteDebug(ushort address, byte value)
    {
        // TODO
    }

    public override void LoadProgram(string filePath)
    {
        var cartridgeData = File.ReadAllBytes(filePath);
        _cartridge = Cartridge.FromData(cartridgeData);

        RaiseProgramLoaded();
    }

    public override void Tick()
    {
        if (_tiaCycle == 0)
        {
            DoCpuCycle();

            _riot.Cycle();
        }

        _tia.Cycle();

        _television.Signal(
            new TelevisionSignal(
                _tia.Pins.Sync,
                _tia.Pins.Blk,
                false,
                (byte)(_tia.Pins.Lum & 0b111 | (_tia.Pins.Col & 0xF) << 3)));

        _tiaCycle++;

        if (_tiaCycle == 3)
        {
            _tiaCycle = 0;
        }

        // TIA can pause CPU.
        _cpu.Pins.Rdy = _tia.Pins.Rdy;

        // Prepare composite video output.
        byte ntscSignal;
        if (_tia.Pins.Sync)
        {
            ntscSignal = 0;
        }
        else if (_tia.Pins.Blk)
        {
            ntscSignal = ConvertRange(0, 140, 0, 240, 40);
        }
        else
        {
            ntscSignal = ConvertRange(0, 7, (byte)(45 / 140.0f * 240.0f), 240, _tia.Pins.Lum);
        }
        for (var i = 0; i < 4; i++)
        {
            _ntscWriter.Write(ntscSignal);
        }
    }

    private static byte ConvertRange(
        byte originalStart, byte originalEnd, // original range
        byte newStart, byte newEnd, // desired range
        byte value) // value to convert
    {
        var scale = (float)(newEnd - newStart) / (originalEnd - originalStart);
        return (byte)(newStart + (value - originalStart) * scale);
    }

    private void DoCpuCycle()
    {
        _cpu.Tick();

        var address = _cpu.Pins.Address;

        if (_cpu.Pins.Sync)
        {
            _lastPC = address;
        }

        // Decode which chips are selected based on A7 and A12.
        var address_7_12 = address & 0b0001000010000000;

        switch (address_7_12)
        {
            case 0b0000000010000000: // RIOT (A7 hi, A12 lo)
                _riot.Pins.RS = GetBitAsBoolean(address, 9); // RIOT RS is connected to A9.
                _riot.Pins.RW = _cpu.Pins.RW;                // RIOT RW is connected to CPU RW.
                _riot.Pins.A = (byte)(address & 0b1111111);  // RIOT Address pins are connected to A0..A6.
                _riot.Pins.DB = _cpu.Pins.Data;

                _riot.CpuCycle();

                _cpu.Pins.Data = _riot.Pins.DB;

                break;

            case 0b0000000000000000: // TIA (A7 lo, A12 lo)
                _tia.Pins.RW = _cpu.Pins.RW;                    // TIA RW is connected to CPU RW.
                _tia.Pins.Address = (byte)(address & 0b111111); // TIA Address pins are connected to A0..A5.
                _tia.Pins.Data05 = (byte)(_cpu.Pins.Data & 0x3F);
                _tia.Pins.Data67 = (byte)(_cpu.Pins.Data >> 6);

                _tia.CpuCycle();

                // On the TIA data pins, only pins 6 and 7 are bidirectional,
                // so we combine those with the existing value on the CPU data bus.
                _cpu.Pins.Data = (byte)(_cpu.Pins.Data & 0x3F | _tia.Pins.Data67 << 6);
                break;
        }

        if (_cpu.Pins.RW)
        {
            // If a cartridge is plugged in, always give it a chance to provide data.
            if (_cartridge != null)
            {
                _cartridge.Pins.A = (ushort)(_cpu.Pins.Address & 0x1FFF);
                _cartridge.Pins.D = _cpu.Pins.Data;

                _cartridge.Cycle();

                _cpu.Pins.Data = _cartridge.Pins.D;
            }
        }
        else
        {
            // TODO: Write to cartridge?
        }
    }

    public override Debugger CreateDebugger()
    {
        return new Atari2600Debugger(this);
    }

    internal IEnumerable<DebuggerWindow> CreateDebuggerWindows()
    {
        foreach (var debuggerWindow in Cpu.CreateDebuggerWindows())
        {
            yield return debuggerWindow;
        }

        foreach (var debuggerWindow in _tia.CreateDebuggerWindows())
        {
            yield return debuggerWindow;
        }
    }

    protected override void OnDispose()
    {
        _ntscWriter.Dispose();
    }
}
