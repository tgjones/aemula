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

    private Cartridge? _cartridge;

    internal Mos6507 Cpu => _cpu;
    //internal VideoOutput VideoOutput => _videoOutput;

    public Atari2600System()
    {
        _cpu = new Mos6507();
        _riot = new Mos6532Chip();
        _tia = new TiaChip();

        _television = new Television();

        // TODO: Remove this - it sets B&W pin to Color.
        _riot.DB = 0b1000;

        if (File.Exists("ntsc.tv"))
        {
            File.Delete("ntsc.tv");
        }

        _ntscWriter = new BinaryWriter(File.OpenWrite("ntsc.tv"));
    }

    public override void Reset()
    {
        _cpu.Res = false;
        _cpu.Res = true;
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

            // Mos6532Chip.Cycle()/CpuCycle() were replaced by the single
            // edge-triggered Phi2 property (Phase 1b) - the falling edge
            // always ticks the interval timer (old Cycle()), the rising
            // edge does a CS-gated RAM/register access (old CpuCycle()).
            // DoCpuCycle() above already set CS1/CS2 per the address-decode
            // switch, same as before this phase.
            _riot.Phi2 = true;
            _riot.Phi2 = false;

            if (_riot.CS1)
            {
                // RIOT's data pins are fully bidirectional (unlike TIA's D6/D7-only),
                // so a selected access can overwrite the whole data bus.
                _cpu.Data = _riot.DB;
            }
        }

        // TiaChip.Cycle() was replaced by the edge-triggered Osc property
        // (Phase 1) - pulse it low->high to run one color-clock tick, same
        // cadence as the old unconditional _tia.Cycle() call. TIA doesn't
        // drive the CPU clock yet (that's Phase 3) - this system still
        // manages _tiaCycle/DoCpuCycle itself, same as before this phase.
        _tia.Osc = false;
        _tia.Osc = true;

        _television.Signal(
            new TelevisionSignal(
                _tia.Sync,
                _tia.Blk,
                false,
                (byte)(_tia.Lum & 0b111 | (_tia.Col & 0xF) << 3)));

        _tiaCycle++;

        if (_tiaCycle == 3)
        {
            _tiaCycle = 0;
        }

        // TIA can pause CPU.
        _cpu.Rdy = _tia.Rdy;

        // Prepare composite video output.
        byte ntscSignal;
        if (_tia.Sync)
        {
            ntscSignal = 0;
        }
        else if (_tia.Blk)
        {
            ntscSignal = ConvertRange(0, 140, 0, 240, 40);
        }
        else
        {
            ntscSignal = ConvertRange(0, 7, (byte)(45 / 140.0f * 240.0f), 240, _tia.Lum);
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
        _cpu.Phi0 = false;
        _cpu.Phi0 = true;

        var address = _cpu.Address;

        if (_cpu.Sync)
        {
            _lastPC = address;
        }

        // Decode which chips are selected based on A7 and A12.
        var address_7_12 = address & 0b0001000010000000;

        // Default RIOT to not-selected; the RIOT case below asserts it.
        // Real address-bit-driven CS wiring (CS1<-A7, CS2<-A12) is Phase 3's
        // job - for now this switch is still what decides "is RIOT selected".
        _riot.CS1 = false;
        _riot.CS2 = true;

        switch (address_7_12)
        {
            case 0b0000000010000000: // RIOT (A7 hi, A12 lo)
                _riot.RS = GetBitAsBoolean(address, 9); // RIOT RS is connected to A9.
                _riot.RW = _cpu.RW;                     // RIOT RW is connected to CPU RW.
                _riot.A = (byte)(address & 0b1111111);  // RIOT Address pins are connected to A0..A6.
                _riot.DB = _cpu.Data;

                _riot.CS1 = true;
                _riot.CS2 = false;

                break;

            case 0b0000000000000000: // TIA (A7 lo, A12 lo)
                _tia.RW = _cpu.RW;                         // TIA RW is connected to CPU RW.
                _tia.Address = (byte)(address & 0b111111); // TIA Address pins are connected to A0..A5.
                _tia.Data05 = (byte)(_cpu.Data & 0x3F);
                _tia.Data67 = (byte)(_cpu.Data >> 6);

                // TiaChip.CpuCycle() was replaced by the CS-gated, edge-triggered
                // Phi2 property (Phase 1). Real address-bit-driven CS wiring is
                // Phase 3's job - for now this switch is still what decides "is
                // TIA selected", so just drive the CS pins to TIA's selected
                // combination and pulse Phi2, same cadence as the old call.
                _tia.CS0 = false;
                _tia.CS1 = true;
                _tia.CS2 = false;
                _tia.CS3 = false;
                _tia.Phi2 = false;
                _tia.Phi2 = true;

                // On the TIA data pins, only pins 6 and 7 are bidirectional,
                // so we combine those with the existing value on the CPU data bus.
                _cpu.Data = (byte)(_cpu.Data & 0x3F | _tia.Data67 << 6);
                break;
        }

        if (_cpu.RW)
        {
            // If a cartridge is plugged in, always give it a chance to provide data.
            if (_cartridge != null)
            {
                _cartridge.Pins.A = (ushort)(_cpu.Address & 0x1FFF);
                _cartridge.Pins.D = _cpu.Data;

                _cartridge.Cycle();

                _cpu.Data = _cartridge.Pins.D;
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

    internal void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        Cpu.CreateDebuggerWindows(result);

        _tia.CreateDebuggerWindows(result);
    }

    protected override void OnDispose()
    {
        _ntscWriter.Dispose();
    }
}
