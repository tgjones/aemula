using System;
using System.Collections.Generic;
using System.IO;
using Aemula.Emulation.Chips;
using Aemula.Emulation.Chips.Intel8080;
using Aemula.Debugging;
using Aemula.Emulation.Chips.MB14241;
using Aemula.Emulation.Systems.SpaceInvaders.Debugging;
using Aemula.UI.LogicAnalyzer;
using Hexa.NET.SDL3;

namespace Aemula.Emulation.Systems.SpaceInvaders;

public sealed partial class SpaceInvadersSystem : EmulatedSystem
{
    private readonly Intel8080Chip _cpu;

    private readonly byte[] _rom;
    private readonly byte[] _ram;

    private readonly MB14241Chip _shifter;

    private byte _lastStatusWord;
    private ulong _masterClock;
    private byte _nextInterrupt;

    public override ulong CyclesPerSecond => 19968000;

    public readonly DisplayBuffer Display;

    public Intel8080Chip Cpu => _cpu;

    public SpaceInvadersSystem()
    {
        _cpu = new Intel8080Chip();

        _rom = new byte[0x2000];

        _ram = new byte[0x2000];

        _shifter = new MB14241Chip();

        _hCounterLow = new Ttl74161Chip();
        _hCounterHigh = new Ttl74161Chip();
        _vCounterLow = new Ttl74161Chip();
        _vCounterHigh = new Ttl74161Chip();
        _blankingFlipFlops = new Ttl7474Chip();
        _interruptFlipFlop = new Ttl7474Chip();

        _ramAddressMuxBits0To3 = new Ttl74157Chip();
        _ramAddressMuxBits4To7 = new Ttl74157Chip();
        _ramAddressMuxBits8To11 = new Ttl74157Chip();
        _ramAddressMuxBit12 = new Ttl74157Chip();

        Display = new DisplayBuffer(256, 256);
    }

    public override void LoadProgram(string filePath)
    {
        void LoadRom(string fileName, ushort startAddress)
        {
            var fullPath = Path.Combine("Emulation", "Systems", "SpaceInvaders", "Roms", fileName);
            using var fileStream = File.OpenRead(fullPath);
            fileStream.ReadExactly(_rom, startAddress, (int)fileStream.Length);
        }

        LoadRom("invaders.h", 0x0000);
        LoadRom("invaders.g", 0x0800);
        LoadRom("invaders.f", 0x1000);
        LoadRom("invaders.e", 0x1800);

        RaiseProgramLoaded();
    }

    public override void Tick()
    {
        _masterClock++;

        // Settles this tick's video-scanner/CPU RAM contention - and drives
        // the CPU's READY pin from it - before TickCpuClock, so READY is
        // already valid if Phi2 happens to fall on this same tick (see
        // TickRamArbitration).
        TickRamArbitration();
        TickCpuClock();
        TickVideoTiming();
    }

    // Real 8080 hardware never has a single "tick the CPU" call: Phi1 and Phi2 are two
    // independent, non-overlapping external clock inputs (see
    // docs/intel8080-half-cycle-plan.md), with Phi2's pulse markedly wider than Phi1's
    // (MCS-80/85 User's Manual AC characteristics: min 60ns Phi1 vs. min 220ns Phi2, at
    // standard 2MHz speed grade). Space Invaders' own arcade clock-generator schematic
    // wasn't located despite searching (see the plan doc's Phase 3 notes), so rather than
    // inventing a duty cycle from nothing, the four edges below are spread across this
    // system's existing 10-master-tick T-state window (~50ns/tick at this system's
    // 19.968MHz master clock) in that same order and proportion - Phi1 high for 2 ticks
    // (~100ns), a 1-tick gap, Phi2 high for 5 ticks (~250ns), a 2-tick gap - rather than
    // firing all four back-to-back at one instant like Phase 1/2 did.
    private const int Phi1RisingTick = 0;
    private const int Phi1FallingTick = 2;
    private const int Phi2RisingTick = 3;
    private const int Phi2FallingTick = 8;

    private void TickCpuClock()
    {
        switch ((_masterClock - 1) % 10)
        {
            case Phi1RisingTick:
                _cpu.Phi1 = true;

                // WR's falling edge (Phi1^ of T3, per the plan's edge protocol table) -
                // commit the write now that Data has been valid since the preceding
                // Phi2^ of T2.
                if (!_cpu.Wr)
                {
                    WriteCpuBus();
                }
                break;

            case Phi1FallingTick:
                _cpu.Phi1 = false;
                break;

            case Phi2RisingTick:
                _cpu.Phi2 = true;

                // Status word/SYNC become valid on this same edge (Phi2^ of T1).
                if (_cpu.Sync)
                {
                    _lastStatusWord = _cpu.Data;
                }

                // DBIN's rising edge (Phi2^ of T2) - supply read data right as the
                // addressed device is asked for it, rather than after the whole
                // T-state; it stays valid until DBIN's falling edge latches it.
                if (_cpu.DBIn)
                {
                    ReadCpuBus();
                }
                break;

            case Phi2FallingTick:
                _cpu.Phi2 = false;
                break;
        }
    }

    private void ReadCpuBus()
    {
        switch (_lastStatusWord)
        {
            case Intel8080Chip.StatusWordFetch:
            case Intel8080Chip.StatusWordMemoryRead:
            case Intel8080Chip.StatusWordStackRead:
                if (_cpu.Address > 0x3FFF)
                {
                    // TODO: Actually this should be a mirror of RAM?
                    throw new InvalidOperationException();
                }
                else if ((_cpu.Address & 0x2000) == 0x2000)
                {
                    _cpu.Data = _ram[_cpu.Address & 0x1FFF];
                }
                else
                {
                    _cpu.Data = _rom[_cpu.Address & 0x1FFF];
                }
                break;

            case Intel8080Chip.StatusWordInputRead:
                _cpu.Data = (_cpu.Address & 0xFF) switch
                {
                    1 => GetIOPort1Value(),
                    2 => 0, // TODO: Player inputs
                    3 => _shifter.GetResult(),
                    _ => throw new InvalidOperationException(),
                };
                break;

            case Intel8080Chip.StatusWordInterruptAcknowledge:
                _cpu.Data = _nextInterrupt;
                _cpu.Int = false;
                break;
        }
    }

    private void WriteCpuBus()
    {
        switch (_lastStatusWord)
        {
            case Intel8080Chip.StatusWordMemoryWrite:
            case Intel8080Chip.StatusWordStackWrite:
                if ((_cpu.Address & 0x2000) == 0x2000)
                {
                    _ram[_cpu.Address & 0x1FFF] = _cpu.Data;
                }
                break;

            case Intel8080Chip.StatusWordOutputWrite:
                switch (_cpu.Address & 0xFF)
                {
                    case 2:
                        _shifter.SetShiftCount(_cpu.Data);
                        break;

                    case 3: // Sound related
                        break;

                    case 4:
                        _shifter.SetShiftData(_cpu.Data);
                        break;

                    case 5: // Sound related
                        break;

                    case 6:
                        break;

                    default:
                        throw new InvalidOperationException();
                }
                break;
        }
    }

    private byte GetIOPort1Value()
    {
        // BIT 0   coin (0 when active)    
        //     1   P2 start button    
        //     2   P1 start button    
        //     3   ?    
        //     4   P1 shoot button    
        //     5   P1 joystick left    
        //     6   P1 joystick right    
        //     7   ?    

        var result = (byte)0;
        if (_keyCoin)
        {
            result |= 0x01;
        }
        if (_keyStart)
        {
            result |= 0x04;
        }
        if (_keyShoot)
        {
            result |= 0x10;
        }
        if (_keyLeft)
        {
            result |= 0x20;
        }
        if (_keyRight)
        {
            result |= 0x40;
        }
        return result;
    }

    private bool _keyCoin;
    private bool _keyStart;
    private bool _keyShoot;
    private bool _keyLeft;
    private bool _keyRight;

    private void UpdateDisplay()
    {
        for (var y = 32; y < 256; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                var videoRamValue = _ram[y * 32 + x];

                byte mask = 1;
                for (var b = 0; b < 8; b++)
                {
                    var outputValue = (videoRamValue & mask) != 0
                        ? (byte)0xFF
                        : (byte)0;

                    var outputAddress = y * 256 + x * 8 + b;

                    Display.Data[outputAddress] = new RgbaByte(
                        outputValue,
                        outputValue,
                        outputValue,
                        0xFF);

                    mask <<= 1;
                }
            }
        }
    }

    private byte ReadByteDebug(ushort address)
    {
        if (address > 0x3FFF)
        {
            // TODO: Actually this should be a mirror of RAM?
            throw new InvalidOperationException();
        }
        else if ((address & 0x2000) == 0x2000)
        {
            return _ram[address & 0x1FFF];
        }
        else
        {
            return _rom[address & 0x1FFF];
        }
    }

    private void WriteByteDebug(ushort address, byte value)
    {
        // TODO
    }

    public override void OnKeyEvent(SDLKeyboardEvent keyEvent)
    {
        var isKeyDown = keyEvent.Type == SDLEventType.KeyDown;

        if (keyEvent.Key == '0') // SDLK_0
        {
            _keyCoin = isKeyDown;
        }
        if (keyEvent.Key == '1') // SDLK_1
        {
            _keyStart = isKeyDown;
        }
        if (keyEvent.Key == 0x400000cdu) // SDLK_KP_SPACE
        {
            _keyShoot = isKeyDown;
        }
        if (keyEvent.Key == 0x40000050u) // SDLK_LEFT
        {
            _keyLeft = isKeyDown;
        }
        if (keyEvent.Key == 0x4000004fu) // SDLK_RIGHT
        {
            _keyRight = isKeyDown;
        }
    }

    public override Debugger CreateDebugger()
    {
        return new SpaceInvadersDebugger(
            this,
            new DebuggerMemoryCallbacks(ReadByteDebug, WriteByteDebug));
    }

    internal IReadOnlyList<ChannelNode> CreateChannelNodes()
    {
        return
        [
            _cpu.CreateChannelGroup(),
        ];
    }
}
