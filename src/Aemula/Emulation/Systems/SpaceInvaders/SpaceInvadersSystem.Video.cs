using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.SpaceInvaders;

// Phase 3 of docs/space-invaders-television-plan.md: CPU/video RAM bus
// arbitration. The video scanner and the CPU share one RAM chip; the
// schematic's "RAM A0"-"RAM A11"+ address mux row (74157 quad 2:1, see the
// plan's hardware reference table) selects, per address bit, between the
// CPU's own address bus and the scanner's own scan address, and a
// wait-state generator holds the CPU's READY pin low - inserting real Tw
// states via Intel8080Chip's Phi2-sampled Ready pin - whenever the CPU
// tries to touch RAM on a master tick the scanner has already claimed.
//
// Phase 4 will extend this file with the 74166 shift register that actually
// consumes the scanner's fetched byte for per-pixel display; this phase
// only needs the scan address and the arbitration signal it gates.
public sealed partial class SpaceInvadersSystem
{
    // Four 74157s selecting each RAM address bit between the CPU's address
    // (S low) and the scanner's own scan address "RAB" (S high). The
    // schematic traced this row only as far as legible ("RAM A0"-"RAM A11"),
    // but RAB's own range (V*32 + H[7:3], up to 0x1FFF - see
    // ComputeScanAddress) needs all 13 bits the CPU's address does too, so a
    // fourth chip (using only its first channel) covers A12.
    private readonly Ttl74157Chip _ramAddressMuxBits0To3;
    private readonly Ttl74157Chip _ramAddressMuxBits4To7;
    private readonly Ttl74157Chip _ramAddressMuxBits8To11;
    private readonly Ttl74157Chip _ramAddressMuxBit12;

    // True for the one pixel-clock cell in each 8-pixel-clock byte-time,
    // during active video, where the scanner claims the bus to fetch the
    // next VRAM byte. The exact H[2:0] tap wasn't legible on this session's
    // schematic scan - same open risk the plan flags for Phase 4's SH/LD
    // tap on the 74166 - so H[2:0]==3 (MiSTer RTL's value) is reused here
    // too, letting Phase 4 share this exact signal for the shift register's
    // own load timing rather than re-deriving it.
    private bool _videoWantsRam;

    internal bool VideoWantsRamForTests => _videoWantsRam;
    internal ushort GetScanAddressForTests() => ComputeScanAddress();
    internal ushort GetRamAddressBusForTests() => ComputeMuxedRamAddress();

    /// <summary>
    /// Recomputes <see cref="_videoWantsRam"/> and drives the CPU's READY
    /// pin from it. Called once per master tick, before <c>TickCpuClock</c>,
    /// so READY is settled before Intel8080Chip's Phi2 (which samples it)
    /// can fall on this same tick.
    /// </summary>
    private void TickRamArbitration()
    {
        if (_masterClock % 4 == 0)
        {
            var (h, _) = GetVideoScannerState();

            _videoWantsRam = !Hblank && !Vblank && (h & 0b111) == 3;
        }

        // The CPU only contends for the bus when it's actually addressing
        // RAM (0x2000-0x3FFF) - ROM and I/O accesses never reach this mux at
        // all, so the scanner can't stall them (the same 0x2000 decode
        // ReadCpuBus/WriteCpuBus already use).
        var cpuWantsRam = (_cpu.Address & 0x2000) != 0;

        _cpu.Ready = !(_videoWantsRam && cpuWantsRam);
    }

    /// <summary>
    /// The video scanner's own RAM address: RAB = V*32 + H[7:3], landing
    /// the first scanned byte at $2400 - MiSTer rtl/mw8080.vhd's video-RAM
    /// scan address formula (see the plan's Hardware reference section).
    /// </summary>
    private ushort ComputeScanAddress()
    {
        var (h, v) = GetVideoScannerState();

        var rab = (v << 5) | (h >> 3);

        return (ushort)(0x2000 | rab);
    }

    /// <summary>
    /// The RAM address bus as the four 74157s actually drive it this
    /// instant - the CPU's own address when the scanner doesn't hold the
    /// bus, the scan address when it does.
    /// </summary>
    private ushort ComputeMuxedRamAddress()
    {
        var cpuAddress = (ushort)(_cpu.Address & 0x1FFF);
        var scanAddress = (ushort)(ComputeScanAddress() & 0x1FFF);

        _ramAddressMuxBits0To3.S = _videoWantsRam;
        _ramAddressMuxBits0To3.G = false;
        _ramAddressMuxBits0To3.A1 = (cpuAddress & (1 << 0)) != 0;
        _ramAddressMuxBits0To3.B1 = (scanAddress & (1 << 0)) != 0;
        _ramAddressMuxBits0To3.A2 = (cpuAddress & (1 << 1)) != 0;
        _ramAddressMuxBits0To3.B2 = (scanAddress & (1 << 1)) != 0;
        _ramAddressMuxBits0To3.A3 = (cpuAddress & (1 << 2)) != 0;
        _ramAddressMuxBits0To3.B3 = (scanAddress & (1 << 2)) != 0;
        _ramAddressMuxBits0To3.A4 = (cpuAddress & (1 << 3)) != 0;
        _ramAddressMuxBits0To3.B4 = (scanAddress & (1 << 3)) != 0;

        _ramAddressMuxBits4To7.S = _videoWantsRam;
        _ramAddressMuxBits4To7.G = false;
        _ramAddressMuxBits4To7.A1 = (cpuAddress & (1 << 4)) != 0;
        _ramAddressMuxBits4To7.B1 = (scanAddress & (1 << 4)) != 0;
        _ramAddressMuxBits4To7.A2 = (cpuAddress & (1 << 5)) != 0;
        _ramAddressMuxBits4To7.B2 = (scanAddress & (1 << 5)) != 0;
        _ramAddressMuxBits4To7.A3 = (cpuAddress & (1 << 6)) != 0;
        _ramAddressMuxBits4To7.B3 = (scanAddress & (1 << 6)) != 0;
        _ramAddressMuxBits4To7.A4 = (cpuAddress & (1 << 7)) != 0;
        _ramAddressMuxBits4To7.B4 = (scanAddress & (1 << 7)) != 0;

        _ramAddressMuxBits8To11.S = _videoWantsRam;
        _ramAddressMuxBits8To11.G = false;
        _ramAddressMuxBits8To11.A1 = (cpuAddress & (1 << 8)) != 0;
        _ramAddressMuxBits8To11.B1 = (scanAddress & (1 << 8)) != 0;
        _ramAddressMuxBits8To11.A2 = (cpuAddress & (1 << 9)) != 0;
        _ramAddressMuxBits8To11.B2 = (scanAddress & (1 << 9)) != 0;
        _ramAddressMuxBits8To11.A3 = (cpuAddress & (1 << 10)) != 0;
        _ramAddressMuxBits8To11.B3 = (scanAddress & (1 << 10)) != 0;
        _ramAddressMuxBits8To11.A4 = (cpuAddress & (1 << 11)) != 0;
        _ramAddressMuxBits8To11.B4 = (scanAddress & (1 << 11)) != 0;

        _ramAddressMuxBit12.S = _videoWantsRam;
        _ramAddressMuxBit12.G = false;
        _ramAddressMuxBit12.A1 = (cpuAddress & (1 << 12)) != 0;
        _ramAddressMuxBit12.B1 = (scanAddress & (1 << 12)) != 0;

        return (ushort)(
            (_ramAddressMuxBits0To3.Y1 ? 1 << 0 : 0) |
            (_ramAddressMuxBits0To3.Y2 ? 1 << 1 : 0) |
            (_ramAddressMuxBits0To3.Y3 ? 1 << 2 : 0) |
            (_ramAddressMuxBits0To3.Y4 ? 1 << 3 : 0) |
            (_ramAddressMuxBits4To7.Y1 ? 1 << 4 : 0) |
            (_ramAddressMuxBits4To7.Y2 ? 1 << 5 : 0) |
            (_ramAddressMuxBits4To7.Y3 ? 1 << 6 : 0) |
            (_ramAddressMuxBits4To7.Y4 ? 1 << 7 : 0) |
            (_ramAddressMuxBits8To11.Y1 ? 1 << 8 : 0) |
            (_ramAddressMuxBits8To11.Y2 ? 1 << 9 : 0) |
            (_ramAddressMuxBits8To11.Y3 ? 1 << 10 : 0) |
            (_ramAddressMuxBits8To11.Y4 ? 1 << 11 : 0) |
            (_ramAddressMuxBit12.Y1 ? 1 << 12 : 0));
    }
}
