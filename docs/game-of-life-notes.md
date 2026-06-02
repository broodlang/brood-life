# Notes — Brood flaws found via the Life demo

This project is a **test-bed for Brood**. The Game of Life is just the vehicle: a
small, real program that exercises the language, the standard library, the GUI
display protocol, and the green-process runtime hard enough to surface flaws,
rough edges, and missing pieces.

This file is where we log what we find. When something in Brood is broken,
missing, awkward, or surprising while working here, record it below — newest
first. A hard runtime crash gets its own write-up; a smaller rough edge gets a
bullet.

## Findings

### RESOLVED 2026-06-02 — most of the below is now fixed

- **Arbitrary-precision integers + unrestricted bit-shifts: DONE.** The kernel now
  auto-promotes i64→bignum on overflow (demoting back when a result fits), and the
  `bit-shift-left/right` `[0,64)` cap is gone (large shifts promote). Plus a new
  `bit-positions` builtin (set-bit indices, O(popcount)). See brood
  `8d63d66 feat: arbitrary-precision integers + bit-positions`. The "ints are i64 /
  shift capped" findings below are closed.
- **The Life demo is now a wide-row bignum bitboard** (one w-bit integer per row;
  a generation is one bit-plane neighbour sum per row, O(h), flat in population)
  with an O(live) `bit-positions` renderer. On a 250×140 window: ~3ms step + ~22ms
  render ⇒ **~40fps at 3000 live cells** (was 4fps with the sparse map). The
  "interpreter floor caps pure-Brood" umbrella item is effectively addressed for
  this workload by moving the heavy lifting into bignum primitives.
- **The GC `flush_oob` crash was the stale-binary recurrence** (the `nest mcp`
  server outliving a rebuild), not a live collector bug — does not reproduce on a
  fresh build under the verifier. See `gc-flush-panic-mcp-2026-05-31.md` (and the
  in-band staleness warning now surfaced by `nest mcp`). **Transients** remain the
  one genuinely-open kernel item (hardest; collides with the moving GC).

### CRASH — GC "slab out of bounds" under sustained allocation

Reproducible hard panic in the runtime, surfaced while benchmarking the sim hot
path via `nest mcp`'s `bench`:

```
(bench "(reduce (fn (a x) (+ a x)) 0 (range 100000))" :iterations 10)
=> panic: GC flush: env handle indexes the source slab out of bounds —
   region=0 age=old epoch=16 index=199 slab_len=23, collecting old-gen (major).
   A handle reachable from the GC roots is not a live this-pass object
   (missed rooting / use-after-GC / foreign handle).
```

- A **single** `(reduce + 0 (range 100000))` evaluates fine (`4999950000`).
  `:iterations 3` is fine. The crash needs **sustained** churn — re-running the
  same large allocation ~10× back-to-back trips it, on both the minor (young)
  and major (old-gen) collector.
- The message points at a **missed-rooting / use-after-GC bug** in the
  collector: a handle reachable from the roots isn't being treated as a live
  object across a collection. Smells like `range`/`reduce` leaves an env or
  vector handle unrooted across a GC boundary.
- Next step for whoever picks this up: re-run with `BROOD_GC_VERIFY=1` for the
  root→cell path (the panic suggests it). Likely in the `range` thunk or
  `reduce`'s accumulator rooting.

### Missing — no arbitrary-precision integers; ints are signed i64

`(* (powr 100 1) ...)` (building 2^100 by repeated `* 2`) panics with
`E0041 %mul: integer overflow`, and `(bit-shift-left 1 63)` returns
`-9223372036854775808`. So:

- Integers are **fixed signed 64-bit**, not bignums. They overflow (hard error
  on `*`, silent two's-complement on shift) past 2^63.
- This **blocks the right algorithm for Life**: the classic bit-plane bitboard
  represents each board row as one integer bitmask and computes a whole
  generation in O(height) bitwise ops (8 neighbour masks summed via full-adders
  per row). At width 200 that needs 200-bit integers — impossible here. A
  64-bit-chunked version is possible but reintroduces per-word cross-boundary
  wrap handling and the sign-bit hazard.
- Ask: **arbitrary-precision integers**, or a dedicated **fixed-width bitset
  type** with the bitwise ops.

### Missing — `bit-shift-left/right` capped at shift < 64

`(bit-shift-left 1 200)` => `E0099 bit-shift-left: shift amount 200 out of
range [0, 64)`. Even with bignums this cap would block shifting a wide row by
its width to wrap the torus. Ask: allow arbitrary shift amounts (paired with
bignums or a bitset type).

### Missing — no transient / mutable collections; persistent-map churn dominates `step`

Measured cost of one `step` on a ~3500-live-cell board (200×80 torus), via
`bench`:

| stage                                    | per-iter |
|------------------------------------------|----------|
| iterate `(keys live)` + the 4 `mod`s     | ~40 ms   |
| + tally 8 neighbours into the count map  | ~198 ms  |
| + `filter` survivors and `into {}` rebuild | ~275 ms |

So **~85% of `step` is persistent-map allocation** — the 8 `get`+`assoc` per
live cell (≈28k map writes) plus the final `into {}`. Swapping integer keys for
`[x y]` vector keys changed nothing (275 ms either way), confirming it's the
HAMT churn, not key hashing.

- A **transient/mutable map** (build the count map and the result map in place,
  freeze at the end) is the standard fix and would cut most of that 235 ms.
- Ask: `transient`/`persistent!`/`assoc!` (Clojure-style) or any mutable-map
  builder.

### Rough edge — interpreter throughput floor (~1 µs / simple op)

`(reduce + 0 (range 100000))` is ~125 ms ⇒ roughly **1 µs per trivial loop
iteration**, ~1M simple ops/sec. The frame budget at 30 fps is 33 ms ≈ **33k
interpreter ops**. The sparse-map `step` blows that at a few thousand live cells
regardless of how it's tuned, so **no pure-Brood rewrite reaches 3000 cells @
30 fps** — the win has to come from the kernel (bignum/bitset bitboard, mutable
collections, or a compiled/bytecode hot path / native neighbour-count
primitive). Worth tracking as the umbrella perf item.
