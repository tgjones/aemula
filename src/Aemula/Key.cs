namespace Aemula;

// A stable, layout-resolved but modifier-independent key identity - what
// SDL's own keycode already is for the printable-ASCII range (SDLK_a == 'a',
// SDLK_RETURN == 0x0D, SDLK_SEMICOLON == ';', and so on), which is why every
// printable key below carries its own ASCII value rather than an arbitrary
// number: a host keycode in that range casts straight across with no lookup
// table entry, in EmulationWindow's SDL->Key translation. Only the
// non-printable keys actually matched by name below (arrows, right shift)
// need an explicit table entry, since their SDL keycodes are
// scancode-derived and don't fall in the ASCII range.
public enum Key
{
    None = 0,

    Space = ' ',
    Return = 0x0D,
    Escape = 0x1B,
    Backspace = 0x08,
    Delete = 0x7F,

    Digit0 = '0',
    Digit1 = '1',
    Digit2 = '2',
    Digit3 = '3',
    Digit4 = '4',
    Digit5 = '5',
    Digit6 = '6',
    Digit7 = '7',
    Digit8 = '8',
    Digit9 = '9',

    A = 'a',
    B = 'b',
    C = 'c',
    D = 'd',
    E = 'e',
    F = 'f',
    G = 'g',
    H = 'h',
    I = 'i',
    J = 'j',
    K = 'k',
    L = 'l',
    M = 'm',
    N = 'n',
    O = 'o',
    P = 'p',
    Q = 'q',
    R = 'r',
    S = 's',
    T = 't',
    U = 'u',
    V = 'v',
    W = 'w',
    X = 'x',
    Y = 'y',
    Z = 'z',

    // Non-printable: no natural ASCII value, so these get arbitrary values
    // outside the ASCII range.
    Up = 256,
    Down,
    Left,
    Right,
    RightShift,
}
