# life

Conway's Game of Life on a wrapping torus, rendered in a native GUI window.

The board starts **empty** — **left-click/drag to drop random shapes** (still-lifes,
oscillators, spaceships and methuselahs, in every rotation: a ~110-pattern catalog) and
**right-click/drag to drop glider guns**. Holding a button auto-repeats after a short
delay, then throttled. Controls:

- **scroll wheel** — zoom the board (the footer keeps a fixed height; only the board scales)
- **`-` / `=`** — lower / raise the frame-rate cap
- **`[` / `]`** — auto-spawn a random pattern rarer / more often (`[` past the max = never; the default is off)
- **`q` / Esc / window-X** — quit

The sim **paces to a frame-rate cap** (default 30 fps, adjustable). It fits itself to
the window and tracks live resizes, so the torus always fills the frame. A big block-font
status line below the board reports generation, FPS (`measured/cap`), live cell count,
frame spikes, the auto-spawn interval, and memory.

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
- `src/life.blsp` — the demo: the random seeder, the click/drag/scroll interaction
  (shapes left, guns right, scroll to zoom), the block-font status renderer, and the
  three-actor frame loop.
- `src/shapes.blsp` — still-life / oscillator / spaceship / methuselah geometry, fanned
  by rotation + reflection (Life is isotropic) into the ~110-pattern `*shapes*` catalog.
- `src/guns.blsp` — Gosper glider-gun geometry, fanned into all eight orientations
  (`*shooters*`); dropped on right-click and to re-energise a settling board.
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
- The SIM **paces to a frame-rate cap** (`*target-fps*`, default 30, live-adjustable with
  `-`/`=`): after drawing it sleeps out the rest of the frame budget. It also waits for the
  renderer's `[:drawn]` ack each frame, bounding the mailbox so it never runs more than one
  frame ahead.
- The renderer **only ever acts on a received message**, so GUI input (including a
  quit signal and **mouse clicks**) is drained no matter how long a frame takes — the
  window stays responsive even when a frame runs over budget.
- **Interaction:** the board starts empty; the renderer forwards each input to the SIM —
  `[:press/:drag :shape|:gun col row]` (left draws shapes, right draws guns), `[:release]`,
  scroll-wheel zoom, and the `-`/`=`/`[`/`]` knobs — and the SIM folds the drawn patterns
  into the next generation. A held button auto-repeats after `*hold-delay*`, then every
  `*spawn-every*` gens; a drag draws freehand (one per cell) without re-arming. Zoom resizes
  the board font (the footer is re-scaled to a fixed pixel height, so only the board grows);
  cells that fall off the shrunk board are dropped (`bitboard/refit` clips, no wrap-back).
  Auto-injection is back as an **opt-in** knob (`[`/`]` set the interval, default off) —
  every `*inject-secs*` seconds the SIM sows a random pattern.
