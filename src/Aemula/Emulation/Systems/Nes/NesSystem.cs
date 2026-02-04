using Aemula.Emulation.Chips.Ricoh2A03;
using Aemula.Emulation.Chips.Ricoh2C02;
using Aemula.Emulation.Systems.Nes.Debugging;
using Aemula.Debugging;

namespace Aemula.Emulation.Systems.Nes;

public sealed class NesSystem : EmulatedSystem
{
    public override ulong CyclesPerSecond => 21477272;

    private readonly byte[] _ram;
    private readonly byte[] _vram;

    private Cartridge? _cartridge;

    private byte _ppuCycle;
    private byte _vramLowAddressLatch;

    private bool? _lastM2;

    public readonly Ricoh2A03Chip Cpu;

    public readonly Ricoh2C02Chip Ppu;

    public NesSystem()
    {
        Cpu = new Ricoh2A03Chip();

        Ppu = new Ricoh2C02Chip();

        _ram = new byte[0x0800];
        _vram = new byte[0x0800];

        // Reset CPU.
        Cpu.Res = false;
        Cpu.Res = true;
    }

    public override void Tick()
    {
        if (_ppuCycle == 0)
        {
            DoCpuCycle();
        }

        DoPpuCycle();

        _ppuCycle++;

        if (_ppuCycle == 13)
        {
            _ppuCycle = 0;
        }

        Cpu.Nmi = Ppu.Pins.Nmi;
    }

    private void TickCpu()
    {
        do
        {
            Tick();
        } while (_ppuCycle != 0);
    }

    private void DoCpuCycle()
    {
        Cpu.Clk = false;
        Cpu.Clk = true;

        if (_lastM2 == null)
        {
            _lastM2 = Cpu.M2;
        }
        else if (_lastM2 == Cpu.M2)
        {
            return;
        }

        if (!Cpu.M2)
        {
            return;
        }

        ref var ppuPins = ref Ppu.Pins;

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
                ppuPins.CpuRW = Cpu.RW;
                ppuPins.CpuAddress = (byte)(address & 0x7);
                ppuPins.CpuData = Cpu.Data;
                Ppu.CpuCycle();
                Cpu.Data = ppuPins.CpuData;
                break;

            // $4000-$401F is mapped internally on 2A03 chip.

            case 0b100: // ROMSEL. Only address pins A0..A14 are connected.
            case 0b101:
            case 0b110:
            case 0b111:
                // This is ROM - can't write to it.
                if (Cpu.RW)
                {
                    // TODO: Mapper implementations.
                    // What follows is NROM-128, mapper 0.
                    Cpu.Data = _cartridge?.PrgRom[address & 0x3FFF] ?? 0;
                }
                break;
        }
    }

    private void DoPpuCycle()
    {
        Ppu.Cycle();

        ref var ppuPins = ref Ppu.Pins;

        if (ppuPins.PpuAle)
        {
            _vramLowAddressLatch = ppuPins.PpuAddressData.Data;
        }

        var pa13 = ppuPins.PpuAddressData.Address >> 13 & 1;
        var ppuAddress = ppuPins.PpuAddressData.AddressHi << 8 | _vramLowAddressLatch;

        if (!ppuPins.PpuRD)
        {
            if (pa13 == 1)
            {
                ppuPins.PpuAddressData.Data = _vram[ppuAddress & 0x7FF];
            }
            else
            {
                // TODO: Use mapper.
                ppuPins.PpuAddressData.Data = _cartridge?.ChrRom[ppuAddress] ?? 0;
            }
        }

        if (!ppuPins.PpuWR)
        {
            if (pa13 == 1)
            {
                _vram[ppuAddress & 0x7FF] = ppuPins.PpuAddressData.Data;
            }
            else
            {
                // Can't write to CHR ROM, maybe?
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

            // ROMSEL. Only address pins A0..A14 are connected.
            // TODO: Mapper implementations. What follows is NROM-128, mapper 0.
            0b100 or 0b101 or 0b110 or 0b111 => _cartridge?.PrgRom[address & 0x3FFF] ?? 0,

            // TODO: Read from PPU registers etc.
            _ => 0,
        };
    }

    internal void WriteByteDebug(ushort address, byte value)
    {
        // TODO
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
        // TODO: Use mapper.
        return _cartridge?.ChrRom[address] ?? 0;
    }

    public override Debugger CreateDebugger()
    {
        return new NesDebugger(this);
    }
}
