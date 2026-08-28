Apple II Dead Test RAM Diagnostic ROM
====================================

apple2dead.bin - 2,048 bytes, an F8-socket ($F800-$FFFF) diagnostic ROM for
the Apple II / II+. It runs with no working RAM: no zero page, no stack. On
start it tests the zero page and stack page, sizes the installed RAM, prints
"ZERO/STACK PAGES OK", then marches test patterns through all of main RAM,
reporting bad bits per page.

Source / credit
---------------
https://github.com/misterblack1/appleII_deadtest
by David Giller (KI3V), based on Frank IZ8DWF's original no-RAM test and
developed with Adrian Black.

Licensed under the GNU General Public License v2 (see the repository's
LICENSE.md) - unlike the Apple system ROMs under
Emulation/Systems/AppleII/Roms/, this image is free to redistribute, so it
is checked in here for use as a test fixture.

apple2dead.bin is the unmodified 2K build output from that repository.

Used by
-------
AppleIISystemTests.RunsAppleIIDeadTestDiagnosticRom, which loads it via the
$D000-$FFFF ROM-override path and runs the emulated machine until the
"ZERO/STACK PAGES OK" banner appears on the text screen.
