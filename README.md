# MKW Ultrawide Patcher

A small Windows tool that adds **ultrawide (21:9 / 32:9) support to Mario Kart World** running on the
[Eglantine](https://github.com/Djihads80/Eglantine) Switch 2 emulator. It patches the game's `main` executable
so the 3D camera shows more on the sides instead of being stretched. It is fully **reversible**.

> **Tested only with Mario Kart World 1.8.0 (update `917504`, Build ID `D00E4F399E411D0613AA8D26B1C02FEF`) on Eglantine.**
> Other versions use auto-detection and may or may not work.

## Download

**[⬇ Download UltrawidePatcher.exe](https://github.com/rocny10/mkw-ultrawide-patcher/raw/main/release/UltrawidePatcher.exe)**
(also in the [`release`](release) folder). No install and no Python needed
(it uses .NET Framework 4.x, which ships with Windows 10/11).

Windows SmartScreen may warn because the exe is not signed: click **More info → Run anyway**.

## How to use

1. Copy `UltrawidePatcher.exe` into the game's `exefs` folder (next to the `main` file) and open it.
   Or open it from anywhere and use **Change...** to pick the `exefs` folder.
2. Choose your monitor resolution (presets or custom).
3. Choose what to patch — **Everything (recommended)**.
4. Click **Apply patch**.
5. In Eglantine set **Aspect Ratio → Stretch to Window** (or 21:9).
6. Launch the game.

To undo it, click **Restore original**: `main` goes back to being byte-for-byte identical to what you had.

### Notes

- The first patch creates `main.original` as a backup. Don't delete it while the patch is applied.
- You can switch resolution/variant without restoring first — it always patches from the original.
- The patched `main` is stored decompressed (bigger file). That's normal.
- If the game is updated, the tool detects the new `main` and uses it as the new original.

## Patch variants

| Variant | Result |
|---|---|
| **Everything** (recommended) | Cameras + UI with correct proportions. In menus the 3D scenery shows on the sides. |
| Game cameras only | Track looks right, menus fill the screen, but the HUD stays stretched. |
| Layer system only | Experimental, for testing. |

## How it works

Every frame the game (`sead::PerspectiveProjection`) recomputes its aspect ratio as
`viewport width / viewport height` (an ARM64 `fdiv`). The tool:

1. Decompresses the `main` NSO (LZ4).
2. Finds those `fdiv` instructions (known offsets for 1.8.0, heuristic search otherwise).
3. Replaces each one with a branch to a small code cave appended to the end of `.text` that runs
   the original `fdiv`, multiplies the result by `target_ratio / (16/9)`, and branches back.
4. Writes an uncompressed NSO and keeps a backup for restoring.

Because the result is *multiplied* (not overwritten), split-screen and cameras with other aspect ratios keep
their relative proportions.

Patch sites for 1.8.0: `0x8A6D4`, `0x8A784` (layer system) and `0xDEFAFC`, `0xDEFDD4`, `0xDEFEC0` (game cameras).

## Limitations

- FPS stays at 60 — the cap comes from the game and from Eglantine's 60 Hz present pacing.
- The HUD/menus are drawn into a 16:9 buffer; depending on the variant, menus show scenery on the sides
  or the HUD is stretched.

## Building from source

- **Windows:** run `build.bat` (uses the `csc.exe` included with .NET Framework 4.x).
- **Linux/macOS:** install Mono (`mono-mcs`, `mono-devel`) and run `./build.sh`.

Output goes to `dist/UltrawidePatcher.exe`.

## Disclaimer

This project contains no game code or assets; it only modifies a file you already own.
Not affiliated with Nintendo or the Eglantine/Eden developers.
