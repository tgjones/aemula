using System;
using System.IO;
using Aemula.Emulation.Chips.Intel8080;
using Aemula.Debugging;
using Aemula.Emulation.Chips.MB14241;
using Aemula.Emulation.Systems.SpaceInvaders.Debugging;
using Hexa.NET.SDL3;

namespace Aemula.Emulation.Systems.SpaceInvaders;

public sealed class SpaceInvadersSystem : EmulatedSystem
{
    private readonly Intel8080Chip _cpu;

    private readonly byte[] _rom;
    private readonly byte[] _ram;

    private readonly MB14241Chip _shifter;

    private byte _lastStatusWord;
    private ulong _masterClock;
    private uint _pixelClock;
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

        Display = new DisplayBuffer(256, 256);
    }

    public override void LoadProgram(string filePath)
    {
        void LoadRom(string fileName, ushort startAddress)
        {
            using var fileStream = File.OpenRead($"Roms/{fileName}");
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

        if (_masterClock % 4 == 0)
        {
            _pixelClock++;
        }

        if (_masterClock % 10 == 0)
        {
            TickCpu();
        }

        if (_pixelClock == 30432 + 10161) // Based on EDL :)
        {
            _nextInterrupt = 0xCF;
            _cpu.Int = true;
        }

        if (_pixelClock == 71008 + 10161) // Based on EDL :)
        {
            _nextInterrupt = 0xD7;
            _cpu.Int = true;
        }

        if (_pixelClock > 83200)
        {
            _pixelClock = 0;

            UpdateDisplay();
        }
    }

    private void TickCpu()
    {
        _cpu.Cycle();

        if (_cpu.Sync)
        {
            _lastStatusWord = _cpu.Data;
        }

        if (_cpu.DBIn)
        {
            // Read data.
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

        if (!_cpu.Wr)
        {
            // Write data.
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
}
