using System;

namespace Aemula.Emulation.Chips.Mos6502.Debugging;

/// <summary>
/// Live X/Y register access for <see cref="Mos6502Disassembler.TryGetEffectiveAddress"/> -
/// a small callback bundle rather than a direct <see cref="Mos6502Chip"/>
/// reference, so <c>Disassembler</c> stays decoupled from concrete chip
/// types, matching how <c>DebuggerMemoryCallbacks</c> is threaded in.
/// Defaults to no callbacks (both null), which TryGetEffectiveAddress treats
/// as "not wired up" rather than throwing.
/// </summary>
public readonly struct Mos6502RegisterCallbacks(
    Func<byte> readX,
    Func<byte> readY)
{
    public readonly Func<byte> ReadX = readX;
    public readonly Func<byte> ReadY = readY;
}
