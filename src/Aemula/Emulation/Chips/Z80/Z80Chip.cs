namespace Aemula.Emulation.Chips.Z80;

/// <summary>
/// A pin-level, half-cycle-accurate NMOS Zilog Z80 CPU.
///
/// The Z80 has a single clock input, CLK; one T-state is one full CLK period.
/// "Half-cycle accurate" means the core does work on <em>both</em> CLK edges -
/// rising and falling - so every bus-signal transition lands on the same
/// half-T-state edge it does on real silicon. A consumer clocks one T-state
/// with <c>chip.Clk = true; chip.Clk = false;</c>.
///
/// Edge map, from the Zilog Z80 CPU User Manual (UM0080) timing diagrams
/// ("T2 down" = the falling edge of T2):
///
///   M1 - opcode fetch
///     T1 up    /M1 low; address bus = PC; PC incremented
///     T1 down  /MREQ low; /RD low
///     T2 down  /WAIT sampled (and on every following Tw down)
///     T3 up    opcode latched off the data bus; /MREQ, /RD, /M1 released;
///              /RFSH low with address bus = I:R; R does its 7-bit increment
///     T3 down  /MREQ pulses low for the refresh cycle
///     T4 up    refresh /MREQ released
///   Memory read (M2 onwards)
///     T1 up    address bus driven
///     T1 down  /MREQ low; /RD low
///     T2 down  /WAIT sampled (+ Tw down)
///     T3 up    data latched
///   Memory write
///     T1 up    address bus driven
///     T1 down  /MREQ low; data bus driven
///     T2 down  /WR low; /WAIT sampled (+ Tw down)
///     T3 down  /WR and /MREQ released
///   I/O read / write
///     T2 up    /IORQ (with /RD or /WR) low
///     -        one Tw is always auto-inserted between T2 and T3
///     Tw down  /WAIT sampled
///     T3 down  /IORQ, /RD, /WR released
///   Interrupt acknowledge - a special M1
///     /M1 low with /IORQ (not /MREQ) low; two Tw are auto-inserted
///
/// Internal (no-bus) machine cycles - the extra clocks the Z80 spends on
/// 16-bit arithmetic, (IX+d) address calculation, block-instruction repeats,
/// PUSH predecrement and so on - are their own machine-cycle type and do their
/// register work with no external pin activity.
/// </summary>
public sealed partial class Z80Chip
{
    // --- Internal state --------------------------------------------------------

    private bool _clk;

    private MachineCycleType _machineCycleType;
    private TState _state;

    // 1-based index of the current machine cycle within the instruction.
    private byte _machineCycle;

    // Instruction register - the opcode latched off the data bus on M1's T3.
    private byte _ir;

    // Scratch byte for microcode that has to carry a value from one machine
    // cycle to a later one - LD (HL),n stashing the immediate here between its
    // read cycle and its write cycle, for instance.
    private byte _tmp;

    // Set by HALT (0x76): the CPU keeps running 4-T opcode-fetch M1s with the
    // program counter frozen and /HALT asserted. Clearing it (on an interrupt)
    // is handled with the rest of the interrupt logic.
    private bool _halted;

    // Which decode table the current opcode belongs to. Unprefixed opcodes
    // decode straight from _ir; a 0xCB / 0xED / 0xDD / 0xFD escape byte latches
    // one of these and runs another M1 before the real opcode is decoded. Only
    // None is exercised so far; the rest are wired ahead of the prefixed tables.
    private Z80Prefix _prefix;

    // The signed displacement byte d captured for a (IX+d) / (IY+d) operand and
    // for the DD CB d / FD CB d double-prefix form: fetched from PC after the
    // opcode (after the CB byte, for the double prefix), then added to IX / IY
    // to form the effective address during the internal address-calculation
    // machine cycle.
    private sbyte _displacement;

    // Wait states hard-wired into the current machine cycle, independent of the
    // /WAIT pin: 1 for an I/O cycle, 2 for interrupt acknowledge, 0 otherwise.
    // A real Z80 I/O cycle always gives a peripheral a full extra clock to
    // decode the port address; interrupt acknowledge gives the interrupting
    // device two, to place its vector on the bus.
    private int _builtInWaitStatesRemaining;

    // Latched on every CLK falling edge; consulted on the next CLK rising edge to
    // decide whether to advance the T-state or park in Tw. Mirrors how real
    // silicon samples /WAIT on the falling edge of T2 and of each Tw already
    // inserted.
    private bool _waitSampledLow;

    // Staged by SetNextCycle, applied on the next CLK rising edge (the real
    // T-state boundary) by ApplyPendingCycleTransition. Deferred rather than
    // applied immediately because microcode may stage it from work running on
    // either edge of the current T-state.
    private MachineCycleType? _pendingMachineCycleType;

    // /RESET, active low, deasserted at power-up.
    private bool _resetPin = true;

    // CLK rising edges counted while /RESET is held low - the Z80 acts on /RESET
    // only once it has been low for at least 3 T-states.
    private int _resetLowClocks;

    // --- Pins ---------------------------------------------------------------

    /// <summary>16-bit address bus (output).</summary>
    public ushort Address { get; private set; }

    /// <summary>8-bit data bus (bidirectional).</summary>
    public byte Data { get; set; }

    /// <summary>
    /// /M1 - machine cycle one. Low through an opcode fetch (and, with /IORQ, an
    /// interrupt acknowledge). Active low.
    /// </summary>
    public bool M1 { get; private set; } = true;

    /// <summary>
    /// /MREQ - memory request. Low while the address bus holds a valid memory
    /// address for a read or write (and pulses low for the M1 refresh). Active low.
    /// </summary>
    public bool MReq { get; private set; } = true;

    /// <summary>
    /// /IORQ - I/O request. Low while the address bus holds a valid port address,
    /// and during interrupt acknowledge. Active low.
    /// </summary>
    public bool IoRq { get; private set; } = true;

    /// <summary>/RD - read. Low when the CPU is reading memory or a port. Active low.</summary>
    public bool Rd { get; private set; } = true;

    /// <summary>/WR - write. Low when the data bus holds valid data to store. Active low.</summary>
    public bool Wr { get; private set; } = true;

    /// <summary>
    /// /RFSH - refresh. Low while the address bus holds the I:R dynamic-RAM
    /// refresh address during the second half of M1. Active low.
    /// </summary>
    public bool Rfsh { get; private set; } = true;

    /// <summary>
    /// /HALT - low after a HALT instruction (while the CPU executes NOPs) until an
    /// interrupt is taken. Active low.
    /// </summary>
    public bool Halt { get; private set; } = true;

    /// <summary>
    /// /BUSAK - bus acknowledge. Low once the CPU has tri-stated its address, data
    /// and control buses in response to /BUSRQ. Active low.
    /// </summary>
    public bool BusAk { get; private set; } = true;

    /// <summary>
    /// /WAIT (input). While held low it stalls the CPU in Tw - it is sampled on
    /// the CLK falling edge of T2 and of every Tw already inserted, and the next
    /// CLK rising edge parks in (or repeats) Tw instead of advancing to T3.
    /// Active low; defaults true (deasserted) so a system that never drives this
    /// pin - and the hand-written wait-state tests - see no stalls.
    /// </summary>
    public bool Wait { internal get; set; } = true;

    /// <summary>/INT - maskable interrupt request (input). Active low, level-sensitive.</summary>
    public bool Int { internal get; set; } = true;

    /// <summary>/NMI - non-maskable interrupt request (input). Active low, edge-triggered.</summary>
    public bool Nmi { internal get; set; } = true;

    /// <summary>/BUSRQ - bus request (input). Active low.</summary>
    public bool BusRq { internal get; set; } = true;

    /// <summary>
    /// /RESET (input). Held low for at least 3 T-states to reset the CPU: PC, I
    /// and R are cleared, both interrupt flip-flops are cleared, interrupt mode 0
    /// is selected, and - as an NMOS part settles them in practice - AF and SP
    /// become 0xFFFF. The CPU parks with no bus activity for as long as the pin is
    /// held low. Active low; defaults true.
    /// </summary>
    public bool Reset
    {
        internal get => _resetPin;
        set
        {
            if (_resetPin == value)
            {
                return;
            }

            _resetPin = value;

            if (!value)
            {
                // /RESET just went low - start counting how long it is held.
                _resetLowClocks = 0;
            }
            else if (_resetLowClocks >= 3)
            {
                // /RESET released after a genuine reset - restart execution with a
                // fresh opcode fetch from 0x0000.
                SetNextCycle(MachineCycleType.OpcodeFetch);
            }
        }
    }

    /// <summary>
    /// CLK (input). The half-cycle engine: a rising edge starts a T-state (apply a
    /// staged machine-cycle transition or advance the T-state, then drive the pins
    /// whose UM0080 transition is on a rising edge); a falling edge samples /WAIT
    /// and drives the pins whose transition is on a falling edge. Driven exactly
    /// like <c>Mos6502Chip.Phi0</c> / <c>Ricoh2C02Chip.Clk</c> - no-change writes
    /// are ignored.
    /// </summary>
    public bool Clk
    {
        set
        {
            if (_clk == value)
            {
                return;
            }

            _clk = value;

            if (value)
            {
                OnClkRising();
            }
            else
            {
                OnClkFalling();
            }
        }
    }

    // --- Public state inspection --------------------------------------------

    public MachineCycleType CurrentMachineCycle => _machineCycleType;

    public TState CurrentState => _state;

    internal byte InstructionRegister => _ir;

    internal int CombinedCycleKey => CombineCycleKey(_machineCycleType, _state);

    /// <summary>
    /// True only in the gap between one instruction's final CLK falling edge and
    /// the next opcode fetch's first rising edge: microcode has staged the next
    /// M1 but it has not started, so PC still points at the next opcode and no
    /// register has been touched for it. A clock-stepping test harness uses this
    /// to halt exactly on an instruction boundary.
    /// </summary>
    internal bool AtInstructionBoundary =>
        _pendingMachineCycleType == MachineCycleType.OpcodeFetch && _prefix == Z80Prefix.None;

    /// <summary>
    /// Whether a HALT (0x76) has parked the CPU. It keeps fetching M1s with PC
    /// frozen and /HALT low until this is cleared (by the interrupt logic).
    /// </summary>
    internal bool Halted
    {
        get => _halted;
        set => _halted = value;
    }

    // --- Construction ------------------------------------------------------

    public Z80Chip()
    {
        ResetState();
        SetNextCycle(MachineCycleType.OpcodeFetch);
    }

    // --- Half-cycle engine ------------------------------------------------

    private void OnClkRising()
    {
        if (!_resetPin)
        {
            // Parked in reset: no bus activity, every active-low control output
            // deasserted. Once /RESET has been low for 3 T-states the internal
            // state is forced to its reset values, and stays there until release.
            if (++_resetLowClocks == 3)
            {
                ResetState();
            }

            M1 = MReq = IoRq = Rd = Wr = Rfsh = BusAk = Halt = true;
            return;
        }

        // A CLK rising edge is the T-state boundary. Apply whatever machine-cycle
        // transition was staged during the previous T-state, or advance to the
        // next T-state of the current machine cycle if nothing was staged.
        if (_pendingMachineCycleType.HasValue)
        {
            ApplyPendingCycleTransition();
        }
        else
        {
            _state = AdvanceTState();
        }

        switch (CombineCycleKey(_machineCycleType, _state))
        {
            case OpcodeFetchT1:
                // /M1 low and the address bus = PC on T1 rising; PC increments now
                // so the I:R refresh address can take the bus at T3. While halted
                // the same 4-T M1 repeats forever with PC held still - the fetched
                // byte is discarded and the CPU behaves as if executing NOPs.
                M1 = false;
                Rfsh = true;
                Address = PC.Value;
                if (!_halted)
                {
                    PC.Value++;
                }
                break;

            case OpcodeFetchT3:
                // The opcode is latched off the data bus, then /MREQ, /RD and /M1
                // are released and the refresh half of M1 begins: /RFSH low with
                // I:R on the address bus, and R's 7-bit auto-increment (bit 7 is
                // software-owned and left untouched).
                _ir = Data;
                MReq = true;
                Rd = true;
                M1 = true;
                Rfsh = false;
                Address = (ushort)((I << 8) | R);
                R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
                break;

            case OpcodeFetchT4:
                // The refresh /MREQ pulse ends.
                MReq = true;
                break;

            case MemoryReadT3:
                // The consumer latches the byte off the bus on this edge; the CPU
                // releases /MREQ and /RD.
                MReq = true;
                Rd = true;
                break;

            case IoReadT2:
            case IoWriteT2:
                // For an I/O cycle /IORQ (with /RD or /WR) goes low on T2 rising -
                // half a T-state later than /MREQ would on a memory cycle, which is
                // what makes room for the built-in Tw that follows.
                IoRq = false;
                if (_machineCycleType == MachineCycleType.IoRead)
                {
                    Rd = false;
                }
                else
                {
                    Wr = false;
                }
                break;
        }

        // Opcode-specific microcode. Runs after the generic pin sequencing so it
        // sees the freshly latched _ir (and, on a machine-cycle boundary, the
        // just-applied cycle transition).
        HandleInstruction(CombineCycleKey(_machineCycleType, _state));
    }

    private void OnClkFalling()
    {
        if (!_resetPin)
        {
            return;
        }

        // /WAIT is sampled on every CLK falling edge. Only the samples taken on T2
        // and on an already-inserted Tw actually gate T-state advance (see
        // AdvanceTState); sampling unconditionally is harmless because the value
        // is consulted only when it is meaningful.
        _waitSampledLow = !Wait;

        switch (CombineCycleKey(_machineCycleType, _state))
        {
            case OpcodeFetchT1:
            case MemoryReadT1:
                // /MREQ and /RD fall half a T-state after the address is valid.
                MReq = false;
                Rd = false;
                break;

            case MemoryWriteT1:
                // A write drives /MREQ low (and, once microcode exists, the data
                // bus) on T1 falling; /WR is deliberately held off until T2 falling
                // so the data has settled first.
                MReq = false;
                break;

            case MemoryWriteT2:
                Wr = false;
                break;

            case MemoryWriteT3:
                Wr = true;
                MReq = true;
                break;

            case OpcodeFetchT3:
                // The refresh /MREQ pulse: low from T3 falling until T4 rising.
                MReq = false;
                break;

            case IoReadT3:
            case IoWriteT3:
                IoRq = true;
                Rd = true;
                Wr = true;
                break;
        }

        // The falling-edge microcode dispatch. The cycle key carries ClkFallingFlag
        // so microcode can tell the two half-cycles of a T-state apart; the
        // unprefixed loads and stack ops do all their register work on the rising
        // edge, so nothing consumes it yet.
        HandleInstruction(CombineCycleKey(_machineCycleType, _state) | ClkFallingFlag);
    }

    /// <summary>
    /// The next T-state for the current machine cycle when nothing has been
    /// staged. T2 (and Tw) branch to Tw whenever a wait is owed - either one wired
    /// into the machine cycle or /WAIT sampled low on the falling edge just past.
    /// With no microcode staged yet, a machine cycle simply holds at T4.
    /// </summary>
    private TState AdvanceTState()
    {
        return _state switch
        {
            TState.T1 => TState.T2,
            TState.T2 => WaitOwed() ? TState.Tw : TState.T3,
            TState.Tw => WaitOwed() ? TState.Tw : TState.T3,
            TState.T3 => TState.T4,
            _ => _state,
        };
    }

    private bool WaitOwed()
    {
        if (_builtInWaitStatesRemaining > 0)
        {
            _builtInWaitStatesRemaining--;
            return true;
        }

        return _waitSampledLow;
    }

    private void SetNextCycle(MachineCycleType machineCycleType)
    {
        _pendingMachineCycleType = machineCycleType;
    }

    private void ApplyPendingCycleTransition()
    {
        _machineCycleType = _pendingMachineCycleType!.Value;
        _pendingMachineCycleType = null;

        _builtInWaitStatesRemaining = _machineCycleType switch
        {
            MachineCycleType.IoRead or MachineCycleType.IoWrite => 1,
            MachineCycleType.InterruptAck => 2,
            _ => 0,
        };

        if (_machineCycleType == MachineCycleType.OpcodeFetch)
        {
            _machineCycle = 1;

            // A genuine instruction boundary (not a 0xCB / 0xED / 0xDD / 0xFD
            // escape's second M1): latch Q - the F byte just produced, or 0 if
            // the finished instruction left F alone - for the next SCF / CCF to
            // read, then clear the tracker for the instruction now starting.
            if (_prefix == Z80Prefix.None)
            {
                _q = _flagsModified ? Flags.AsByte() : (byte)0;
                _flagsModified = false;
            }

            // The end-of-instruction /INT sample belongs here - real silicon
            // latches /INT on the last T-state of an instruction - and is wired in
            // once instruction decode exists.
        }
        else
        {
            _machineCycle++;
        }

        _state = TState.T1;
    }

    private void ResetState()
    {
        // UM0080 reset: PC and the interrupt vector / refresh registers are
        // cleared, both interrupt flip-flops are cleared and interrupt mode 0 is
        // selected. The documented Zilog reset leaves AF and SP alone, but an NMOS
        // part settles them to 0xFFFF, which is the post-cold-start value software
        // (and the conformance suites) rely on.
        PC.Value = 0;
        I = 0;
        R = 0;
        IFF1 = false;
        IFF2 = false;
        IM = 0;
        AF.Value = 0xFFFF;
        SP.Value = 0xFFFF;

        _pendingMachineCycleType = null;
        _machineCycleType = MachineCycleType.OpcodeFetch;
        _state = TState.T1;
        _machineCycle = 1;
        _builtInWaitStatesRemaining = 0;
        _waitSampledLow = false;
        _ir = 0;
        _tmp = 0;
        _halted = false;
        _prefix = Z80Prefix.None;
        _displacement = 0;
        _q = 0;
        _flagsModified = false;
    }

    // --- Test hooks ------------------------------------------------------

    /// <summary>
    /// Stages an arbitrary machine cycle as the next one, the way microcode will
    /// once it exists. Lets the hand-written tests reach machine cycles - an I/O
    /// cycle, to observe its built-in Tw, for instance - that otherwise need a
    /// decoded opcode to trigger.
    /// </summary>
    internal void StageNextMachineCycleForTest(MachineCycleType machineCycleType)
    {
        SetNextCycle(machineCycleType);
    }

    // --- State-machine types -------------------------------------------

    public enum MachineCycleType : byte
    {
        OpcodeFetch,
        MemoryRead,
        MemoryWrite,
        IoRead,
        IoWrite,
        Internal,
        InterruptAck,
    }

    /// <summary>
    /// Which decode table an opcode belongs to. A leading 0xCB / 0xED / 0xDD /
    /// 0xFD byte is a prefix that selects one of these; 0xDD/0xFD followed by
    /// 0xCB gives the DDCB / FDCB double prefix. Only <see cref="None"/> is
    /// decoded so far.
    /// </summary>
    public enum Z80Prefix : byte
    {
        None,
        CB,
        ED,
        DD,
        FD,
        DDCB,
        FDCB,
    }

    public enum TState : byte
    {
        T1,
        T2,

        /// <summary>
        /// Wait state. Inserted (repeatedly, while needed) between T2 and T3
        /// whenever /WAIT sampled low on the preceding CLK falling edge, or a
        /// machine cycle with a hard-wired wait (I/O, interrupt acknowledge) still
        /// owes one.
        /// </summary>
        Tw,

        T3,
        T4,
        T5,
        T6,
    }

    private static int CombineCycleKey(MachineCycleType machineCycleType, TState tState)
    {
        return ((byte)machineCycleType << 8) | (byte)tState;
    }

    // OR'd into the cycle key passed to HandleInstruction from the CLK falling
    // edge. Set above the byte the machine-cycle type and T-state occupy, so a
    // plain "case OpcodeFetchT4:" label matches the rising-edge dispatch only.
    private const int ClkFallingFlag = 1 << 16;

    private const int OpcodeFetchT1 = ((byte)MachineCycleType.OpcodeFetch << 8) | (byte)TState.T1;
    private const int OpcodeFetchT2 = ((byte)MachineCycleType.OpcodeFetch << 8) | (byte)TState.T2;
    private const int OpcodeFetchTw = ((byte)MachineCycleType.OpcodeFetch << 8) | (byte)TState.Tw;
    private const int OpcodeFetchT3 = ((byte)MachineCycleType.OpcodeFetch << 8) | (byte)TState.T3;
    private const int OpcodeFetchT4 = ((byte)MachineCycleType.OpcodeFetch << 8) | (byte)TState.T4;

    private const int MemoryReadT1 = ((byte)MachineCycleType.MemoryRead << 8) | (byte)TState.T1;
    private const int MemoryReadT2 = ((byte)MachineCycleType.MemoryRead << 8) | (byte)TState.T2;
    private const int MemoryReadTw = ((byte)MachineCycleType.MemoryRead << 8) | (byte)TState.Tw;
    private const int MemoryReadT3 = ((byte)MachineCycleType.MemoryRead << 8) | (byte)TState.T3;

    private const int MemoryWriteT1 = ((byte)MachineCycleType.MemoryWrite << 8) | (byte)TState.T1;
    private const int MemoryWriteT2 = ((byte)MachineCycleType.MemoryWrite << 8) | (byte)TState.T2;
    private const int MemoryWriteTw = ((byte)MachineCycleType.MemoryWrite << 8) | (byte)TState.Tw;
    private const int MemoryWriteT3 = ((byte)MachineCycleType.MemoryWrite << 8) | (byte)TState.T3;

    private const int IoReadT1 = ((byte)MachineCycleType.IoRead << 8) | (byte)TState.T1;
    private const int IoReadT2 = ((byte)MachineCycleType.IoRead << 8) | (byte)TState.T2;
    private const int IoReadTw = ((byte)MachineCycleType.IoRead << 8) | (byte)TState.Tw;
    private const int IoReadT3 = ((byte)MachineCycleType.IoRead << 8) | (byte)TState.T3;

    private const int IoWriteT1 = ((byte)MachineCycleType.IoWrite << 8) | (byte)TState.T1;
    private const int IoWriteT2 = ((byte)MachineCycleType.IoWrite << 8) | (byte)TState.T2;
    private const int IoWriteTw = ((byte)MachineCycleType.IoWrite << 8) | (byte)TState.Tw;
    private const int IoWriteT3 = ((byte)MachineCycleType.IoWrite << 8) | (byte)TState.T3;

    private const int InternalT1 = ((byte)MachineCycleType.Internal << 8) | (byte)TState.T1;
    private const int InternalT2 = ((byte)MachineCycleType.Internal << 8) | (byte)TState.T2;
    private const int InternalT3 = ((byte)MachineCycleType.Internal << 8) | (byte)TState.T3;
    private const int InternalT4 = ((byte)MachineCycleType.Internal << 8) | (byte)TState.T4;
    private const int InternalT5 = ((byte)MachineCycleType.Internal << 8) | (byte)TState.T5;
    private const int InternalT6 = ((byte)MachineCycleType.Internal << 8) | (byte)TState.T6;

    private const int InterruptAckT1 = ((byte)MachineCycleType.InterruptAck << 8) | (byte)TState.T1;
    private const int InterruptAckT2 = ((byte)MachineCycleType.InterruptAck << 8) | (byte)TState.T2;
    private const int InterruptAckTw = ((byte)MachineCycleType.InterruptAck << 8) | (byte)TState.Tw;
    private const int InterruptAckT3 = ((byte)MachineCycleType.InterruptAck << 8) | (byte)TState.T3;
    private const int InterruptAckT4 = ((byte)MachineCycleType.InterruptAck << 8) | (byte)TState.T4;
}
