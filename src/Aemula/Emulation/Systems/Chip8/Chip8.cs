using System;
using System.IO;
using Aemula.Core;
using Aemula.Core.Debugging;
using Aemula.Systems.Chip8.Debugging;

namespace Aemula.Systems.Chip8;

public sealed partial class Chip8 : EmulatedSystem
{
    private readonly byte[] _memory; // 4KB
    private readonly byte[] _v; // V registers
    private readonly ushort[] _stack;
    private readonly byte[] _display;
    private readonly bool[] _keys;

    private readonly Random _random;
    private readonly byte[] _randomBytes;

    public ushort PC;

    private ushort _i;
    private byte _sp;

    private byte _dt; // Delay timer
    private byte _st; // Sound timer

    private bool _waitingForKeyPress;
    private byte _waitingForKeyPressRegister;

    private byte _cycle;

    public override ulong CyclesPerSecond => 600;

    public Chip8()
    {
        _memory = new byte[0x1000];
        _v = new byte[16];
        _stack = new ushort[16];
        _display = new byte[ScreenWidth * ScreenHeight];
        _keys = new bool[16];
        _random = new Random(42);
        _randomBytes = new byte[1];

        Reset();
    }

    public override void Reset()
    {
        Array.Clear(_memory, 0, _memory.Length);
        Array.Clear(_v, 0, _v.Length);
        Array.Clear(_stack, 0, _stack.Length);
        Array.Clear(_display, 0, _display.Length);
        Array.Clear(_keys, 0, _keys.Length);
        _i = 0;
        PC = ProgramStart;
        _sp = 0;
        _dt = 0;
        _st = 0;
        _waitingForKeyPress = false;
        _waitingForKeyPressRegister = 0;

        // Add sprite characters at start of memory.
        Array.Copy(SpriteCharacters, _memory, SpriteCharacters.Length);
    }

    public override void LoadProgram(string filePath)
    {
        var program = File.ReadAllBytes(filePath);
        Array.Copy(program, 0, _memory, ProgramStart, program.Length);
    }

    public void SetKeyDown(byte key)
    {
        _keys[key] = true;

        if (_waitingForKeyPress)
        {
            _v[_waitingForKeyPressRegister] = key;
            _waitingForKeyPress = false;
        }
    }

    public void SetKeyUp(byte key)
    {
        _keys[key] = false;
    }

    public override void Tick()
    {
        DoCpuCycle();

        _cycle++;

        // The system is updated at 600Hz, but timers need to be updated at 60Hz.
        if (_cycle == 10)
        {
            UpdateTimers();
            _cycle = 0;
        }
    }

    private void UpdateTimers()
    {
        if (_dt > 0)
        {
            _dt -= 1;
        }

        if (_st > 0)
        {
            // TODO: Play sound
            _st -= 1;
        }
    }

    private void DoCpuCycle()
    {
        if (_waitingForKeyPress)
        {
            return;
        }

        var kk = _memory[PC + 1];
        var opcode = (ushort)((_memory[PC] << 8) | kk);

        PC += 2;

        var x = (opcode & 0x0F00) >> 8;
        var y = (opcode & 0x00F0) >> 8;
        var nnn = (ushort)(opcode & 0xFFF);

        switch (opcode & 0xF000)
        {
            case 0x0000:
                switch (opcode)
                {
                    // CLS - Clear the display.
                    case 0x00E0:
                        Array.Clear(_display, 0, _display.Length);
                        break;

                    // RET - Return from a subroutine.
                    case 0x00EE:
                        _sp--;
                        PC = _stack[_sp];
                        break;
                }
                break;

            // 1nnn - JP addr - Jump to location nnn.
            case 0x1000:
                PC = nnn;
                break;

            // 2nnn - CALL addr - Call subroutine at nnn.
            case 0x2000:
                _stack[_sp] = PC;
                _sp++;
                PC = nnn;
                break;

            // 3xkk - SE Vx, byte - Skip next instruction if Vx = kk.
            case 0x3000:
                if (_v[x] == kk)
                {
                    PC += 2;
                }
                break;

            // 4xkk - SNE Vx, byte - Skip next instruction if Vx != kk.
            case 0x4000:
                if (_v[x] != kk)
                {
                    PC += 2;
                }
                break;

            // 5xy0 - SE Vx, Vy - Skip next instruction if Vx = Vy.
            case 0x5000:
                if (_v[x] == _v[y])
                {
                    PC += 2;
                }
                break;

            // 6xkk - LD Vx, byte - Set Vx = kk.
            case 0x6000:
                _v[x] = kk;
                break;

            // 7xkk - ADD Vx, byte - Set Vx = Vx + kk.
            case 0x7000:
                _v[x] += kk;
                break;

            case 0x8000:
                switch (opcode & 0x000F)
                {
                    // 8xy0 - LD Vx, Vy - Set Vx = Vy.
                    case 0x0:
                        _v[x] = _v[y];
                        break;

                    // 8xy1 - OR Vx, Vy - Set Vx = Vx OR Vy.
                    case 0x1:
                        _v[x] |= _v[y];
                        break;

                    // 8xy2 - AND Vx, Vy - Set Vx = Vx AND Vy.
                    case 0x2:
                        _v[x] &= _v[y];
                        break;

                    // 8xy3 - XOR Vx, Vy - Set Vx = Vx XOR Vy.
                    case 0x3:
                        _v[x] ^= _v[y];
                        break;

                    // 8xy4 - ADD Vx, Vy - Set Vx = Vx + Vy, set VF = carry.
                    case 0x4:
                        (_v[x], _v[0xF]) = OverflowingAdd(_v[x], _v[y]);
                        break;

                    // 8xy5 - SUB Vx, Vy - Set Vx = Vx - Vy, set VF = NOT borrow.
                    case 0x5:
                        (_v[x], _v[0xF]) = OverflowingSub(_v[x], _v[y]);
                        break;

                    // 8xy6 - SHR Vx {, Vy} - Set Vx = Vx SHR 1.
                    case 0x6:
                        (_v[x], _v[0xF]) = OverflowingShr(_v[x]);
                        break;

                    // 8xy6 - SUBN Vx, Vy - Set Vx = Vy - Vx, set VF = NOT borrow.
                    case 0x7:
                        (_v[x], _v[0xF]) = OverflowingSub(_v[y], _v[x]);
                        break;

                    // 8xyE - SHL Vx {, Vy} - Set Vx = Vx SHL 1.
                    case 0xE:
                        (_v[x], _v[0xF]) = OverflowingShl(_v[x]);
                        break;
                }
                break;

            // 9xy0 - SNE Vx, Vy - Skip next instruction if Vx != Vy.
            case 0x9000:
                if (_v[x] != _v[y])
                {
                    PC += 2;
                }
                break;

            // Annn - LD I, addr - Set I = nnn.
            case 0xA000:
                _i = nnn;
                break;

            // Bnnn - JP V0, addr - Jump to location nnn + V0.
            case 0xB000:
                PC = (ushort)(nnn + _v[0]);
                break;

            // Cxkk - RND Vx, byte - Set Vx = random byte AND kk.
            case 0xC000:
                _random.NextBytes(_randomBytes);
                _v[x] = (byte)(_randomBytes[0] & kk);
                break;

            // Dxyn - DRW Vx, Vy, nibble - Display n-byte sprite starting at memory location I
            // at (Vx, Vy), set Vf = collision.
            case 0xD000:
                var height = opcode & 0x000F;
                var vx = _v[x];
                _v[0xF] = 0;
                for (var pixelY = 0; pixelY < height; pixelY++)
                {
                    var sprite = _memory[_i + pixelY];
                    var actualY = (vx + pixelY) % ScreenHeight;
                    for (var pixelX = 0; pixelX < 8; pixelX++)
                    {
                        if ((sprite & 0x80) != 0)
                        {
                            var actualX = (vx + pixelX) % ScreenWidth;
                            var displayIndex = (actualY * ScreenWidth) + actualX;
                            _display[displayIndex] ^= 1;
                            if (_display[displayIndex] == 0)
                            {
                                _v[0xF] = 1;
                            }
                        }
                        sprite <<= 1;
                    }
                }
                break;

            case 0xE000:
                switch (opcode & 0x00FF)
                {
                    // Ex9E - SKP Vx - Skip next instruction if key with the value of Vx is pressed.
                    case 0x009E:
                        if (_keys[_v[x]])
                        {
                            PC += 2;
                        }
                        break;

                    // ExA1 - SKNP Vx - Skip next instruction if key with the value of Vx is not pressed.
                    case 0x00A1:
                        if (!_keys[_v[x]])
                        {
                            PC += 2;
                        }
                        break;
                }
                break;

            case 0xF000:
                switch (opcode & 0x00FF)
                {
                    // Fx07 - LD Vx, DT - Set Vx = delay timer value.
                    case 0x0007:
                        _v[x] = _dt;
                        break;

                    // Fx0A - LD Vx, K - Wait for a key press, store the value of the key in Vx.
                    case 0x000A:
                        _waitingForKeyPress = true;
                        _waitingForKeyPressRegister = (byte)x;
                        break;

                    // Fx15 - LD DT, Vx - Set delay timer = Vx.
                    case 0x0015:
                        _dt = _v[x];
                        break;

                    // Fx18 - LD ST, Vx - Set sound timer = Vx.
                    case 0x0018:
                        _st = _v[x];
                        break;

                    // Fx1E - ADD I, Vx - Set I = I + Vx.
                    case 0x001E:
                        _i += _v[x];
                        break;

                    // Fx29 - LD F, Vx - Set I = location of sprite for digit Vx.
                    case 0x0029:
                        _i = (ushort)(_v[x] * SpriteCharacterHeight);
                        break;

                    // Fx33 - LD B, Vx - Store BCD representation of Vx in memory locations I, I+1, and I+2
                    case 0x0033:
                        var value = _v[x];
                        for (var i = 2; i >= 0; i--)
                        {
                            _memory[_i + i] = (byte)(value % 10);
                            value /= 10;
                        }
                        break;

                    // Fx55 - LD [I], Vx - Stores registers V0 through Vx in memory starting at location I.
                    case 0x0055:
                        Array.Copy(_v, 0, _memory, _i, x + 1);
                        break;

                    // Fx65 - LD Vx, [I] - Read registers V0 through Vx from memory starting at location I.
                    case 0x0065:
                        Array.Copy(_memory, _i, _v, 0, x + 1);
                        break;
                }
                break;
        }
    }

    private const byte Zero = 0;
    private const byte One = 1;

    private static (byte result, byte vf) OverflowingAdd(byte x, byte y)
    {
        var temp = x + y;
        return ((byte)temp, (temp > byte.MaxValue) ? One : Zero);
    }

    private static (byte result, byte vf) OverflowingSub(byte x, byte y)
    {
        var temp = x - y;
        return ((byte)temp, (temp >= 0) ? One : Zero);
    }

    private static (byte result, byte vf) OverflowingShr(byte x)
    {
        return ((byte)(x >> 1), ((x & 1) != 0) ? One : Zero);
    }

    private static (byte result, byte vf) OverflowingShl(byte x)
    {
        return ((byte)(x << 1), ((x & 0x80) != 0) ? One : Zero);
    }

    private byte ReadByteDebug(ushort address) => _memory[address];

    private void WriteByteDebug(ushort address, byte value) => _memory[address] = value;

    public override Debugger CreateDebugger()
    {
        return new Chip8Debugger(this, new DebuggerMemoryCallbacks(ReadByteDebug, WriteByteDebug));
    }
}
