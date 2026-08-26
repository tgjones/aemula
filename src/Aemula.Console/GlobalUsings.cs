// This project's own namespace, Aemula.Console, textually collides with
// System.Console - unqualified Console.WriteLine etc. inside `namespace
// Aemula.Console` resolves to this namespace itself rather than the BCL type
// (confirmed by build failure without this alias). SystemConsole sidesteps it.
global using SystemConsole = System.Console;
