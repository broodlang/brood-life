# life — .NET port

A faithful .NET port of the Brood `life` demo, kept here so the two can be compared
side by side. Same program: Conway's Game of Life on a wrapping torus, animated in a
native GUI window, structured as a **SIM** process and a thin **RENDERER**.

```sh
cd Life
dotnet run                 # open the window, animate until q / Esc / window close
dotnet run --for 2s        # run bounded, then exit cleanly (the CI / headless path)
dotnet run -- --selftest   # exercise the pure bitboard core (no window)
```

Window: **Raylib-cs** (raylib via GLFW/OpenGL — an immediate-mode "blit rectangles each
frame" surface, the closest analog to the Brood renderer's job).

## What's faithful, and what isn't

The point was to mirror the **architecture**, not to clone every UI knob.

Kept faithful:

- **The bit-plane bitboard step.** `BitBoard.cs` is a line-for-line port of
  `src/bitboard.blsp`: the whole grid is one `System.Numerics.BigInteger`, bit `y*w+x` =
  cell `(x,y)`, and `Step()` is the same full-adder neighbour sum over the eight
  torus-shifted copies — a fixed handful of big-int ops, independent of population.
  `--selftest` checks blinker/block/glider/torus against the same cases as the Brood tests.
- **The SIM / RENDERER split (ADR-058).** The SIM owns the model, steps it, recolours it,
  **builds the render ops**, and formats the status line; the RENDERER just **blits** the
  ops it's handed and forwards input.
- **The self-pacing timer (ADR-101).** The SIM paces *itself* to a frame-rate cap by
  parking on a per-frame timeout that **any input preempts**, and it waits for the
  renderer's `Drawn` ack so it never runs more than one frame ahead.

Trimmed (would be mechanical to add): the ~110-pattern rotation/reflection catalog (kept
6 patterns + the Gosper gun), the block-font status renderer, frame-spike/memory stats,
and the held-button auto-repeat (drag already draws freehand).

## The architecture mapping

This is the interesting part — how Brood's process/actor model translates to .NET:

| Brood (`life`) | .NET (this port) |
|---|---|
| Two **processes** (green processes / actors) | Two **threads**: main = RENDERER, `Task.Run` = SIM |
| Message passing between processes | `System.Threading.Channels` (`toSim`, `toRenderer`) |
| SIM `receive` parked on `(after period)`, input preempts | `WaitToReadAsync(token)` cancelled by a per-frame delay CTS |
| Renderer `[:drawn]` ack bounds the mailbox | a `Drawn` message gates the next `Emit` |
| Render op = a draw instruction the renderer blits | `RenderOp(x, y, r, g, b)` struct |
| Arbitrary-precision integer grid + unrestricted shifts | `System.Numerics.BigInteger` |
| `bit-positions` (enumerate live cells, O(live)) | `BitBoard.Cells()` scanning set bits |
| Immutable board record, `assoc` returns a new one | `readonly struct BitBoard`, each op returns a new one |

## Where the two genuinely differ

- **The split is a real perf win here, for a different reason.** Brood splits because op
  building on the root process is ~2× slower than on a spawned one. In .NET both threads
  are equally fast native code; the split instead buys you *parallelism* — the SIM computes
  generation N+1 while the renderer blits N — because there's no GIL. (Brood's per-frame
  work is serial on the SIM by design; tried fanning it out, reverted — see its README.)
- **`BigInteger` is immutable and allocates per op**, like Brood's bignums, so the step
  has the same "handful of allocations per generation" character — but .NET's is a managed
  heap with a GC, where Brood's is the kernel's bignum arena. For a 250×140 board the step
  is still well under a millisecond.
- **Timing primitive.** Brood's `(after)` is a first-class scheduler timeout; .NET fakes
  the same shape with a linked `CancellationTokenSource.CancelAfter` racing a channel read.
  Same semantics (timeout vs. preempting message), different machinery.

## Files

- `BitBoard.cs` — the packed-bitboard core (port of `src/bitboard.blsp`).
- `Shapes.cs` — a small pattern catalog + the Gosper gun (port of `shapes`/`guns`).
- `Messages.cs` — the SIM↔RENDERER message protocol and the `RenderOp` type.
- `Sim.cs` — the SIM: model, step, recolour, op-building, self-pacing timer, ack wait.
- `Program.cs` — the RENDERER (root): window, input, blit, plus the entry point.
- `SelfTest.cs` — the no-window core check (port of `tests/life_test.blsp`).
