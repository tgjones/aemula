using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.SpaceInvaders;

// Phases 3 and 4 of docs/space-invaders-television-plan.md.
//
// Phase 3: CPU/video RAM bus arbitration. The video scanner and the CPU
// share one RAM chip; the schematic's "RAM A0"-"RAM A11"+ address mux row
// (74157 quad 2:1, see the plan's hardware reference table) selects, per
// address bit, between the CPU's own address bus and the scanner's own scan
// address, and a wait-state generator holds the CPU's READY pin low -
// inserting real Tw states via Intel8080Chip's Phi2-sampled Ready pin -
// whenever the CPU tries to touch RAM on a master tick the scanner has
// already claimed.
//
// Phase 4: the 74166 video shift register that actually consumes the
// scanner's fetched byte, serialized per pixel clock straight into
// Display - see TickVideoShiftRegister - replacing the old frame-end bulk
// blit.
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

    // The video shift register (`4F` on the schematic) - see the plan's
    // "Video shift register pin mapping" section. Ser is grounded and Clr
    // held high for the chip's whole lifetime, so neither is ever touched
    // again after construction.
    private readonly Ttl74166Chip _videoShiftRegister;

    // True for the one pixel-clock cell in each 8-pixel-clock byte-time,
    // during active video, where the scanner claims the bus to fetch the
    // next VRAM byte. The exact H[2:0] tap wasn't legible on this session's
    // schematic scan - same open risk the plan flags for Phase 4's SH/LD
    // tap on the 74166 - so H[2:0]==3 (MiSTer RTL's value) is used here as
    // a starting assumption.
    //
    // Correction found while implementing Phase 4: this signal turned out
    // not to be reusable, as originally planned, for the shift register's
    // own SH/LD timing below - getting Qh's output bit to land on the
    // exact display column its data byte belongs to requires the load to
    // happen exactly on the edge that starts each byte-time's column
    // window (post-edge H%8==0, see TickVideoShiftRegister), a different
    // phase than this claim window. The two stay independent signals; nothing
    // about this phase's own CPU-stall behavior (already covered by this
    // phase's own tests) changes.
    private bool _videoWantsRam;

    internal bool VideoWantsRamForTests => _videoWantsRam;
    internal ushort GetScanAddressForTests() => ComputeScanAddress();
    internal ushort GetRamAddressBusForTests() => ComputeMuxedRamAddress();
    internal bool GetShiftRegisterQhForTests() => _videoShiftRegister.Qh;

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

    /// <summary>
    /// Phase 4: drives the shift register (<c>4F</c>) and, from its Qh
    /// output, writes one pixel into <see cref="Display"/> per pixel clock.
    /// Runs after <c>TickVideoTiming</c> (see <c>Tick</c>) specifically so
    /// H/V/HBLANK/VBLANK have already been advanced to this tick's real,
    /// post-edge state before anything below reads them - the load
    /// condition below needs "the column we just entered", not "the column
    /// about to end", to land each byte's D0 on the exact pixel-clock tick
    /// its own column window starts (see the correction note on
    /// <see cref="_videoWantsRam"/> above for why this couldn't just reuse
    /// that signal's own, differently-phased, tap).
    /// </summary>
    private void TickVideoShiftRegister()
    {
        if (_masterClock % 4 != 0)
        {
            return;
        }

        var (h, v) = GetVideoScannerState();

        if (Hblank || Vblank || v < 0x20)
        {
            // Not real hardware behavior for the Hblank/Vblank part (the
            // 74166 has no enable pin and would keep clocking through
            // blanking on real silicon too) - but since blanking is never
            // read into Display either way, there's nothing to gain from
            // modelling it. v < 0x20 only ever happens during this system's
            // own cold-start settling (see SpaceInvadersSystemVideoTimingTests'
            // own note on the same quirk) - real V never revisits below
            // 0x20 once steady state is reached, and $2000-$23FF is free
            // work RAM, not VRAM (see the plan's "Correcting the existing
            // code" section) - so this guards against a one-time,
            // non-representative pass permanently leaking scan-address
            // garbage into Display rows that should stay untouched.
            return;
        }

        // Every 8th pixel clock - the first column of a fresh byte-time -
        // loads the next VRAM byte instead of shifting. See the plan's
        // "Video shift register pin mapping" section for D0..D7 -> H..A.
        if ((h & 0b111) == 0)
        {
            var videoByte = _ram[ComputeScanAddress() & 0x1FFF];

            _videoShiftRegister.H = (videoByte & 0x01) != 0;
            _videoShiftRegister.G = (videoByte & 0x02) != 0;
            _videoShiftRegister.F = (videoByte & 0x04) != 0;
            _videoShiftRegister.E = (videoByte & 0x08) != 0;
            _videoShiftRegister.D = (videoByte & 0x10) != 0;
            _videoShiftRegister.C = (videoByte & 0x20) != 0;
            _videoShiftRegister.B = (videoByte & 0x40) != 0;
            _videoShiftRegister.A = (videoByte & 0x80) != 0;

            _videoShiftRegister.ShLd = false; // Parallel load.
        }
        else
        {
            _videoShiftRegister.ShLd = true; // Shift.
        }

        _videoShiftRegister.Clk = false;
        _videoShiftRegister.Clk = true;

        var outputValue = _videoShiftRegister.Qh ? (byte)0xFF : (byte)0;

        Display.Data[v * 256 + h] = new RgbaByte(outputValue, outputValue, outputValue, 0xFF);
    }
}
