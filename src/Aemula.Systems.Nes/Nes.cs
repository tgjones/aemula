using Aemula.Chips.Ricoh2A03;
using Aemula.Chips.Ricoh2C02;
using Aemula.Systems.Nes.Debugging;
using Aemula.Core;
using Aemula.Core.Debugging;

namespace Aemula.Systems.Nes;

public sealed class Nes : EmulatedSystem
{
    public override ulong CyclesPerSecond => 5369318;

    private readonly byte[] _ram;
    private readonly byte[] _vram;

    private Cartridge _cartridge;

    private byte _ppuCycle;
    private byte _vramLowAddressLatch;

    public readonly Ricoh2A03 Cpu;

    public readonly Ricoh2C02 Ppu;

    public Nes()
    {
        Cpu = new Ricoh2A03();

        Ppu = new Ricoh2C02();

        _ram = new byte[0x0800];
        _vram = new byte[0x0800];
    }

    public override void Tick()
    {
        if (_ppuCycle == 0)
        {
            DoCpuCycle();
        }

        DoPpuCycle();

        _ppuCycle++;

        if (_ppuCycle == 3)
        {
            _ppuCycle = 0;
        }

        Cpu.CpuCore.Pins.Nmi = Ppu.Pins.Nmi;
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
        Cpu.Cycle();

        ref var cpuPins = ref Cpu.CpuCore.Pins;
        ref var ppuPins = ref Ppu.Pins;

        var address = cpuPins.Address;

        // The 3 high bits dictate which chips are selected.
        var a13_a15 = address >> 13;

        switch (a13_a15)
        {
            case 0b000: // Internal RAM. Only address pins A0..A10 are connected.
                if (cpuPins.RW)
                {
                    cpuPins.Data = _ram[address & 0x7FF];
                }
                else
                {
                    _ram[address & 0x7FF] = cpuPins.Data;
                }
                break;

            case 0b001: // PPU ports. Only address pins A0..A2 are connected.
                ppuPins.CpuRW = cpuPins.RW;
                ppuPins.CpuAddress = (byte)(address & 0x7);
                ppuPins.CpuData = cpuPins.Data;
                Ppu.CpuCycle();
                cpuPins.Data = ppuPins.CpuData;
                break;

            // $4000-$401F is mapped internally on 2A03 chip.

            case 0b100: // ROMSEL. Only address pins A0..A14 are connected.
            case 0b101:
            case 0b110:
            case 0b111:
                // This is ROM - can't write to it.
                if (cpuPins.RW)
                {
                    // TODO: Mapper implementations.
                    // What follows is NROM-128, mapper 0.
                    cpuPins.Data = _cartridge?.PrgRom[address & 0x3FFF] ?? 0;
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

        var pa13 = (ppuPins.PpuAddressData.Address >> 13) & 1;
        var ppuAddress = (ppuPins.PpuAddressData.AddressHi << 8) | _vramLowAddressLatch;

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
        Cpu.CpuCore.Pins.Res = true;
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
