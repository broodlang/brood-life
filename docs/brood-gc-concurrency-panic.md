# Brood panic: GC pair-copy indexes a slab out of bounds under concurrent spawn/message load

**Component:** `crates/lisp/src/core/heap.rs` — generational GC copy/flush phase
**Severity:** memory-unsafety class (hard panic **and** silent wrong results)
**Repro:** intermittent / timing-dependent

---

## Summary

Running an embarrassingly-parallel workload — a coordinator process `spawn`s N
worker processes that each allocate heavily and `send` a result back, which the
coordinator `receive`s and merges — intermittently **panics** inside the
generational GC's copy phase with a slice out-of-bounds, and (separately, on other
runs) returns a **silently wrong result**. Both point to a dangling / mis-tagged
heap handle reachable from the GC roots: during young-gen compaction the collector
classifies a handle as a live LOCAL object and follows its index into the source
slab, but the index is past the slab's length.

This surfaced from the foobar Game-of-Life demo's parallel `step` (`pstep`): a
sim-worker fans one generation across ~8 row-band worker processes.

## The panic (from `.brood_crash_dump`, verbatim)

```
when:    1780221280527 ms since epoch
thread:  nest-main
panic:   panicked at crates/lisp/src/core/heap.rs:4472:29:
index out of bounds: the len is 3007 but the index is 7187
backtrace:
   0..41: <unknown>            # release build — frames unsymbolicated
  42: start_thread
  43: __GI___clone3
```

- Thread **`nest-main`** — the root process (the one running the `pstep`
  coordinator), not a spawned worker thread.
- `index 7187` ≈ 2.4× `len 3007`.

## Where

The attributed line `heap.rs:4472` is inside the `mint_fn!` macro / `FlushForward`
cluster (the GC copy machinery, ~4456–4480). Release-build inlining attributes the
panic to that inlined span; the **actual out-of-bounds index** is the source-slab
access in the pair-copy walk:

```rust
// crates/lisp/src/core/heap.rs:4532  (inside flush_pair)
let (car, cdr) = old.pairs[p.index()];   // p.index() == 7187, old.pairs.len() == 3007
```

The sibling copy routines (`flush_vector` / `flush_map` / `flush_string` /
`flush_rope` / `flush_closure`) index their own slabs the same way and are equally
exposed.

The guard that let the bad handle in:

```rust
// heap.rs:4451
fn copies(&self, region: u8, is_old: bool) -> bool {
    region == LOCAL && is_old == self.src_old
}
// flush_value (4481) / flush_pair (4527): `if fwd.copies(p.region(), p.is_old())`
```

So the offending Pair handle was tagged **LOCAL** and matched the generation being
collected (`is_old == src_old`) — i.e. it looked like a live, this-pass object — yet
its `index()` (7187) is beyond the live source slab (3007). It is a stale or
mis-tagged handle that survived into the root set.

## Failure modes

1. **Hard panic** at the index above (slab OOB during GC copy).
2. **Silent corruption** — in one run `(pstep board)` returned a result `≠ (step
   board)` with *no* panic, while other boards in the same batch were correct. A
   miscompacted/aliased handle produces a wrong-but-not-crashing object.

Both are consistent with one root cause: a handle reachable by the GC that does not
correspond to a live slot in the slab it is indexed into.

## How to reproduce

Coordinator spawns workers that allocate and message back, with a global rebound
underfoot (a writer racing the readers). Standalone repro (`/tmp/race_repro.blsp`):

```clojure
(def *spin* 0)
(defn work (n) (reduce (fn (a x) (assoc a x (mod (+ x *spin*) 7))) {} (range n)))
(defn fan (me k n)
  (do (dotimes (b k) (let (p me) (spawn (send p [:r (count (work n))]))))
      (reduce (fn (acc _) (receive ([:r c] (+ acc c)))) 0 (range k))))
(defn trial (me) (do (dotimes (i 400) (def *spin* i)) (fan me 16 4000)))
;; loop `trial` a few thousand times
```

The original trigger was foobar's `pstep`: build N halo'd sub-boards, `spawn` a
process per band that runs `step` (heavy small-map churn + reads of shared globals)
and sends its slice back; coordinator `receive`s and merges — while the eval also
rebinds a global (`(def *h* …)`) mid-flight.

## Reproducibility / build vintage

- **Intermittent** — dozens of clean runs between failures.
- The captured panic came from a **long-lived `nest mcp` server pinned to an older
  nest binary** (it still has the pre-`(gui-font! id spec)` 1-arg `gui-font!`).
- On the **freshly built nest** (`~/.local/bin/nest`, built same day) the standalone
  repro ran ~150 s of aggressive stress **without panicking**.

So this may be the KI-1/KI-2 scheduler race **already addressed on 2026-05-29** — but
given the slab-index site above and that it is timing-dependent, it should be
confirmed against a current build before closing.

## Ruled out

- **Symbol interner** (`core/value.rs:36`, `NAMES: boxcar::Vec<String>`): read via
  `.get(id).expect("interned symbol id")` — an OOB there panics with `"interned
  symbol id"`, not this message. `intern` holds the `IDS` mutex across the `NAMES`
  push, keeping the tables consistent.
- **Global table** (`core/heap.rs:487`, `globals: RwLock<SymbolMap<Value>>`):
  RwLock-guarded, and a map wouldn't produce a slice OOB.

The OOB is a raw `slab[idx]` in the GC copy path — a per-heap arena indexed by a
handle that should have been remapped/rooted.

## Suggested next steps

1. **Confirm on a current build**: restart `nest mcp` (so it isn't pinned to the old
   binary) and re-run the repro. If it no longer fires, this is the already-fixed
   race — add a regression test and close.
2. **If it still fires on HEAD**, instrument the copy path: replace the bare
   `old.pairs[p.index()]` (and siblings) with a checked access that, on OOB, logs the
   handle's `region` / `is_old` / `epoch` / `index` and the slab len — this names the
   bad handle's provenance directly.
3. Build **debug** + `RUST_BACKTRACE=full` for a symbolicated stack (the release
   backtrace was `<unknown>`), and/or run the repro under **ThreadSanitizer**
   (`-Zsanitizer=thread`) to catch the racing read/write pair.
4. Audit how handles cross the `spawn` / `send`(deep-copy) / `receive` boundary and
   whether a handle from a foreign epoch/region can alias `LOCAL` + current
   generation so that `copies()` returns true for it.
5. **Regression test**: spawn-N-then-collect where each worker steps a shared board
   and a global is rebound concurrently; assert the parallel result equals the serial
   one over many iterations.

## Notes

- foobar's `pstep` row-band decomposition was verified **exactly equal** to the
  serial `step` when run sequentially (no spawn) across sizes, the torus seam, odd
  grid heights, and worker counts — so the algorithm is correct; the fault is purely
  in the concurrent runtime path above.
