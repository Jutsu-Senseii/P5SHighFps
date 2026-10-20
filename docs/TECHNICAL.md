# Technical notes

All addresses are relative to `game.exe` (Steam v1.03, TimeDateStamp 1632810770, image base `0x140000000`).

## Engine timing

| Address | Meaning |
|---|---|
| `+2284504` | Frame divisor (int): 1 = 60 FPS, 2 = 30 FPS |
| `+2284508` | Frame cap (int) |
| `+228450C` | Frame cap (float) |
| `+5B20B0` | `SetFrameRate(divisor)`: writes the three values above |
| `+5B20F0` (thunk `+80BBB`) | Divisor getter, called from ~240 places |
| `+5B6297` | Runs exactly once per frame (`frame_counter += divisor`), measured at 120.0 calls/s in every game state tested |

Systems convert the divisor to float (`cvtsi2ss`) to get a step in 1/60 s ticks, or add it to integer counters.

## Fix families

1. **Floating-point step sites** (`cvtsi2ss xmmN, r64` after the getter). Each is replaced with `call thunk_N`, where
   the thunk loads `60 / target` into `xmmN`. The list starts from zettozoid's static cheat table for v1.02, minus
   the sites its author found harmful (`135A37`, `2054E5`, `75F187`, `7CDC2D`, `450D3C`), plus:
   - `2BB351C`, `2BE0236`, `356C1F6`, `19597532`, `1967F748`: `.trace` section sites relocated in v1.03;
   - `20FCCE`: the helper at `20FCC0` returning `divisor × game speed / 60` (seconds per frame);
   - `1589DF`, `158B47`, `158C8B`: timers missing from the cheat table; with integer ticks they made NPCs vanish
     depending on the camera;
   - `153F5F`, `153F86`: twins of `152F94` / `152FBC` in the cable slide (zipline) update, also missing from the
     cheat table; with integer ticks the character fell off the cable about one time in three.

   Left out on purpose (found by live bisection): replacing `cvtsi2ss` by the step is only valid when the converted
   value is the plain divisor.
   - **Gauges** (`700254`, `7002CE`, `704EC4`, `704F3E`, `7061D4`, `70624E`, `6FE764`, `6FE7DE`, and the unpatched twins
     `6FAAF4`, `6FAB72`): HP/SP counters move toward their target with `rate = max(gap / duration, floor)`, the rate is
     computed once (while it is 0), then `value = (int)(value + rate)` on every update. A floor below 1 (a step of 0.5,
     or a divisor of 0 every other frame) leaves a rate that never moves the gauge: the in-game HP/SP counter stayed
     behind the real value after a fight (the menu was right). Their divisor reads (`70024A`, `7002C4`, `704EBA`,
     `704F34`, `7061CA`, `706244`, `6FAAEA`, `6FAB68`, `6FE75A`, `6FE7D4`) are redirected to the getter returning 1,
     as at 60 FPS. The duration is counted in updates, so the gauges also filled twice as fast at 120 FPS (four times at
     240): the `cvtdq2ps xmm0, xmm0 ; divss xmm6, xmm0` right before each of these reads (`700243`, `7002BD`, `704EB3`,
     `704F2D`, `7061C3`, `70623D`, `6FAAE3`, `6FAB61`, `6FE753`, `6FE7CD`) is replaced by a call that multiplies the
     duration by `target / 60`, so they take the same real time as at 60 FPS.
   - **Enemy detection** (`2E6C20`, `2E6C88`): `imul eax, rate` runs between the getter and the conversion. The constant
     dropped the multiplication, the detection gauge filled far too slowly and enemies never ambushed the player.
   - **A counter, not the divisor** (`6DBC9E`): converts `min(counter + divisor, limit)`.
   - **A whole number of frames** (`14BE2A`): `(int)(divisor * 1.0)` is passed to a unit method (`[vtable+4B0h]`). The
     step 0.5 truncates to 0, so the method never received a frame. Found by reviewing every patched site for a
     float-to-integer truncation after the step.

   Unpatched, they follow the integer tick sequence (1, 0, 1, 0...), which is correct on average.

   `SetFrameRate` is redirected so the divisor stays 1 and the cap stays at the target.
2. **Relocated `.trace` sites**: see above.
3. **Seconds-per-frame helper** (`20FCCE`).
4. **UI fixed-step animations**: the animation player at `B96F30` (`time += step`) uses `[object+38h]` as its step in
   fixed-step mode. `B96F55` (`movss xmm1, [rdx+38h]`) is replaced by a call that multiplies it by `60 / target`.
   The cheat table also halved the speed when the animation started (`6B8C4A`, `7719E9`, `771E59`); those sites are
   left unpatched so speed stays correct even when the frame rate changes during an animation.
5. **Integer counters** (default; `--no-int-ticks` disables them): the once-per-frame anchor `5B6297` calls a stub
   that accumulates `60` per frame modulo `target` and writes the number of whole ticks to the divisor
   (1, 0, 1, 0... at 120 FPS). Integer counters (`counter += divisor`) therefore keep real-time speed. Calls that must
   never see 0 are redirected to a getter returning 1:
   - integer divisions by the divisor: `16AD9E`, `16ADA8`, `16AE7D`, `16AE87`, `188B94`, `188C2C`, `188E6E`,
     `188FF2`, `18908D`, `2F4F0B`, `2F4F24`, `2BD65BE`, `2BD6629`;
   - float divisions by a value proportional to it: `14C74E`, `1B0405`, `1FB8AE`, `3C2025`;
   - gauge floors (HP/SP counters, see above): `70024A`, `7002C4`, `704EBA`, `704F34`, `7061CA`, `706244`, `6FAAEA`, `6FAB68`, `6FE75A`, `6FE7D4`;
   - one-time UI animation speed setup: `6B8C2E`, `7719D3`, `771E43`;
   - a loop that samples a curve with `t += divisor` until `t` reaches its end (Morgana's van summon): `147CF6`.
     With 0 the loop never ends and the game freezes;
   - conversions the cheat table had excluded (enemy spawning, attack particles): `135A2D`, `450D1B`, `2054D6`.

   Eye blinking (`115100`) advances `round(step × 60)` frames per update, which is always 1 at 120 FPS: `11511C`
   (`cvttss2si r8d, xmm0`) is replaced by a read of the divisor.

6. **Motions snapped to whole frames**: the motion time advance at `1FD5D0` (`time += step`, time at `[motion+B8h]`,
   length at `[motion+114h]`, owner type at `[owner+20h]`) already contains a branch (`1FD666`) that snaps the time
   to whole frames and carries the remainder to the next update, used by the engine for some unit types. Two cases
   break when sampled between frames and are routed to that branch by a stub at `1FD61E`:
   - motions of 2 frames or less (the Persona showcase pose, looped by restarting it): the scene was drawn misplaced
     every other frame on the Persona screen;
   - clips of exactly 51 or 81 frames in the pool of owner types 80 to 109 (the characters' motion players; the engine
     ignores types from 110 up): the spark played when two characters clap hands during a character switch looked
     doubled (two copies half a frame apart) at 120 FPS. Snapping the whole pool, or clips of 11 to 31 frames, made
     every character start running with its pose stepping at 60 Hz while the position moved at 120 Hz (about a
     second, gone when blocked by a wall), so only these two durations are snapped.

   Everything else keeps a fractional time and stays smooth. Found by bisecting the step sites live (`206880`, the
   per-unit update, receives the step), then logging the (owner type, length) pairs seen during switches against
   the idle and running ones and bisecting the durations (`P5SScan` is not needed for this).

7. **Lock-on camera dead zone**: the lock-on camera (`505E90`) turns toward the target each update by an angle limited
   by a speed that is scaled by the camera step (written at `4FC39F`), then applies a dead zone that is not:
   `if |angle| < threshold: no turn, else angle -= threshold`. At 240 FPS the angle per update fell below the threshold
   and the camera never turned toward a new target; at 120 FPS the threshold was removed twice as often and the turn
   took twice as long. `50623A` (`mulss xmm2, xmm12`, the threshold) becomes a call that also multiplies it by the step.
   Found by bisecting the step sites live, then a hardware read watchpoint on the camera step to list its readers.
   Leaving `4FC39F` unpatched instead made the camera update only on ticks (every other frame at 120 FPS), and the
   reticles shook while the camera followed a locked enemy.

About 65 other floating-point conversions of the divisor are left alone; with integer ticks they receive 1/0
alternately, which is correct on average. They were not patched by the cheat table either.

## Code cave

The address space around the module is fully reserved by the game, so nothing can be allocated within reach of a
`rel32` call. Thunks and data live in the int3 padding of the `.arch` section (65 KB of `0xCC` at `8DF91`); the
patcher uses `96000`–`963FF` and makes that page writable for its counters. It refuses to patch if the area is not
empty.

| Offset | Content |
|---|---|
| `+000` | Step (float, `60 / target`) |
| `+008` | Frame counter (u64), incremented by the anchor |
| `+010`–`+09F` | `movss xmmN, [step] ; ret` thunks, one per register, `0x10` apart |
| `+0B0` | Tick accumulator (u32) |
| `+100` | `SetFrameRate` replacement |
| `+140` / `+180` | Anchor stubs: frame count only / integer ticks |
| `+1E0` | Getter returning 1 |
| `+200` / `+210` | Eye blink stub / UI step stub |
| `+230` / `+234` / `+238` | Constants: 1 (int), 2.0, 0.0 |
| `+240` | Short motion stub |

## Frame rate presets (F8 / F9)

F8 selects 120 FPS and F9 selects 240 FPS; pressing the active key turns the patch off, pressing the other one switches.
A switch runs with the game suspended: `CodeCave.Retarget` rewrites the step (`60 / target`), the integer tick stub
(`div` by the target), the `SetFrameRate` replacement and resets the tick accumulator, then the frame globals
(divisor, cap int and float) are written. `--fps=N` only selects the frame rate of the automatic activation.

## Diagnostics (`--diagnostics`)

- **F7** disables one family of fixes at a time (1 → 8, then all on), to find which family causes a problem.
- **F5 / F6** bisect the ~220 divisor readers: half of the suspects are forced to 1, you answer "bug gone" (F5) or
  "bug present" (F6), and after about 8 answers the log names the culprit. This is how the NPC timers were found.

## Code layout (`src/P5SHighFps`)

| File | Role |
|---|---|
| `Program.cs` | Entry point: options, game process, activation, patching, key loop |
| `Options.cs` | Command line options |
| `Log.cs` | Console and `P5SHighFps.log` |
| `Game/Addresses.cs` | Every address, expected byte sequence and site list (start here to port to another version) |
| `Game/GameLauncher.cs` | Starts the game from Steam launch options, waits for it, checks the version |
| `Patching/CodeCave.cs` | Code cave layout and the x86-64 stubs written there |
| `Patching/Patch.cs` | A patch (address, original bytes, replacement, family) and the diagnostics families |
| `Patching/PatchBuilder.cs` | Builds and verifies the patch list |
| `Patching/Patcher.cs` | Applies / removes the patches with the game suspended |
| `Diagnostics/Bisection.cs` | F5/F6 bisection |
| `Native/RemoteProcess.cs` | Reads and writes the game's memory (Windows API) |
| `UI/Overlay.cs`, `UI/Feedback.cs`, `UI/Keyboard.cs` | On-screen banner, beeps, global key polling |

## Research tools (`tools/P5SScan`)

`P5SScan.exe` must run while the game runs; measurements start with F9 in game.

| Command | What it does |
|---|---|
| `P5SScan.exe [--seconds=N]` | Scans all writable memory for frame-locked counters (+1/−1 or ±1/60 per frame) and lists looping ones with their period in `candidates.txt` |
| `P5SScan.exe --watch=list.txt` | Measures the listed addresses again (compare at 60 and 120 FPS) |
| `P5SScan.exe --trace=ADDR[,ADDR...]` | Records the instructions writing to up to four addresses, using hardware breakpoints and a small injected exception handler (requires P5SHighFps running) |
| `P5SScan.exe --trace=ADDR --exec` | Execute breakpoint; records `xmm1`, `xmm0` and 16 stack qwords per hit |

Typical workflow: scan → compare the rates at 60 and 120 FPS → trace the writer → disassemble → add a fix.
