using Aemula.Emulation.Chips.Ricoh2A03;
using Aemula.Emulation.Chips.Ricoh2C02;
using Aemula.Emulation.Systems.Nes.Debugging;
using Aemula.Debugging;

namespace Aemula.Emulation.Systems.Nes;

public sealed partial class NesSystem : EmulatedSystem
{
    public override ulong CyclesPerSecond => 21477272;

    private readonly byte[] _ram;

    // The console's own 2 KB name-table SRAM (CIRAM). Not on the cartridge - the
    // cart only drives its CIRAM A10 / CIRAM /CE pins (see Cartridge).
    private readonly byte[] _ciram;

    private Cartridge? _cartridge;

    // The mainboard's address latch (74LS373) demuxing PPU AD0-7, feeding the
    // CIRAM lookup below.
    private byte _ciramAddressLatch;

    private bool _lastCpuPhi2;

    private bool _lastPpuRd;
    private bool _lastPpuWr;

    public readonly Ricoh2A03Chip Cpu;

    public readonly Ricoh2C02Chip Ppu;

    public NesSystem()
    {
        Cpu = new Ricoh2A03Chip();

        Ppu = new Ricoh2C02Chip();

        _ram = new byte[0x0800];
        _ciram = new byte[0x0800];

        // Reset CPU.
        Cpu.Res = false;
        Cpu.Res = true;
    }

    /// <summary>
    /// When false, <see cref="Tick"/> skips the composite-video decode. The NTSC
    /// decode FIR is the measured hot cost and headless test-ROM runs read their
    /// result out of memory, never off the Television. Defaults to true.
    /// </summary>
    public bool DecodeVideo { get; set; } = true;

    public override void Tick()
    {
        // One master clock cycle (21.477272 MHz). Both chips are fed the master
        // clock every tick: the 2A03's internal divide-by-12 makes the CPU phi0
        // (one CPU cycle = 12 ticks), and the 2C02's internal divide-by-4 makes
        // the dot clock (one dot = 4 ticks).
        DoCpuCycle();

        DoPpuCycle();

        if (DecodeVideo)
        {
            TickCompositeVideo();
        }

        Cpu.Nmi = Ppu.Nmi;
    }

    private void DoCpuCycle()
    {
        Cpu.Clk = false;
        Cpu.Clk = true;

        // Service the external bus once per CPU cycle, on the rising edge of the
        // core's phi2. M2 rises three master cycles earlier (a head start the
        // 2A03 gives slow carts), but the 6502 only drives the write value onto
        // the data pins when phi2 goes high - sampling at M2 rising would latch
        // the previous fetch byte instead. phi2 rising is also the phase the
        // cycle-accurate Ricoh2A03 pin test performs its own bus access on, and
        // the core has not yet advanced the address bus to the next cycle there.
        var cpuPhi2 = Cpu.CpuCorePhi2;
        var cpuPhi2Rising = cpuPhi2 && !_lastCpuPhi2;
        _lastCpuPhi2 = cpuPhi2;
        if (!cpuPhi2Rising)
        {
            return;
        }

        var address = Cpu.Address;

        // The 3 high bits dictate which chips are selected.
        var a13_a15 = address >> 13;

        switch (a13_a15)
        {
            case 0b000: // Internal RAM. Only address pins A0..A10 are connected.
                if (Cpu.RW)
                {
                    Cpu.Data = _ram[address & 0x7FF];
                }
                else
                {
                    _ram[address & 0x7FF] = Cpu.Data;
                }
                break;

            case 0b001: // PPU ports. Only address pins A0..A2 are connected.
                Ppu.CpuRw = Cpu.RW;
                Ppu.CpuAddress = (byte)(address & 0x7);
                Ppu.CpuData = Cpu.Data;
                Ppu.Dbe = false;   // select - runs the register access
                Ppu.Dbe = true;    // deselect
                Cpu.Data = Ppu.CpuData;
                break;

            // $4000-$401F is mapped internally on the 2A03, except the two
            // controller ports: OUT0..OUT2 and the serial /IN0 /IN1 lines are
            // 2A03 pins, but the pad's shift register and the mainboard logic
            // that latches and clocks it are off-chip (see NesController).
            case 0b010: // $4000-$5FFF.
                switch (address)
                {
                    case 0x4016:
                        if (Cpu.RW)
                        {
                            // /IN0 -> data bit 0; bits 1..7 are open bus.
                            Cpu.Data = (byte)((Cpu.Data & 0xFE) | (_controller1.SerialData ? 1 : 0));
                            PulseControllerClock(_controller1);
                        }
                        else
                        {
                            // OUT0 drives the P/S (latch) line of both pads.
                            var latch = (Cpu.Data & 1) != 0;
                            _controller1.Latch = latch;
                            _controller2.Latch = latch;
                        }
                        break;

                    case 0x4017:
                        // Read: controller 2's /IN1 line. A write here is the
                        // APU frame counter, handled on the 2A03 (TODO).
                        if (Cpu.RW)
                        {
                            Cpu.Data = (byte)((Cpu.Data & 0xFE) | (_controller2.SerialData ? 1 : 0));
                            PulseControllerClock(_controller2);
                        }
                        break;
                }
                break;

            case 0b011: // $6000-$7FFF - cartridge WRAM window, decoded on the cart.
            case 0b100: // $8000-$FFFF - /ROMSEL. Only address pins A0..A14 connect.
            case 0b101:
            case 0b110:
            case 0b111:
            {
                if (_cartridge is null)
                {
                    break;
                }

                // Drive the cartridge's CPU connector pins. /ROMSEL is the
                // mainboard's !(A15 & M2); the external bus is serviced on
                // phi2-high, so M2 is asserted here.
                var romSel = (address & 0x8000) != 0;
                _cartridge.SetCpuBus((ushort)(address & 0x7FFF), Cpu.RW, romSel);

                if (Cpu.RW)
                {
                    if (_cartridge.CpuData is byte cartData)
                    {
                        Cpu.Data = cartData;
                    }
                    // else: cartridge isn't driving the bus - open bus, so the
                    // last value on Cpu.Data stays.
                }
                else
                {
                    _cartridge.CpuWrite(Cpu.Data);
                }
                break;
            }
        }
    }

    private void DoPpuCycle()
    {
        // Feed the PPU the master clock. Its internal divide-by-four runs a dot
        // on every fourth tick; ALE / /RD / /WR only move on those dot
        // boundaries, so the external address/data bus is serviced off the /RD
        // and /WR falling edges the dot produces (mirroring the phi2-rising gate
        // in DoCpuCycle) rather than every master tick.
        Ppu.Clk = false;
        Ppu.Clk = true;

        // Drive the cartridge's PPU connector pins: the multiplexed AD0-7, the
        // separate A8-A13, and ALE. The cartridge latches AD0-7 on ALE itself
        // (its own 74LS373), so it never sees a pre-demuxed address.
        _cartridge?.SetPpuBus(
            (byte)Ppu.PpuAddressBus,
            (byte)((Ppu.PpuAddressBus >> 8) & 0x3F),
            Ppu.PpuAle);

        if (Ppu.PpuAle)
        {
            // The mainboard's own address latch, feeding the CIRAM lookup below.
            _ciramAddressLatch = (byte)Ppu.PpuAddressBus;
        }

        var ppuRd = Ppu.PpuRd;
        var ppuWr = Ppu.PpuWr;
        var ppuRdFalling = !ppuRd && _lastPpuRd;
        var ppuWrFalling = !ppuWr && _lastPpuWr;
        _lastPpuRd = ppuRd;
        _lastPpuWr = ppuWr;

        if (!ppuRdFalling && !ppuWrFalling)
        {
            return;
        }

        var ppuAddress = (ushort)((Ppu.PpuAddressBus & 0x3F00) | _ciramAddressLatch);

        // CIRAM /CE: the console name-table SRAM answers for $2000-$3FFF unless
        // the cartridge holds its /CE off (4-screen &c.). Below $2000 (pattern
        // tables) the cartridge drives CHR.
        var ciramSelected = _cartridge?.CiramCe ?? ((ppuAddress & 0x2000) != 0);

        if (ciramSelected)
        {
            var offset = _cartridge?.CiramOffset(ppuAddress)
                ?? (((ppuAddress & 0x0400) != 0 ? 0x400 : 0) | (ppuAddress & 0x03FF));

            if (ppuRdFalling)
            {
                Ppu.PpuData = _ciram[offset];
            }
            else
            {
                _ciram[offset] = Ppu.PpuData;
            }
        }
        else if (_cartridge is not null)
        {
            if (ppuRdFalling)
            {
                Ppu.PpuData = _cartridge.PpuRead();
            }
            else
            {
                _cartridge.PpuWrite(Ppu.PpuData);
            }
        }
    }

    internal byte ReadByteDebug(ushort address)
    {
        // The 3 high bits dictate which chips are selected.
        var a13_a15 = address >> 13;

        return a13_a15 switch
        {
            // Internal RAM. Only address pins A0..A10 are connected.
            0b000 => _ram[address & 0x7FF],

            // $6000-$7FFF cartridge WRAM, and $8000-$FFFF PRG - the cartridge
            // decodes both (side-effect-free peek).
            0b011 or 0b100 or 0b101 or 0b110 or 0b111 => _cartridge?.PeekCpu(address) ?? 0,

            // TODO: Read from PPU registers etc.
            _ => 0,
        };
    }

    internal void WriteByteDebug(ushort address, byte value)
    {
        switch (address >> 13)
        {
            case 0b000:
                _ram[address & 0x7FF] = value;
                break;

            case 0b011:
            case 0b100:
            case 0b101:
            case 0b110:
            case 0b111:
                _cartridge?.PokeCpu(address, value);
                break;
        }
    }

    /// <summary>
    /// Side-effect-free read of the console name-table SRAM for a raw
    /// $2000-$3FFF PPU address, resolved through the cartridge's mirroring.
    /// </summary>
    internal byte PeekCiram(ushort address)
    {
        var offset = _cartridge?.CiramOffset(address)
            ?? (((address & 0x0400) != 0 ? 0x400 : 0) | (address & 0x03FF));
        return _ciram[offset];
    }

    public override void LoadProgram(string filePath)
    {
        var cartridge = Cartridge.FromFile(filePath);
        InsertCartridge(cartridge);

        Reset();
    }

    private void InsertCartridge(Cartridge cartridge)
    {
        _cartridge = cartridge;

        RaiseProgramLoaded();
    }

    public override void Reset()
    {
        Cpu.Res = false;
        Cpu.Res = true;
    }

    internal byte ReadChrRom(ushort address)
    {
        return _cartridge?.PeekPpu(address) ?? 0;
    }

    public override Debugger CreateDebugger()
    {
        return new NesDebugger(this);
    }
}
