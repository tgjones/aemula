namespace Aemula.Emulation.Output;

// Lets tooling (e.g. a headless runner) get at a system's Television
// generically, without switching on concrete system type.
public interface IHasTelevision
{
    Television Television { get; }
}
