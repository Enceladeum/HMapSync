# Re-measuring MapDiscoveryManager after a patch

The map-reveal feature writes raw discovery bytes through a pointer computed from three
constants measured out of `ffxiv_dx11.exe`: `Disc16Count`, `Disc32Count`, and `Disc32Base`
(in `HMSyncPlugin.cs`, next to `ResolveDiscoveryTable`). A game patch can grow the discovery
arrays or move the struct, at which point those constants go stale and a reveal write lands
in the wrong place (or is refused). This is the recipe to re-measure them.

Symptom: an `[HMSync] [MAPREVEAL]` line reports a map "uses DiscoveryIndex N, but this build
measured the 16/32-region array as holding only M entries". (The `IndexOutOfRange` class of
`DiscResolve` — auto-reveal is skipped and logs a warning; manual `/hms mapreveal` prints it.)

1. Sig-scan `MapDiscoveryManager.IsRegionDiscovered` in `ffxiv_dx11.exe`:
   `33 C0 4C 8B D1 66 3B C2 7F ?? 45 84 C9 74 ?? B8 ?? ?? ?? ?? 66 3B D0 73 ?? 48 0F BF C2`
2. Disassemble it:
   - 16-region bound → `mov eax, imm32`   (0xA2 = 162 in 7.55)
   - 32-region bound → `cmp dx, imm8`     (0x31 =  49 in 7.55)
   - 32-region base  → `add rax, 0x51` then `shl 5` → 0x51 * 0x20 = 0xA20
3. `ReportCooldown` sits after both arrays — find via the update fn
   (`movss xmm0,[rcx+off]; subss; movss`) or a reset (`mov [rcx+off], 0`). Struct size = off + 4.
4. Cross-check against the Map sheet: the max `DiscoveryIndex` in each `DiscoveryArrayByte` family
   must be < the corresponding bound. If it isn't, the binary read is wrong.
5. Update `Disc16Count` / `Disc32Count` / `Disc32Base` together, and the size/cooldown in the comment.

DO NOT take these from FFXIVClientStructs. As of 2026-07-28 CS main still declares `Size = 0x1024`
and `FixedSizeArray48` — stale for 7.55. CS is ground truth for SIGNATURES, not for these counts.
