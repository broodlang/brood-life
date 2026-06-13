# life

Conway's Game of Life on a wrapping torus, rendered in a native GUI window.

The board starts **empty** — **left-click/drag to drop random shapes** (still-lifes,
oscillators, spaceships and methuselahs, in every rotation: a ~110-pattern catalog) and
**right-click/drag to drop glider guns**. Holding a button auto-repeats after a short
delay, then throttled. Controls:

- **scroll wheel** — zoom the board (the footer keeps a fixed height; only the board scales)
- **status-bar buttons** — click **`[spawn …]`** to toggle auto-spawn on/off; **drag**
  **`[fps …]`** left/right to scrub the frame-rate cap
- **`-` / `=`** — lower / raise the frame-rate cap (keyboard equivalent of the fps button)
- **`[` / `]`** — auto-spawn a random pattern rarer / more often (`[` past the max = never)
- **`q` / Esc / window-X** — quit

It starts **uncapped** (as fast as it can draw — it rips from the first frame); the fps
button/keys retune it all the way down to a gentle **1 fps** when you want to watch the
patterns evolve — drawing and mouse feedback stay instant regardless of the cap.
It also starts **auto-spawning** a random pattern every **30 s**, so an empty board comes
to life on its own; the `[spawn …]` button / `[`/`]` keys retune or disable that.
It fits itself to the window and tracks live resizes, so the torus always fills the frame.
A big block-font status line below the board leads with the two buttons, then reports
generation, measured FPS, live cell count, frame spikes, and memory.

The board is a packed **bitboard** — the *whole grid* in one arbitrary-precision integer,
bit `y*w + x` = cell `(x,y)` alive. A whole generation is one bit-plane neighbour sum (a
full-adder over the eight torus-shifted copies of the grid), a fixed handful of big-int
ops, so the step cost is **independent of how many cells are live** (well under a
millisecond on a 250×140 board whether 300 or 3000 cells are lit). Rendering enumerates
live cells with `bit-positions`, so it's O(live), not O(area). This needs Brood's
arbitrary-precision integers + unrestricted bit-shifts (each row's edge bits shift across
to wrap the torus) and the `bit-positions` builtin.

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

- The board is a packed **bitboard** (`bitboard` module): the whole grid in one
  arbitrary-precision integer (bit `y*w + x` = cell `(x,y)`). `step` is a bit-plane
  full-adder neighbour sum over the eight torus-shifted copies of the grid — a fixed
  handful of big-int ops, independent of population. `render` enumerates live cells with
  the `bit-positions` builtin (O(live)) and emits one draw op each over a leading `clear`.
  Relies on the kernel's bignums + unrestricted shifts + `bit-positions`.
- The program is **two processes** (ADR-058): a **SIM** owns the model, steps it,
  formats the status line, writes the perf log, and pushes each board to the
  renderer; the **RENDERER** (the root process) owns the window and is a thin
  compositor.
- The SIM steps the board **serially** (`step` is a fixed handful of big-int ops,
  ~0.2 ms — population-independent and not worth parallelising). The per-frame hot
  spot is instead the **spawn-colour layer** (`recolor`): a per-birth blend of the
  cell's live neighbours. Births are independent and write disjoint keys, so on a
  **high-churn, moderate-size** board the SIM fans them across a small pool of
  persistent worker processes (`recolor-par`, `*recolor-workers*`) for a ~2.7–3.2×
  win. The win is bounded: each worker is sent the whole colour map every frame, so
  past `*recolor-par-max-cells*` live cells that O(population) copy outweighs the
  saving and `recolor-par` falls back to serial. (A copy-free version — workers that
  own a board band and trade only halo rows — would win unconditionally, but is a
  larger re-architecture and isn't built.)
- The SIM **paces ITSELF to a frame-rate cap** (`*target-fps*`, default uncapped, live-adjustable
  with `-`/`=`): its `receive` parks on an `(after period)` timeout — a self-resetting timer
  (the same mechanism as ADR-101's `ui-run` timers) whose `period` subtracts the work already
  spent, so the cap is a true target. Any input message **preempts** the timeout, so a paste
  is applied + drawn at once, never blocked behind the cap (the old separate CLOCK process is
  gone). It also waits for the renderer's `[:drawn]` ack each frame, bounding the mailbox so it
  never runs more than one frame ahead.
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
  Auto-injection is on by default (`*inject-secs*`, **30 s**; `[`/`]` retune it or turn it
  off) — every `*inject-secs*` seconds the SIM sows a random pattern.
