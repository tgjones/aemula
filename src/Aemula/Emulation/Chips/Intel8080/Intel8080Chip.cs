using System;
using System.Collections.Generic;
using Aemula.Emulation.Chips.Intel8080.UI;
using Aemula.UI;

namespace Aemula.Emulation.Chips.Intel8080;

public sealed partial class Intel8080Chip
{
    // Internal state
    private MachineCycleType _machineCycleType;
    private byte _machineCycle;
    private State _state;
    private byte _ir;  // Instruction register
    private byte _act; // Temporary accumulator
    private byte _tmp; // Temporary register
    private WZRegister _wz;
    private bool _condition; // Most-recently-evaluated condition
    private bool _interruptLatch;

    // Registers
    public PCRegister PC;
    public BCRegister BC;
    public DERegister DE;
    public HLRegister HL;
    public SPRegister SP;
    public byte A;

    // Flags
    public Intel8080Flags Flags;

    // Pins
    public Intel8080Pins Pins;

    public MachineCycleType CurrentMachineCycle => _machineCycleType;
    public State CurrentState => _state;
    internal int CombinedMachineCycleTypeAndState => CombineMachineCycleTypeAndState(_machineCycleType, _state);
    public bool InterruptLatch => _interruptLatch;

    public Intel8080Chip()
    {
        SetNextCycle(MachineCycleType.Fetch);
    }

    public void Cycle()
    {
        var machineCycleTypeAndState = CombineMachineCycleTypeAndState(_machineCycleType, _state);

        switch (machineCycleTypeAndState)
        {
            case FetchT1:
                Pins.Data = _interruptLatch ? StatusWordInterruptAcknowledge : StatusWordFetch;
                Pins.Sync = true;
                Pins.Wr = true;
                Pins.Address = PC.Value; // This will be overridden by some instructions like CALL and RET.
                if (_interruptLatch)
                {
                    Pins.IntE = false;
                }
                break;

            case FetchT2:
                Pins.DBIn = true;
                Pins.Sync = false;
                // TODO: Sample READY and HOLD pins
                // TODO: Check for HALT instruction.
                if (!_interruptLatch)
                {
                    PC.Value++;
                }
                break;

            case FetchT3:
                _interruptLatch = false;
                Pins.DBIn = false;
                _ir = Pins.Data;
                break;

            case MemoryReadT1:
                Pins.Data = StatusWordMemoryRead;
                Pins.Sync = true;
                Pins.Wr = true;
                break;

            case MemoryReadT2:
                Pins.Sync = false;
                Pins.DBIn = true;
                break;

            case MemoryReadT3:
                Pins.DBIn = false;
                break;

            case MemoryWriteT1:
                Pins.Data = StatusWordMemoryWrite;
                Pins.Sync = true;
                Pins.Wr = true;
                break;

            case MemoryWriteT2:
                Pins.Sync = false;
                break;

            case MemoryWriteT3:
                Pins.Wr = false;
                break;

            case StackReadT1:
                Pins.Data = StatusWordStackRead;
                Pins.Sync = true;
                Pins.Wr = true;
                Pins.Address = SP.Value;
                break;

            case StackReadT2:
                Pins.Sync = false;
                Pins.DBIn = true;
                break;

            case StackReadT3:
                Pins.DBIn = false;
                break;

            case StackWriteT1:
                Pins.Data = StatusWordStackWrite;
                Pins.Sync = true;
                Pins.Wr = true;
                Pins.Address = SP.Value;
                break;

            case StackWriteT2:
                Pins.Sync = false;
                break;

            case StackWriteT3:
                Pins.Wr = false;
                break;

            case StackWriteT4:
                Pins.Wr = true;
                break;

            case InputReadT1:
                Pins.Data = StatusWordInputRead;
                Pins.Sync = true;
                Pins.Wr = true;
                Pins.Address = _wz.Value;
                break;

            case InputReadT2:
                Pins.Sync = false;
                Pins.DBIn = true;
                break;

            case InputReadT3:
                Pins.DBIn = false;
                break;

            case OutputWriteT1:
                Pins.Data = StatusWordOutputWrite;
                Pins.Sync = true;
                Pins.Wr = true;
                Pins.Address = _wz.Value;
                break;

            case OutputWriteT2:
                Pins.Sync = false;
                break;

            case OutputWriteT3:
                Pins.Wr = false;
                break;
        }

        switch (_state)
        {
            case State.T1:
                _state = State.T2;
                break;

            case State.T2:
                _state = State.T3;
                break;

            case State.T3:
                _state = State.T4;
                break;

            case State.T4:
                _state = State.T5;
                break;
        }

        HandleInstruction(machineCycleTypeAndState);
    }

    private void SetNextCycle(MachineCycleType machineCycleType)
    {
        _machineCycleType = machineCycleType;

        if (machineCycleType == MachineCycleType.Fetch)
        {
            // This means the current state is the last state of the last cycle of the current instruction.
            // Time to check the interrupt pin.
            if (Pins.Int && Pins.IntE)
            {
                _interruptLatch = true;
            }

            _machineCycle = 1;
        }
        else
        {
            _machineCycle++;
        }

        _state = State.T1;
    }

    private void HandleInstruction(int machineCycleTypeAndState)
    {
        switch (_ir)
        {
            // MOV r1, r2
            case 0x40:
            case 0x41:
            case 0x42:
            case 0x43:
            case 0x44:
            case 0x45:
            case 0x47:
            case 0x48:
            case 0x49:
            case 0x4A:
            case 0x4B:
            case 0x4C:
            case 0x4D:
            case 0x4F:
            case 0x50:
            case 0x51:
            case 0x52:
            case 0x53:
            case 0x54:
            case 0x55:
            case 0x57:
            case 0x58:
            case 0x59:
            case 0x5A:
            case 0x5B:
            case 0x5C:
            case 0x5D:
            case 0x5F:
            case 0x60:
            case 0x61:
            case 0x62:
            case 0x63:
            case 0x64:
            case 0x65:
            case 0x67:
            case 0x68:
            case 0x69:
            case 0x6A:
            case 0x6B:
            case 0x6C:
            case 0x6D:
            case 0x6F:
            case 0x78:
            case 0x79:
            case 0x7A:
            case 0x7B:
            case 0x7C:
            case 0x7D:
            case 0x7F:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        _tmp = GetRegister(_ir & 0x7);
                        break;

                    case FetchT5:
                        GetRegister((_ir & 0x38) >> 3) = _tmp;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // MOV r, M
            case 0x46:
            case 0x4E:
            case 0x56:
            case 0x5E:
            case 0x66:
            case 0x6E:
            case 0x7E:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        GetRegister((_ir & 0x38) >> 3) = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // MOV M, r
            case 0x70:
            case 0x71:
            case 0x72:
            case 0x73:
            case 0x74:
            case 0x75:
            case 0x77:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        _tmp = GetRegister(_ir & 0x7);
                        SetNextCycle(MachineCycleType.MemoryWrite);
                        break;

                    case MemoryWriteT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryWriteT2:
                        Pins.Data = _tmp;
                        break;

                    case MemoryWriteT3:
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // SPHL
            case 0xF9:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SP.Lo = HL.L;
                        break;

                    case FetchT5:
                        SP.Hi = HL.H;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // MVI r, data
            case 0x06:
            case 0x0E:
            case 0x16:
            case 0x1E:
            case 0x26:
            case 0x2E:
            case 0x3E:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        GetRegister((_ir & 0x38) >> 3) = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // MVI M, data
            case 0x36:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.MemoryWrite);
                        break;

                    case MemoryWriteT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryWriteT2:
                        Pins.Data = _tmp;
                        break;

                    case MemoryWriteT3:
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // LXI rp, data
            case 0x01:
            case 0x11:
            case 0x21:
            case 0x31:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        if (_machineCycle == 2)
                        {
                            GetRegisterPairLo((_ir & 0x30) >> 4) = Pins.Data;
                            SetNextCycle(MachineCycleType.MemoryRead);
                        }
                        else
                        {
                            GetRegisterPairHi((_ir & 0x30) >> 4) = Pins.Data;
                            SetNextCycle(MachineCycleType.Fetch);
                        }
                        break;
                }
                break;

            // LDA addr
            case 0x3A:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        switch (_machineCycle)
                        {
                            case 2:
                            case 3:
                                Pins.Address = PC.Value;
                                break;

                            case 4:
                                Pins.Address = _wz.Value;
                                break;
                        }
                        break;

                    case MemoryReadT2:
                        switch (_machineCycle)
                        {
                            case 2:
                            case 3:
                                PC.Value++;
                                break;
                        }
                        break;

                    case MemoryReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                _wz.Z = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryRead);
                                break;

                            case 3:
                                _wz.W = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryRead);
                                break;

                            case 4:
                                A = Pins.Data;
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // STA addr
            case 0x32:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                _wz.Z = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryRead);
                                break;

                            case 3:
                                _wz.W = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryWrite);
                                break;
                        }
                        break;

                    case MemoryWriteT1:
                        Pins.Address = _wz.Value;
                        break;

                    case MemoryWriteT2:
                        Pins.Data = A;
                        break;

                    case MemoryWriteT3:
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // LHLD addr
            case 0x2A:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        switch (_machineCycle)
                        {
                            case 2:
                            case 3:
                                Pins.Address = PC.Value;
                                break;

                            case 4:
                            case 5:
                                Pins.Address = _wz.Value;
                                break;
                        }
                        break;

                    case MemoryReadT2:
                        switch (_machineCycle)
                        {
                            case 2:
                            case 3:
                                PC.Value++;
                                break;

                            case 4:
                                _wz.Value++;
                                break;
                        }
                        break;

                    case MemoryReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                _wz.Z = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryRead);
                                break;

                            case 3:
                                _wz.W = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryRead);
                                break;

                            case 4:
                                HL.L = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryRead);
                                break;

                            case 5:
                                HL.H = Pins.Data;
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // SHLD addr
            case 0x22:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                _wz.Z = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryRead);
                                break;

                            case 3:
                                _wz.W = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryWrite);
                                break;
                        }
                        break;

                    case MemoryWriteT1:
                        Pins.Address = _wz.Value;
                        break;

                    case MemoryWriteT2:
                        switch (_machineCycle)
                        {
                            case 4:
                                Pins.Data = HL.L;
                                _wz.Value++;
                                break;

                            case 5:
                                Pins.Data = HL.H;
                                break;
                        }
                        break;

                    case MemoryWriteT3:
                        switch (_machineCycle)
                        {
                            case 4:
                                SetNextCycle(MachineCycleType.MemoryWrite);
                                break;

                            case 5:
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // LDAX rp
            case 0x0A:
            case 0x1A:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = GetRegisterPair((_ir & 0x30) >> 4);
                        break;

                    case MemoryReadT3:
                        A = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // STAX rp
            case 0x02:
            case 0x12:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryWrite);
                        break;

                    case MemoryWriteT1:
                        Pins.Address = GetRegisterPair((_ir & 0x30) >> 4);
                        break;

                    case MemoryWriteT2:
                        Pins.Data = A;
                        break;

                    case MemoryWriteT3:
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // XCHG
            case 0xEB:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        _wz.Value = DE.Value;
                        DE.Value = HL.Value;
                        HL.Value = _wz.Value;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ADD r
            case 0x80:
            case 0x81:
            case 0x82:
            case 0x83:
            case 0x84:
            case 0x85:
            case 0x87:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Add();
                        break;

                    case FetchT4:
                        _tmp = GetRegister(_ir & 0x7);
                        _act = A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ADD M
            case 0x86:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Add();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ADI data
            case 0xC6:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Add();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ADC r
            case 0x88:
            case 0x89:
            case 0x8A:
            case 0x8B:
            case 0x8C:
            case 0x8D:
            case 0x8F:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Adc();
                        break;

                    case FetchT4:
                        _tmp = GetRegister(_ir & 0x7);
                        _act = A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ADC M
            case 0x8E:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Adc();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ACI data
            case 0xCE:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Adc();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // SUB r
            case 0x90:
            case 0x91:
            case 0x92:
            case 0x93:
            case 0x94:
            case 0x95:
            case 0x97:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Sub();
                        break;

                    case FetchT4:
                        _tmp = GetRegister(_ir & 0x7);
                        _act = A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // SUB M
            case 0x96:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Sub();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // SUI data
            case 0xD6:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Sub();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // SBB r
            case 0x98:
            case 0x99:
            case 0x9A:
            case 0x9B:
            case 0x9C:
            case 0x9D:
            case 0x9F:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Sbb();
                        break;

                    case FetchT4:
                        _tmp = GetRegister(_ir & 0x7);
                        _act = A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // SBB M
            case 0x9E:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Sbb();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // SBI data
            case 0xDE:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Sbb();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // INR r
            case 0x04:
            case 0x0C:
            case 0x14:
            case 0x1C:
            case 0x24:
            case 0x2C:
            case 0x3C:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        _tmp = (byte)(GetRegister((_ir & 0x38) >> 3) + 1);
                        Flags.AuxiliaryCarry = (_tmp & 0xF) == 0;
                        SetParityZeroSignBits(_tmp);
                        break;

                    case FetchT5:
                        GetRegister((_ir & 0x38) >> 3) = _tmp;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // INR M
            case 0x34:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = (byte)(Pins.Data + 1);
                        Flags.AuxiliaryCarry = (_tmp & 0xF) == 0;
                        SetParityZeroSignBits(_tmp);
                        SetNextCycle(MachineCycleType.MemoryWrite);
                        break;

                    case MemoryWriteT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryWriteT2:
                        Pins.Data = _tmp;
                        break;

                    case MemoryWriteT3:
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // DCR r
            case 0x05:
            case 0x0D:
            case 0x15:
            case 0x1D:
            case 0x25:
            case 0x2D:
            case 0x3D:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        _tmp = (byte)(GetRegister((_ir & 0x38) >> 3) - 1);
                        Flags.AuxiliaryCarry = (_tmp & 0xF) != 0xF;
                        SetParityZeroSignBits(_tmp);
                        break;

                    case FetchT5:
                        GetRegister((_ir & 0x38) >> 3) = _tmp;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // DCR M
            case 0x35:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = (byte)(Pins.Data - 1);
                        Flags.AuxiliaryCarry = (_tmp & 0xF) != 0xF;
                        SetParityZeroSignBits(_tmp);
                        SetNextCycle(MachineCycleType.MemoryWrite);
                        break;

                    case MemoryWriteT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryWriteT2:
                        Pins.Data = _tmp;
                        break;

                    case MemoryWriteT3:
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // INX rp
            case 0x03:
            case 0x13:
            case 0x23:
            case 0x33:
                switch (machineCycleTypeAndState)
                {
                    case FetchT5:
                        GetRegisterPair((_ir & 0x30) >> 4)++;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // DCX rp
            case 0x0B:
            case 0x1B:
            case 0x2B:
            case 0x3B:
                switch (machineCycleTypeAndState)
                {
                    case FetchT5:
                        GetRegisterPair((_ir & 0x30) >> 4)--;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // DAD rp
            case 0x09:
            case 0x19:
            case 0x29:
            case 0x39:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.Dad);
                        break;

                    case DadT1:
                        switch (_machineCycle)
                        {
                            case 2:
                                _act = GetRegisterPairLo((_ir & 0x30) >> 4);
                                break;

                            case 3:
                                _act = GetRegisterPairHi((_ir & 0x30) >> 4);
                                break;
                        }
                        break;

                    case DadT2:
                        switch (_machineCycle)
                        {
                            case 2:
                                _tmp = HL.L;
                                break;

                            case 3:
                                _tmp = HL.H;
                                break;
                        }
                        break;

                    case DadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                HL.L = (byte)(_act + _tmp);
                                Flags.Carry = (_act + _tmp) > 0xFF;
                                SetNextCycle(MachineCycleType.Dad);
                                break;

                            case 3:
                                HL.H = (byte)(_act + _tmp + (Flags.Carry ? 1 : 0));
                                Flags.Carry = (_act + _tmp + (Flags.Carry ? 1 : 0)) > 0xFF;
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // DAA
            case 0x27:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        Daa();
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ANA r
            case 0xA0:
            case 0xA1:
            case 0xA2:
            case 0xA3:
            case 0xA4:
            case 0xA5:
            case 0xA7:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Ana();
                        break;

                    case FetchT4:
                        _tmp = GetRegister(_ir & 0x7);
                        _act = A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ANA M
            case 0xA6:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Ana();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ANI data
            case 0xE6:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Ana();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // XRA r
            case 0xA8:
            case 0xA9:
            case 0xAA:
            case 0xAB:
            case 0xAC:
            case 0xAD:
            case 0xAF:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Xra();
                        break;

                    case FetchT4:
                        _act = A;
                        _tmp = GetRegister(_ir & 0x7);
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // XRA M
            case 0xAE:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Xra();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // XRI data
            case 0xEE:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Xra();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ORA r
            case 0xB0:
            case 0xB1:
            case 0xB2:
            case 0xB3:
            case 0xB4:
            case 0xB5:
            case 0xB7:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Ora();
                        break;

                    case FetchT4:
                        _tmp = GetRegister(_ir & 0x7);
                        _act = A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ORA M
            case 0xB6:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Ora();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // ORI data
            case 0xF6:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Ora();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // CMP r
            case 0xB8:
            case 0xB9:
            case 0xBA:
            case 0xBB:
            case 0xBC:
            case 0xBD:
            case 0xBF:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Cmp();
                        break;

                    case FetchT4:
                        _tmp = GetRegister(_ir & 0x7);
                        _act = A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // CMP M
            case 0xBE:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Cmp();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // CPI data
            case 0xFE:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Cmp();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        _tmp = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // RLC
            case 0x07:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Rlc();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // RRC
            case 0x0F:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Rrc();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // RAL
            case 0x17:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Ral();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // RAR
            case 0x1F:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        Rar();
                        break;

                    case FetchT4:
                        _act = A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // CMA
            case 0x2F:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        A = (byte)~A;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // CMC
            case 0x3F:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        Flags.Carry = !Flags.Carry;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // STC
            case 0x37:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        Flags.Carry = true;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // JMP addr
            case 0xC3:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT1:
                        Pins.Address = _wz.Value;
                        break;

                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        SetPCFromWZ();
                        break;

                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                _wz.Z = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryRead);
                                break;

                            case 3:
                                _wz.W = Pins.Data;
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // J cond addr
            case 0xC2:
            case 0xCA:
            case 0xD2:
            case 0xDA:
            case 0xE2:
            case 0xEA:
            case 0xF2:
            case 0xFA:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT1:
                        if (_condition)
                        {
                            Pins.Address = _wz.Value;
                        }
                        break;

                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        if (_condition)
                        {
                            SetPCFromWZ();
                        }
                        break;

                    // Error in Intel docs, which say this has 5 FETCH states?
                    case FetchT4:
                        JudgeCondition((_ir & 0x38) >> 3);
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                _wz.Z = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryRead);
                                break;

                            case 3:
                                _wz.W = Pins.Data;
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // CALL addr
            case 0xCD:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT1:
                        Pins.Address = _wz.Value;
                        break;

                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        SetPCFromWZ();
                        break;

                    case FetchT5:
                        SP.Value--;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                _wz.Z = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryRead);
                                break;

                            case 3:
                                _wz.W = Pins.Data;
                                SetNextCycle(MachineCycleType.StackWrite);
                                break;
                        }
                        break;

                    case StackWriteT2:
                        switch (_machineCycle)
                        {
                            case 4:
                                SP.Value--;
                                Pins.Data = PC.Hi;
                                break;

                            case 5:
                                Pins.Data = PC.Lo;
                                break;
                        }

                        break;

                    case StackWriteT3:
                        switch (_machineCycle)
                        {
                            case 4:
                                SetNextCycle(MachineCycleType.StackWrite);
                                break;

                            case 5:
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // C cond addr
            case 0xC4:
            case 0xCC:
            case 0xD4:
            case 0xDC:
            case 0xE4:
            case 0xEC:
            case 0xF4:
            case 0xFC:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT1:
                        if (_condition)
                        {
                            Pins.Address = _wz.Value;
                        }
                        break;

                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        if (_condition)
                        {
                            SetPCFromWZ();
                        }
                        break;

                    case FetchT5:
                        if (JudgeCondition((_ir & 0x38) >> 3))
                        {
                            SP.Value--;
                        }
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                _wz.Z = Pins.Data;
                                SetNextCycle(MachineCycleType.MemoryRead);
                                break;

                            case 3:
                                _wz.W = Pins.Data;
                                if (_condition)
                                {
                                    SetNextCycle(MachineCycleType.StackWrite);
                                }
                                else
                                {
                                    SetNextCycle(MachineCycleType.Fetch);
                                }
                                break;
                        }
                        break;

                    case StackWriteT2:
                        switch (_machineCycle)
                        {
                            case 4:
                                SP.Value--;
                                Pins.Data = PC.Hi;
                                break;

                            case 5:
                                Pins.Data = PC.Lo;
                                break;
                        }

                        break;

                    case StackWriteT3:
                        switch (_machineCycle)
                        {
                            case 4:
                                SetNextCycle(MachineCycleType.StackWrite);
                                break;

                            case 5:
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // RET
            case 0xC9:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT1:
                        Pins.Address = _wz.Value;
                        break;

                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        SetPCFromWZ();
                        break;

                    case FetchT4:
                        SetNextCycle(MachineCycleType.StackRead);
                        break;

                    case StackReadT2:
                        SP.Value++;
                        break;

                    case StackReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                _wz.Z = Pins.Data;
                                SetNextCycle(MachineCycleType.StackRead);
                                break;

                            case 3:
                                _wz.W = Pins.Data;
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // R cond addr
            case 0xC0:
            case 0xC8:
            case 0xD0:
            case 0xD8:
            case 0xE0:
            case 0xE8:
            case 0xF0:
            case 0xF8:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT1:
                        if (_condition)
                        {
                            Pins.Address = _wz.Value;
                        }
                        break;

                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        if (_condition)
                        {
                            SetPCFromWZ();
                        }
                        break;

                    case FetchT5:
                        if (JudgeCondition((_ir & 0x38) >> 3))
                        {
                            SetNextCycle(MachineCycleType.StackRead);
                        }
                        else
                        {
                            SetNextCycle(MachineCycleType.Fetch);
                        }
                        break;

                    case StackReadT2:
                        SP.Value++;
                        break;

                    case StackReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                _wz.Z = Pins.Data;
                                SetNextCycle(MachineCycleType.StackRead);
                                break;

                            case 3:
                                _wz.W = Pins.Data;
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // RST n
            case 0xC7:
            case 0xCF:
            case 0xD7:
            case 0xDF:
            case 0xE7:
            case 0xEF:
            case 0xF7:
            case 0xFF:
                switch (machineCycleTypeAndState)
                {
                    // This executes during the fetch of the next instruction.
                    case FetchT1:
                        Pins.Address = _wz.Value;
                        break;

                    // This executes during the fetch of the next instruction.
                    case FetchT2:
                        SetPCFromWZ();
                        break;

                    case FetchT3:
                        _wz.W = 0;
                        break;

                    case FetchT5:
                        SP.Value--;
                        SetNextCycle(MachineCycleType.StackWrite);
                        break;

                    case StackWriteT2:
                        switch (_machineCycle)
                        {
                            case 2:
                                SP.Value--;
                                Pins.Data = PC.Hi;
                                break;

                            case 3:
                                _wz.Z = (byte)(_ir & 0b111000);
                                Pins.Data = PC.Lo;
                                break;
                        }
                        break;

                    case StackWriteT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                SetNextCycle(MachineCycleType.StackWrite);
                                break;

                            case 3:
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // PCHL
            case 0xE9:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        PC.Lo = HL.L;
                        break;

                    case FetchT5:
                        PC.Hi = HL.H;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // PUSH rp
            case 0xC5:
            case 0xD5:
            case 0xE5:
                switch (machineCycleTypeAndState)
                {
                    case FetchT5:
                        SP.Value--;
                        SetNextCycle(MachineCycleType.StackWrite);
                        break;

                    case StackWriteT2:
                        switch (_machineCycle)
                        {
                            case 2:
                                SP.Value--;
                                Pins.Data = GetRegisterPairHi((_ir & 0x30) >> 4);
                                break;

                            case 3:
                                Pins.Data = GetRegisterPairLo((_ir & 0x30) >> 4);
                                break;
                        }

                        break;

                    case StackWriteT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                SetNextCycle(MachineCycleType.StackWrite);
                                break;

                            case 3:
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // PUSH PSW
            case 0xF5:
                switch (machineCycleTypeAndState)
                {
                    case FetchT5:
                        SP.Value--;
                        SetNextCycle(MachineCycleType.StackWrite);
                        break;

                    case StackWriteT2:
                        switch (_machineCycle)
                        {
                            case 2:
                                SP.Value--;
                                Pins.Data = A;
                                break;

                            case 3:
                                Pins.Data = Flags.AsByte();
                                break;
                        }

                        break;

                    case StackWriteT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                SetNextCycle(MachineCycleType.StackWrite);
                                break;

                            case 3:
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // POP rp
            case 0xC1:
            case 0xD1:
            case 0xE1:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.StackRead);
                        break;

                    case StackReadT2:
                        SP.Value++;
                        break;

                    case StackReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                GetRegisterPairLo((_ir & 0x30) >> 4) = Pins.Data;
                                SetNextCycle(MachineCycleType.StackRead);
                                break;

                            case 3:
                                GetRegisterPairHi((_ir & 0x30) >> 4) = Pins.Data;
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // POP PSW
            case 0xF1:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.StackRead);
                        break;

                    case StackReadT2:
                        SP.Value++;
                        break;

                    case StackReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                Flags.SetFromByte(Pins.Data);
                                SetNextCycle(MachineCycleType.StackRead);
                                break;

                            case 3:
                                A = Pins.Data;
                                SetNextCycle(MachineCycleType.Fetch);
                                break;
                        }
                        break;
                }
                break;

            // XTHL
            case 0xE3:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.StackRead);
                        break;

                    case StackReadT2:
                        switch (_machineCycle)
                        {
                            case 2:
                                SP.Value++;
                                break;
                        }
                        break;

                    case StackReadT3:
                        switch (_machineCycle)
                        {
                            case 2:
                                _wz.Z = Pins.Data;
                                SetNextCycle(MachineCycleType.StackRead);
                                break;

                            case 3:
                                _wz.W = Pins.Data;
                                SetNextCycle(MachineCycleType.StackWrite);
                                break;
                        }
                        break;

                    case StackWriteT2:
                        switch (_machineCycle)
                        {
                            case 4:
                                SP.Value--;
                                Pins.Data = HL.H;
                                break;

                            case 5:
                                Pins.Data = HL.L;
                                break;
                        }
                        break;

                    case StackWriteT3:
                        switch (_machineCycle)
                        {
                            case 4:
                                SetNextCycle(MachineCycleType.StackWrite);
                                break;
                        }
                        break;

                    case StackWriteT4:
                        HL.H = _wz.W;
                        break;

                    case StackWriteT5:
                        HL.L = _wz.Z;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // OUT port
            case 0xD3:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        _wz.W = Pins.Data;
                        _wz.Z = Pins.Data;
                        SetNextCycle(MachineCycleType.OutputWrite);
                        break;

                    case OutputWriteT2:
                        Pins.Data = A;
                        break;

                    case OutputWriteT3:
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // IN port
            case 0xDB:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Pins.Address = PC.Value;
                        break;

                    case MemoryReadT2:
                        PC.Value++;
                        break;

                    case MemoryReadT3:
                        _wz.W = Pins.Data;
                        _wz.Z = Pins.Data;
                        SetNextCycle(MachineCycleType.InputRead);
                        break;

                    case InputReadT3:
                        A = Pins.Data;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // EI
            case 0xFB:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        Pins.IntE = true;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // DI
            case 0xF3:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        Pins.IntE = false;
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            // NOP
            case 0x00:
                switch (machineCycleTypeAndState)
                {
                    case FetchT4:
                        SetNextCycle(MachineCycleType.Fetch);
                        break;
                }
                break;

            default:
                throw new InvalidOperationException($"Opcode 0x{_ir:X2} not supported");
        }
    }

    private ref byte GetRegister(int register)
    {
        switch (register)
        {
            case 0b000:
                return ref BC.B;

            case 0b001:
                return ref BC.C;

            case 0b010:
                return ref DE.D;

            case 0b011:
                return ref DE.E;

            case 0b100:
                return ref HL.H;

            case 0b101:
                return ref HL.L;

            case 0b111:
                return ref A;

            default:
                throw new InvalidOperationException();
        }
    }

    private ref byte GetRegisterPairLo(int register)
    {
        switch (register)
        {
            case 0b00:
                return ref BC.C;

            case 0b01:
                return ref DE.E;

            case 0b10:
                return ref HL.L;

            case 0b11:
                return ref SP.Lo;

            default:
                throw new InvalidOperationException();
        }
    }

    private ref byte GetRegisterPairHi(int register)
    {
        switch (register)
        {
            case 0b00:
                return ref BC.B;

            case 0b01:
                return ref DE.D;

            case 0b10:
                return ref HL.H;

            case 0b11:
                return ref SP.Hi;

            default:
                throw new InvalidOperationException();
        }
    }

    private ref ushort GetRegisterPair(int register)
    {
        switch (register)
        {
            case 0b00:
                return ref BC.Value;

            case 0b01:
                return ref DE.Value;

            case 0b10:
                return ref HL.Value;

            case 0b11:
                return ref SP.Value;

            default:
                throw new InvalidOperationException();
        }
    }

    private bool JudgeCondition(int ccc)
    {
        return _condition = ccc switch
        {
            0b000 => !Flags.Zero,
            0b001 => Flags.Zero,
            0b010 => !Flags.Carry,
            0b011 => Flags.Carry,
            0b100 => !Flags.Parity,
            0b101 => Flags.Parity,
            0b110 => !Flags.Sign,
            0b111 => Flags.Sign,
            _ => throw new InvalidOperationException()
        };
    }

    private void DoAlu(
        byte operand,
        byte carryIn,
        out byte result,
        out bool carryOut)
    {
        var newValue = _act + carryIn + operand;

        carryOut = newValue > byte.MaxValue;
        Flags.AuxiliaryCarry = (_act & 0xF) + (operand & 0xF) + carryIn > 0xF;

        result = (byte)newValue;

        SetParityZeroSignBits(result);
    }

    private void Add()
    {
        DoAlu(_tmp, 0, out A, out Flags.Carry);
    }

    private void Adc()
    {
        DoAlu(_tmp, (byte)(Flags.Carry ? 1 : 0), out A, out Flags.Carry);
    }

    private void Cmp()
    {
        DoAlu((byte)(~_tmp), 1, out _, out var carry);
        Flags.Carry = !carry;
    }

    private void Sub()
    {
        DoAlu((byte)(~_tmp), 1, out A, out var carry);
        Flags.Carry = !carry;
    }

    private void Sbb()
    {
        DoAlu((byte)(~_tmp), (byte)(Flags.Carry ? 0 : 1), out A, out var carry);
        Flags.Carry = !carry;
    }

    private void Ana()
    {
        Flags.Carry = false;

        // From the Intel 8080/8085 Assembly Language Programming manual:
        // "The 8080 logical AND instructions set the flag to reflect the logical OR
        // of bit 3 of the values involved in the AND operation."
        Flags.AuxiliaryCarry = (((_act | _tmp) >> 3) & 0x1) == 0x1;

        A = (byte)(_act & _tmp);

        SetParityZeroSignBits(A);
    }

    private void Xra()
    {
        Flags.Carry = false;
        Flags.AuxiliaryCarry = false;

        A = (byte)(_act ^ _tmp);

        SetParityZeroSignBits(A);
    }

    private void Ora()
    {
        Flags.Carry = false;
        Flags.AuxiliaryCarry = false;

        A = (byte)(_act | _tmp);

        SetParityZeroSignBits(A);
    }

    private void Daa()
    {
        byte adjustment = 0;

        if (Flags.AuxiliaryCarry || (A & 0xF) > 9)
        {
            adjustment = 6;
        }

        if (Flags.Carry || A > 0x99)
        {
            // Carry is only set, never reset.
            Flags.Carry = true;
            adjustment |= 0x60;
        }

        _act = A;

        DoAlu(adjustment, 0, out A, out _);
    }

    private void Rlc()
    {
        var carryBit = _act >> 7;
        Flags.Carry = carryBit == 0x1;
        A = (byte)((_act << 1) | carryBit);
    }

    private void Ral()
    {
        var carryBit = _act >> 7;
        A = (byte)((_act << 1) | (Flags.Carry ? 1 : 0));
        Flags.Carry = carryBit == 0x1;
    }

    private void Rrc()
    {
        var carryBit = _act & 0x1;
        Flags.Carry = carryBit == 0x1;
        A = (byte)((_act >> 1) | (carryBit << 7));
    }

    private void Rar()
    {
        var carryBit = _act & 0x1;
        A = (byte)((_act >> 1) | ((Flags.Carry ? 1 : 0) << 7));
        Flags.Carry = carryBit == 0x1;
    }

    private void SetParityZeroSignBits(byte result)
    {
        Flags.Parity = ParityValues[result];

        Flags.Zero = result == 0;

        Flags.Sign = (result & 0x80) != 0;
    }

    private void SetPCFromWZ()
    {
        // If this is an INTERRUPT machine cycle,
        // prevent PC incrementing.
        PC.Value = (ushort)(_wz.Value + (_interruptLatch ? 0 : 1));
    }

    public const byte StatusWordFetch = 0b10100010;
    public const byte StatusWordMemoryRead = 0b10000010;
    public const byte StatusWordMemoryWrite = 0b00000000;
    public const byte StatusWordStackRead = 0b10000110;
    public const byte StatusWordStackWrite = 0b00000100;
    public const byte StatusWordInputRead = 0b01000010;
    public const byte StatusWordOutputWrite = 0b00100011;
    public const byte StatusWordInterruptAcknowledge = 0b00100011;
    public const byte StatusWordHaltAcknowledge = 0b10001010;
    public const byte StatusWordInterruptAcknowledgeWhileHalt = 0b00101011;

    public enum MachineCycleType : byte
    {
        Fetch,
        MemoryRead,
        MemoryWrite,
        StackRead,
        StackWrite,
        InputRead,
        OutputWrite,
        InterruptAcknowledge,
        HaltAcknowledge,
        InterruptAcknowledgeWhileHalt,
        Dad, // Special cycle type for DAD instruction
    }

    public enum State : byte
    {
        T1,
        T2,
        T3,
        T4,
        T5,
    }

    private const int FetchT1 = ((byte)MachineCycleType.Fetch << 8) | (byte)State.T1;
    private const int FetchT2 = ((byte)MachineCycleType.Fetch << 8) | (byte)State.T2;
    private const int FetchT3 = ((byte)MachineCycleType.Fetch << 8) | (byte)State.T3;
    private const int FetchT4 = ((byte)MachineCycleType.Fetch << 8) | (byte)State.T4;
    private const int FetchT5 = ((byte)MachineCycleType.Fetch << 8) | (byte)State.T5;

    private const int MemoryReadT1 = ((byte)MachineCycleType.MemoryRead << 8) | (byte)State.T1;
    private const int MemoryReadT2 = ((byte)MachineCycleType.MemoryRead << 8) | (byte)State.T2;
    private const int MemoryReadT3 = ((byte)MachineCycleType.MemoryRead << 8) | (byte)State.T3;

    private const int MemoryWriteT1 = ((byte)MachineCycleType.MemoryWrite << 8) | (byte)State.T1;
    private const int MemoryWriteT2 = ((byte)MachineCycleType.MemoryWrite << 8) | (byte)State.T2;
    private const int MemoryWriteT3 = ((byte)MachineCycleType.MemoryWrite << 8) | (byte)State.T3;

    private const int StackReadT1 = ((byte)MachineCycleType.StackRead << 8) | (byte)State.T1;
    private const int StackReadT2 = ((byte)MachineCycleType.StackRead << 8) | (byte)State.T2;
    private const int StackReadT3 = ((byte)MachineCycleType.StackRead << 8) | (byte)State.T3;

    private const int StackWriteT1 = ((byte)MachineCycleType.StackWrite << 8) | (byte)State.T1;
    private const int StackWriteT2 = ((byte)MachineCycleType.StackWrite << 8) | (byte)State.T2;
    private const int StackWriteT3 = ((byte)MachineCycleType.StackWrite << 8) | (byte)State.T3;
    private const int StackWriteT4 = ((byte)MachineCycleType.StackWrite << 8) | (byte)State.T4;
    private const int StackWriteT5 = ((byte)MachineCycleType.StackWrite << 8) | (byte)State.T5;

    private const int DadT1 = ((byte)MachineCycleType.Dad << 8) | (byte)State.T1;
    private const int DadT2 = ((byte)MachineCycleType.Dad << 8) | (byte)State.T2;
    private const int DadT3 = ((byte)MachineCycleType.Dad << 8) | (byte)State.T3;

    private const int InputReadT1 = ((byte)MachineCycleType.InputRead << 8) | (byte)State.T1;
    private const int InputReadT2 = ((byte)MachineCycleType.InputRead << 8) | (byte)State.T2;
    private const int InputReadT3 = ((byte)MachineCycleType.InputRead << 8) | (byte)State.T3;

    private const int OutputWriteT1 = ((byte)MachineCycleType.OutputWrite << 8) | (byte)State.T1;
    private const int OutputWriteT2 = ((byte)MachineCycleType.OutputWrite << 8) | (byte)State.T2;
    private const int OutputWriteT3 = ((byte)MachineCycleType.OutputWrite << 8) | (byte)State.T3;

    private static int CombineMachineCycleTypeAndState(MachineCycleType machineCycleType, State state)
    {
        return ((byte)machineCycleType << 8) | (byte)state;
    }

    public IEnumerable<DebuggerWindow> CreateDebuggerWindows()
    {
        yield return new CpuStateWindow(this);
    }
}
