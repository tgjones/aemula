namespace Aemula.Emulation.Chips.Intel8080;

public struct Intel8080Pins
{
    /// <summary>
    /// Address bus.
    /// </summary>
    public ushort Address;

    /// <summary>
    /// Data bus.
    /// </summary>
    public byte Data;

    /// <summary>
    /// Clears the program counter, and INTE and HLDA flip/flops.
    /// </summary>
    public bool Reset;

    /// <summary>
    /// Requests the CPU to enter the HOLD state.
    /// </summary>
    public bool Hold;

    /// <summary>
    /// Interrupt request. Will be recognized at the end of the current instruction or while halted.
    /// </summary>
    public bool Int;

    /// <summary>
    /// Interrupt enable. Indicates the content of the internal interrupt enable flip/flop.
    /// </summary>
    public bool IntE;

    /// <summary>
    /// Data bus in. Indicates that the data bus is in the input mode.
    /// </summary>
    public bool DBIn;

    /// <summary>
    /// Write. Used for memory write or I/O output control.
    /// </summary>
    public bool Wr;

    /// <summary>
    /// Synchronizing signal. Indicates the beginning of each machine cycle.
    /// </summary>
    public bool Sync;

    /// <summary>
    /// Acknowledges that the CPU is in a WAIT state.
    /// </summary>
    public bool Wait;

    /// <summary>
    /// Indicates to the CPU that valid memory or input data is available on the data bus.
    /// </summary>
    public bool Ready;

    /// <summary>
    /// Hold acknowledge. Appears in response to the HOLD signal.
    /// </summary>
    public bool HldA;
}
