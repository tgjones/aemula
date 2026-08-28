using System.Collections.Generic;
using System.IO;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6532;
using Aemula.Emulation.Chips.Tia;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems.Atari2600.Debugging;
using Aemula.UI;
using Aemula.UI.LogicAnalyzer;
using static Aemula.BitUtility;

namespace Aemula.Emulation.Systems.Atari2600;

public sealed partial class Atari2600System : EmulatedSystem, IHasTelevision
{
    // 3.58 MHZ
    public override ulong CyclesPerSecond => 3580000;

    private readonly Mos6507 _cpu;
    private readonly Mos6532Chip _riot;
    private readonly TiaChip _tia;

    private ushort _lastPC;

    private Cartridge? _cartridge;

    internal Mos6507 Cpu => _cpu;
    internal TiaChip Tia => _tia;
    //internal VideoOutput VideoOutput => _videoOutput;

    public Atari2600System()
    {
        _cpu = new Mos6507();
        _riot = new Mos6532Chip();
        _tia = new TiaChip();

        // Console switches (SWCHB, read via RIOT's PB pin - see
        // Mos6532Chip.ReadIORegister): bit 3 is the TV Type switch,
        // 1 = Color. Real hardware wires this (and the other switches)
        // straight into RIOT's PB pins - there's no CPU/TIA involvement,
        // so setting it once here at construction, rather than every
        // tick, matches how a physical switch that isn't being touched
        // actually behaves.
        _riot.PB = 0b1000;
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
            // Real RIOT only has 7 address pins (A0..A6) wired up - same
            // masking DoAddressDecode applies to _riot.A below - so mask
            // here too rather than relying on Mos6532Chip to reject an
            // address outside its own RAM.
            return _riot.ReadByteDebug((ushort)(address & 0b1111111));
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
        // TIA generates the CPU clock, not the other way around: one master
        // tick is one 3.579545MHz OSC pulse into TIA, which divides it by 3
        // internally (TiaChip.Osc's setter) and drives its own Phi0 output
        // from that division.
        _tia.Osc = false;
        _tia.Osc = true;

        // Plain getter->setter propagation of TIA's Phi0 output into the
        // 6507's Phi0 input, same pattern as the _cpu.Rdy = _tia.Rdy line
        // below.
        _cpu.Phi0 = _tia.Phi0;

        DoAddressDecode();

        // Render this colour clock's pixel only now - after DoAddressDecode
        // has driven any 6507 write for this tick into TIA (TiaChip.Phi2). A
        // store to a graphics/colour/playfield register must be visible on
        // the colour clock it completes on, not the next one; rendering
        // before this point clipped the leading edge of digits in Pitfall's
        // race-the-beam score kernel.
        _tia.RenderColorClock();

        TickCompositeVideo();

        // TIA can pause CPU.
        _cpu.Rdy = _tia.Rdy;
    }

    private void DoAddressDecode()
    {
        var address = _cpu.Address;

        if (_cpu.Sync)
        {
            _lastPC = address;
        }

        // Every chip sees the whole bus every cycle, same as real hardware -
        // no address-range switch deciding which chip's cycle method to
        // call. Each chip's own CS pins (driven here straight from address
        // bits, matching their real wiring) are what make it selective.
        _tia.RW = _cpu.RW;                         // TIA RW is connected to CPU RW.
        _tia.Address = (byte)(address & 0b111111); // TIA Address pins are connected to A0..A5.
        _tia.Data05 = (byte)(_cpu.Data & 0x3F);
        _tia.Data67 = (byte)(_cpu.Data >> 6);
        _tia.CS0 = GetBitAsBoolean(address, 12); // CS0 <- A12 (active low).
        _tia.CS1 = true;                         // Tied to +5V (active high).
        _tia.CS2 = false;                        // Tied to GND (active low).
        _tia.CS3 = GetBitAsBoolean(address, 7);  // CS3 <- A7 (active low).

        _riot.RS = GetBitAsBoolean(address, 9); // RIOT RS is connected to A9.
        _riot.RW = _cpu.RW;                     // RIOT RW is connected to CPU RW.
        _riot.A = (byte)(address & 0b1111111);  // RIOT Address pins are connected to A0..A6.
        _riot.DB = _cpu.Data;
        _riot.CS1 = GetBitAsBoolean(address, 7);  // CS1 <- A7 (active high).
        _riot.CS2 = GetBitAsBoolean(address, 12); // CS2 <- A12 (active low).

        // Both chips decide for themselves (via their own CS-gated Phi2
        // setter) whether this edge means "do a register/RAM access".
        _tia.Phi2 = _cpu.Phi2;
        _riot.Phi2 = _cpu.Phi2;

        if (_cpu.RW)
        {
            // On the TIA data pins, only pins 6 and 7 are bidirectional,
            // so we combine those with the existing value on the CPU data bus.
            _cpu.Data = (byte)(_cpu.Data & 0x3F | _tia.Data67 << 6);

            // RIOT's data pins are fully bidirectional (unlike TIA's
            // D6/D7-only), so only let a selected RIOT overwrite the whole
            // data bus - an unselected RIOT's DB is stale.
            if (_riot.CS1 && !_riot.CS2)
            {
                _cpu.Data = _riot.DB;
            }

            // If a cartridge is plugged in, always give it a chance to provide data.
            if (_cartridge != null)
            {
                // Cartridge.Cycle() was replaced by the combinational Address
                // property - no cycle call needed, just read Data back
                // afterward. Data is null (high-impedance) whenever A12
                // isn't asserted, so the null-check here is what keeps a
                // not-selected access from stomping _cpu.Data with cartridge
                // output - the "am I selected" logic lives entirely in
                // Cartridge itself now, not duplicated at this call site.
                _cartridge.Address = (ushort)(address & 0x1FFF);

                if (_cartridge.Data is byte cartridgeData)
                {
                    _cpu.Data = cartridgeData;
                }
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

    internal IReadOnlyList<ChannelNode> CreateChannelNodes()
    {
        return
        [
            Cpu.CreateChannelGroup(),
            _tia.CreateChannelGroup(),
            _riot.CreateChannelGroup(),
            new ChannelGroup("Composite Video",
            [
                Channel.Analog("Composite Video", () => CurrentCompositeVideoSample, SyncLevel, WhiteLevel, ""),
            ]),
        ];
    }
}
