# life

Conway's Game of Life on a wrapping torus, rendered in a native GUI window.

The board starts **empty** — **click anywhere to drop a random shape** (still-lifes,
oscillators, spaceships: block, blinker, glider, toad, beacon, beehive, LWSS) at that
cell — and runs **uncapped** (as fast as it can draw, no frame-rate limit). It fits
itself to the window and tracks live resizes, so the torus always fills the frame. A
big block-font status line below the board reports generation, FPS, live cell count,
frame spikes, and memory.

The board is a packed **bitboard** — one arbitrary-precision integer per row, bit `x`
= cell alive. A whole generation is one bit-plane neighbour sum per row over the three
torus-wrapped rows, so the step cost is **independent of how many cells are live** (a
flat ~10ms on a 250×140 board whether 300 or 3000 cells are lit). Rendering enumerates
live cells with `bit-positions`, so it's O(live), not O(area). This needs Brood's
arbitrary-precision integers + unrestricted bit-shifts (a row is a w-bit number shifted
by w−1 to wrap the torus) and the `bit-positions` builtin.

Written in Brood (`.blsp`), a small immutable Lisp.

## Running

```sh
nest run            # open the window and animate until q / Esc / window close
nest run --for 2s   # run for a bounded time, then exit cleanly (good for CI)
nest test           # run the test suite
nest format         # format the source
```

`nest run` enters `life/run-life` (set as `:main` in `project.blsp`).

## Layout

- `src/bitboard.blsp` — the packed-bitboard board: `make`/`bset`/`place`/`step`/
  `cells`/`live-count`. The bit-plane Game-of-Life step is here (`step`).
- `src/life.blsp` — the demo: the random seeder, click-to-add-a-shape interaction,
  the block-font status renderer, and the three-actor frame loop.
- `src/shapes.blsp` — still-life / oscillator / spaceship pattern geometry
  (`*shapes*`).
- `src/guns.blsp` — Gosper glider-gun geometry and its reflections
  (`*shooters*`); a gun keeps re-energising a settling board.
- `src/perflog.blsp` — a tiny process logger that writes a perf line to
  `life.log`.
- `tests/life_test.blsp` — exercises the simulation core (blinker, glider,
  torus wrapping, deterministic seeding, the guns) and the frame loop's quit
  predicate.
- `docs/game-of-life-notes.md` — the running log of Brood flaws/rough edges this
  demo surfaces (the app's real purpose).
- `docs/brood-for-claude.md` — Brood language reference.

## Design notes

- The board is a packed **bitboard** (`bitboard` module): one arbitrary-precision
  integer per row (bit `x` = cell `(x,y)`). `step` is a bit-plane full-adder neighbour
  sum per row over the three torus-wrapped rows — O(height) big-int ops, independent of
  population (and an all-zero band is skipped). `render` enumerates live cells with the
  `bit-positions` builtin (O(live)) and emits one draw op each over a leading `clear`.
  Relies on the kernel's bignums + unrestricted shifts + `bit-positions`.
- The program is **three processes** (ADR-058): a **SIM** owns the model + clock
  and pushes each board to the renderer; a **STATS** actor formats the status
  line and writes the perf log off the hot path; the **RENDERER** (the root
  process) owns the window and is a thin compositor.
- The SIM steps the board **serially** — `step` is the bulk of the CPU, and on
  these board sizes a serial recompute beats the copy-on-send overhead of fanning
  it across processes.
- There is **no frame-rate cap** (go ham): the SIM steps as fast as it can, but it
  still waits for the renderer's `[:drawn]` ack each frame, bounding the mailbox so it
  never runs more than one frame ahead.
- The renderer **only ever acts on a received message**, so GUI input (including a
  quit signal and **mouse clicks**) is drained no matter how long a frame takes — the
  window stays responsive even when a frame runs over budget.
- **Interaction:** the board starts empty; a left-click is forwarded by the renderer
  to the SIM as `[:click x y]`, which drops a random shape at that cell into the next
  generation. (The old adaptive auto-injection schedule was removed — you drive it.)
