# life

Conway's Game of Life on a wrapping torus, rendered in a native GUI window.

The board is sown with random known patterns — still-lifes, oscillators, and
spaceships (block, blinker, glider, toad, beacon, beehive, LWSS) plus Gosper
glider guns — and animated at a target 30 FPS. It fits itself to the window at
startup and tracks live resizes, so the torus always fills the frame. A big
block-font status line below the board reports generation, FPS, live cell count,
frame spikes, and memory.

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

- `src/life.blsp` — the simulation core (the Life rules and seeder), the
  block-font status renderer, and the three-actor frame loop.
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

- The board is a **sparse map** keyed by live `[x y]` cells, not a full grid —
  `render` emits one draw op per live cell and leans on a leading `clear`.
- The program is **three processes** (ADR-058): a **SIM** owns the model + clock
  and pushes each board to the renderer; a **STATS** actor formats the status
  line and writes the perf log off the hot path; the **RENDERER** (the root
  process) owns the window and is a thin compositor.
- The SIM steps the board **serially** — `step` is the bulk of the CPU, and on
  these board sizes a serial recompute beats the copy-on-send overhead of fanning
  it across processes.
- Frame pacing is a **per-frame deadline** measured from each frame's start, so a
  slow/GC frame is never chased by a catch-up speed burst. The SIM also waits for
  the renderer's `[:drawn]` ack each frame, bounding the mailbox so it never runs
  more than one frame ahead.
- The renderer **only ever acts on a received message**, so GUI input (including a
  quit signal) is drained no matter how long a frame takes — the window stays
  responsive even when a frame runs over budget.
- Patterns are sown on an **adaptive schedule**: when a board goes constant-ish it
  injects sooner; a glider gun landing on it trends the population back up.
