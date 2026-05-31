# foobar

Conway's Game of Life on a wrapping torus, rendered in a native GUI window.

The board is sown with random known patterns (block, blinker, glider, toad,
beacon, beehive, LWSS) and animated at ~22 FPS. It fits itself to the window
at startup and tracks live resizes, so the torus always fills the frame. A
big block-font status line below the board reports generation, FPS, live cell
count, frame spikes, memory, and GC collections.

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

- `src/life.blsp` — the whole program: the Life rules, the random seeder, the
  block-font status renderer, and the frame loop.
- `tests/life_test.blsp` — exercises the simulation core (blinker, glider,
  torus wrapping, deterministic seeding) and the frame loop's input handling.
- `docs/game-of-life-notes.md` — design notes.
- `docs/brood-for-claude.md` — Brood language reference.

## Design notes

- The board is a **sparse map** keyed by live `[x y]` cells, not a full grid —
  `render` emits one draw op per live cell and leans on a leading `clear`.
- `step` (the heavy generation compute) runs **off the render thread** in a
  `sim-worker` process, so a slow generation doesn't stretch the frame.
- Frame pacing is a **per-frame deadline** measured from each frame's start, so
  a slow/GC frame is never chased by a catch-up speed burst.
- `wait-frame` keeps the window responsive to input even when a frame runs over
  its time budget — it always drains the mailbox before emitting the next board.
