using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Emulation.Chips;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.Emulation.Chips.Mos6820;
using Aemula.Emulation.Peripherals;
using Aemula.Emulation.Systems.AppleI.Cassette;
using Aemula.Emulation.Systems.AppleI.Debugging;
using Aemula.Emulation.Systems.AppleI.Roms;

namespace Aemula.Emulation.Systems.AppleI;

// The processor sheet: CPU, RAM, Monitor ROM, PIA and the 74154 chip-select
// decoder. The terminal sheet - video timing, the shift-register character
// memory and the composite output - is the other partial files.
public sealed partial class AppleISystem : EmulatedSystem
{
    // The board's master oscillator: 4x the NTSC colour subcarrier
    // (3.579545MHz), the same crystal AppleIISystem ticks at (ZQ1 on the
    // schematic, 14.31818MHz). The CPU clock (1.022727MHz, exactly 2/7 of
    // the subcarrier) and the video dot clock are both synchronous
    // divisions of this one oscillator - see the plan's "Composite video"
    // section.
    public override ulong CyclesPerSecond => 14_318_180;

    public readonly Mos6502Chip Cpu;

    // The two onboard 4K DRAM banks (ICB11-18 and ICA11-18). Bank A is fixed at
    // $0000-$0FFF. Bank B's jumper block maps it either just above bank A at
    // $1000-$1FFF, for a contiguous 8K, or up at $E000-$EFFF, the split Integer
    // BASIC needs - one or the other, so the range not chosen is unpopulated.
    // Only these two banks exist on the board; more RAM than 8K meant a card on
    // the expansion connector. Each bank is a flat array behind its chip-select
    // (the banks share one M0-M7 data bus on real hardware, safe because CS0/CS1
    // are mutually exclusive); the row/column-multiplexing 74157s (ICB5-ICB8)
    // and their RAS'/CAS' pulses only matter once DRAM refresh rides on the
    // video scan, so they're not modelled as chip instances here - same
    // reasoning AppleIISystem uses for its own DRAM.
    private readonly byte[] _bankA = new byte[0x1000];
    private readonly byte[] _bankB = new byte[0x1000];

    // Whether bank B answers at $E000-$EFFF (true) or $1000-$1FFF (false).
    private readonly bool _bankBAtE000;

    // The cabled devices the fitted expansion card brings with it (the ACI
    // card's cassette deck), or empty. Built and patched by the rig assembler.
    private readonly IReadOnlyList<PeripheralRequest> _peripheralRequests = [];

    // The Monitor ROM (WozMon). ICA1/ICA2 only have 8 address pins (A0-A7),
    // so on real hardware the 256-byte image mirrors every page of the CSF
    // block below - $FF00-$FFFF (where the 6502's vectors live) is just the
    // top instance of that mirroring, not a separately decoded range.
    private readonly byte[] _rom = WozMonitor.Image;

    // ICD2: the character generator, a Signetics 2513 - see
    // Emulation/Chips/Roms/README.txt for why this is a shared chip class
    // rather than data private to this system (the Apple II sockets the
    // literal same physical part).
    private readonly Signetics2513Chip _characterGenerator;

    public readonly Mos6820Chip Pia;

    // ICB9: the chip-select generator - a single 4-to-16 decoder over
    // A12-A15 dividing the whole 64K map into 4K blocks (Y0-Y15 = CS0-CSF).
    // Y0/Y1 (jumpers X/W) select the two RAM banks, Y13 (jumper Z) the PIA,
    // Y15 (jumper Y) the ROM; the rest are unpopulated expansion blocks.
    private readonly Ttl74154Chip _chipSelectDecoder;

    // Whatever is plugged into the board's one expansion connector, or null for
    // a bare board. Driven once per CPU bus cycle from DoCpuMemoryAccess - the
    // connector carries no clock of its own.
    private readonly IExpansionCard? _expansionCard;

    public AppleISystem()
        : this(AppleISystemOptions.Default, ExpansionSlotConfiguration.Empty)
    {
    }

    public AppleISystem(AppleISystemOptions options)
        : this(options, ExpansionSlotConfiguration.Empty)
    {
    }

    public AppleISystem(ExpansionSlotConfiguration expansionSlots)
        : this(AppleISystemOptions.Default, expansionSlots)
    {
    }

    public AppleISystem(AppleISystemOptions options, ExpansionSlotConfiguration expansionSlots)
    {
        Cpu = new Mos6502Chip(Mos6502Options.Default);
        Pia = new Mos6820Chip();
        _chipSelectDecoder = new Ttl74154Chip();

        _characterGenerator = Signetics2513Chip.Load();
        _characterGenerator.ChipEnable = false; // Tied low - always enabled.

        _bankBAtE000 = options.BankB == AppleIBankBMapping.AtE000;

        // The one expansion connector: at most one card, chosen through the
        // "expansion" slot. The card carries no timing element of its own and is
        // driven once per CPU bus cycle from DoCpuMemoryAccess; anything it
        // brings cabled to it (the ACI card's cassette deck) is built and
        // patched later by the rig assembler.
        var binding = AppleIExpansionSlots.FindBinding(
            expansionSlots.ResolvedAgainst(AppleIExpansionSlots.Catalog)[AppleIExpansionSlots.ExpansionSlotId]);
        if (binding != null)
        {
            var installation = binding.Install();
            _expansionCard = installation.Card;
            _cassetteCard = _expansionCard as AppleCassetteInterfaceCard;
            _peripheralRequests = installation.Peripherals;
        }

        InitializeVideoTiming();

        Cpu.Res = false;
        Cpu.Res = true;

        Pia.Res = false;
        Pia.Res = true;

        // Power-on only: seed the recirculating character/cursor rings (they
        // free-run from power-on and the RESET key never touches them - see
        // Reset()).
        ResetCharacterMemory();

        InitializeConsoleControls();
    }

    public override IReadOnlyList<PeripheralRequest> PeripheralRequests => _peripheralRequests;

    public override void Reset()
    {
        // The RESET key (and the UI's Reset command) just pulses the 6502 and
        // PIA reset lines. The video counters and the shift-register character
        // memory free-run from power-on and are left alone - like the real
        // board, where RESET doesn't clear the screen (that's the CLEAR SCREEN
        // key), and re-seeding them mid-run would knock the recirculating rings
        // out of phase with the still-running counters.
        Cpu.Res = false;
        Cpu.Res = true;

        Pia.Res = false;
        Pia.Res = true;

        _expansionCard?.Reset();
    }

    public override void Tick()
    {
        // One master-oscillator tick. Drives Cpu.Phi0 off the real
        // character clock and, on its rising edge, calls DoCpuMemoryAccess()
        // - see AppleISystem.VideoTiming.cs.
        TickVideoTiming();
    }

    private void DoCpuMemoryAccess()
    {
        var address = Cpu.Address;

        SetChipSelectDecoderAddress(address);

        // Cs0/Cs2 aren't traceable past the 74154 on the rendered schematic
        // tiles, so they're assumed tied to their inactive-safe levels
        // (high/low) and CSD (Cs1) alone gates selection.
        var piaSelected = !_chipSelectDecoder.Y13;

        Pia.Rs0 = (address & 0x01) != 0;
        Pia.Rs1 = (address & 0x02) != 0;
        Pia.RW = Cpu.RW;
        Pia.Cs0 = true;
        Pia.Cs1 = piaSelected;
        Pia.Cs2 = false;

        if (piaSelected)
        {
            // DB is only driven from the shared bus while actually selected,
            // so it's otherwise left holding whatever the PIA last really
            // answered (what ReadByteDebug/WriteByteDebug read back later,
            // without re-triggering a live access, rather than whatever
            // unrelated ROM/RAM byte happened to cross the bus most
            // recently).
            Pia.DB = Cpu.Data;
        }

        // E (pin 25) is tied to the free-running phi2 clock (net O2), so it
        // pulses every CPU cycle whether or not the PIA is addressed. Only a
        // selected cycle commits a register access (rising edge = read,
        // falling edge = write), but the edges still advance the CA2/CB2
        // handshake state machine on unselected cycles - which is what lets
        // the CB2 strobe armed by a display write fall on the very next
        // cycle, instead of stalling until the next PIA access and being
        // missed by WozMon's back-to-back busy poll.
        Pia.E = false;
        Pia.E = true;
        Pia.E = false;

        // The expansion connector: the raw bus, pulsed once per CPU cycle. A
        // card that decodes this address drives the data bus and asserts
        // DrivesData; the motherboard's open-bus value is used otherwise.
        if (_expansionCard != null)
        {
            _expansionCard.Address = address;
            _expansionCard.RW = Cpu.RW;
            _expansionCard.Rdy = true;
            _expansionCard.ResetBar = Cpu.Res;
            _expansionCard.DmaBar = true;
            _expansionCard.DataBus = Cpu.Data;
            _expansionCard.Phi2 = false;
            _expansionCard.Phi2 = true;
            _expansionCard.Phi2 = false;
        }

        if (Cpu.RW)
        {
            Cpu.Data = ReadByte(address);

            if (_expansionCard is { DrivesData: true })
            {
                Cpu.Data = _expansionCard.DataBus;
            }
        }
        else
        {
            WriteByte(address, Cpu.Data);
        }
    }

    private void SetChipSelectDecoderAddress(ushort address)
    {
        _chipSelectDecoder.A = (address & 0x1000) != 0; // A12
        _chipSelectDecoder.B = (address & 0x2000) != 0; // A13
        _chipSelectDecoder.C = (address & 0x4000) != 0; // A14
        _chipSelectDecoder.D = (address & 0x8000) != 0; // A15
        _chipSelectDecoder.G1 = false; // Tied low - always enabled.
        _chipSelectDecoder.G2 = false; // Tied low - always enabled.
    }

    private byte ReadByte(ushort address)
    {
        if (!_chipSelectDecoder.Y0)
        {
            return _bankA[address & 0x0FFF];
        }

        if (!_chipSelectDecoder.Y1 && !_bankBAtE000)
        {
            return _bankB[address & 0x0FFF];
        }

        if (!_chipSelectDecoder.Y14 && _bankBAtE000)
        {
            return _bankB[address & 0x0FFF];
        }

        if (!_chipSelectDecoder.Y13)
        {
            // The PIA's own chip-select input beyond RS0/RS1 is just this
            // one 4K block strobe, so it responds identically at every
            // 4-byte-aligned offset in $D000-$DFFF - $D010-$D013
            // (KBD/KBDCR/DSP/DSPCR) is simply the instance of that
            // mirroring the Monitor ROM actually uses.
            return Pia.DB;
        }

        if (!_chipSelectDecoder.Y15)
        {
            return _rom[address & 0xFF];
        }

        return 0xFF;
    }

    private void WriteByte(ushort address, byte value)
    {
        if (!_chipSelectDecoder.Y0)
        {
            _bankA[address & 0x0FFF] = value;
        }
        else if (!_chipSelectDecoder.Y1 && !_bankBAtE000)
        {
            _bankB[address & 0x0FFF] = value;
        }
        else if (!_chipSelectDecoder.Y14 && _bankBAtE000)
        {
            _bankB[address & 0x0FFF] = value;
        }

        // A PIA write already happened above, as a side effect of Pia.E
        // falling with Cs1/DB set for this address - nothing further to do
        // here. The ROM can't be written.
    }

    internal byte ReadByteDebug(ushort address)
    {
        if (_expansionCard is AppleCassetteInterfaceCard card &&
            card.PeekDebug(address) is byte value)
        {
            return value;
        }

        SetChipSelectDecoderAddress(address);

        return ReadByte(address);
    }

    internal void WriteByteDebug(ushort address, byte value)
    {
        SetChipSelectDecoderAddress(address);

        WriteByte(address, value);
    }

    public override Debugger CreateDebugger()
    {
        return new AppleIDebugger(this);
    }
}
