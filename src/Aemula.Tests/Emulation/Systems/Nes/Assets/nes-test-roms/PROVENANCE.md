# NES test ROMs

A curated subset of <https://github.com/christopherpow/nes-test-roms>, used to
drive the NES PPU implementation (see `docs/nes-ppu-plan.md`). Each subdirectory
keeps the upstream layout and its `readme.txt` documents the pass/fail codes.

These are freely redistributable test ROMs written by blargg (Shay Green) and
others for verifying NES emulator accuracy.

| Directory | Upstream author | Result protocol |
| --- | --- | --- |
| `blargg_ppu_tests_2005.09.15b/` | blargg | on-screen text + zero-page `result` ($F8) |
| `ppu_vbl_nmi/rom_singles/` | blargg | `$6000` status + `$DE $B0 $61` at `$6001` + text at `$6004` |
| `sprite_hit_tests_2005.10.05/` | blargg | on-screen text + zero-page `result` ($F8) |
| `sprite_overflow_tests/` | blargg | on-screen text + zero-page `result` ($F8) |
| `oam_read/` | blargg | `$6000` protocol |
| `ppu_open_bus/` | blargg | `$6000` protocol |
| `full_palette/` | Rahsennor | visual (framebuffer hash) |
