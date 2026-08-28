# Apple II

## Information

* [Understanding the Apple II](https://archive.org/details/understanding_the_apple_ii) by Jim Sather
* [The Apple II Circuit Description](https://archive.org/details/apple-ii-circuit-description) by Winston Gayler

## ROMs

Apple II ROM images sourced from [AppleWin](https://github.com/AppleWin/AppleWin).

Passing a file path as the program argument (`appleii <path>`) overlays that
image onto the `$D000`-`$FFFF` ROM space. A full 12K image replaces the lot; a
shorter image (e.g. the 2K [Apple II Dead Test](https://github.com/misterblack1/appleII_deadtest),
an F8-socket ROM) is mapped at the top of the space, leaving the bundled
Applesoft image in the lower sockets. Anything over 12K is rejected.