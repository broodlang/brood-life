# Brood GC slab-OOB crash — reproduces on HEAD via the Life demo

**Reporter:** brood-life (Conway demo) · **Date:** 2026-06-15
**Kernel build:** `nest 0.1.0`, installed binary `~/.local/bin/nest` (built 2026-06-15 00:24)
**Kernel HEAD at report time:** `2f350df` (`lisp: native string-split, table (ETS), telemetry, proc cwd/env, treesit + fuzzy speedups`)
**Component:** `crates/lisp/src/core/heap.rs` — moving GC copy/flush phase (`flush_oob`, ~L5590)

**STATUS: RESOLVED** — Root cause was **KI-4** (bitset stored as non-UTF-8 `Value::Str`).
Fixed in `95f52fd` (2026-06-15): bitsets are now a distinct `Value::Bitset` kind with a
byte-clean slab; `promote_in` no longer routes through the UTF-8 string accessor.
Regression tests: `gc::ki4_bitset_survives_spawn_promote` and
`gc::ki4_bitset_survives_gc_churn_in_spawned_process` — both clean under
`BROOD_GC_STRESS=1 BROOD_GC_VERIFY=1`. See `docs/known-issues.md` KI-4 for the full
post-mortem and verification record.

---

## TL;DR

Running the Life demo **uncapped** crashes the Brood runtime roughly **1 in 3–4 runs**
(6-second runs). It is a **memory-safety fault in the per-process garbage collector**: a
`Value` handle reachable from the GC roots indexes its source slab out of bounds during a
copy collection. Depending on timing it surfaces three ways:

1. **GC tripwire panic** — `flush_oob`: *"… handle indexes the source slab out of bounds …
   missed rooting / use-after-GC / foreign handle"* (writes `.brood_crash_dump`).
2. **Raw `SIGSEGV`** (most common on the current release build) — the OOB read faults before
   the tripwire runs. *No* `.brood_crash_dump` (the panic hook doesn't catch SIGSEGV).
3. **glibc `SIGABRT` — "corrupted double-linked list"** — the OOB *write* corrupts the
   allocator's free-list metadata.

All three are the same underlying bug.

**This matches the `KI-1` / `KI-3` GC slab-OOB signature that `docs/known-issues.md` marks
"fixed" — but it still reproduces on HEAD, and in a SINGLE process** (the SIM green process),
not the multi-worker `pstep` race that KI-1 was about. So the rooting fault is exercised by
ordinary high-churn single-process allocation, independent of work-stealing / cross-thread
migration.

---

## Severity

**High.** Silent memory corruption + hard crash in the runtime, reachable from pure Brood
code with no `unsafe` on the user's side. The "corrupted double-linked list" variant means
the heap is genuinely being scribbled on, so the silent-corruption ("wrong value read back")
failure mode from the KI-1/KI-3 history is also possible here.

---

## Reproduction

```sh
cd brood-life
# Uncapped Game of Life, fair benchmark (256×256, centred 100×100 block, refc-shared bitset,
# no colour layer). One SIM green process steps + builds ops as fast as it can.
for i in $(seq 1 12); do ./run brood --fair --for 6s >/dev/null 2>err.log; echo "iter $i: exit $?"; done
```

- ~1 in 3–4 iterations exits non-zero: `139` (SIGSEGV), `134` (SIGABRT), or a `flush_oob`
  panic. Clean runs reach ~gen 15,000–17,800 in 6 s.
- `BROOD_GC_VERIFY=1` does **not** make it safe — it still SIGSEGVs/SIGABRTs (the raw fault
  often wins the race before the verifier's check). It is the documented way to get the
  **root→cell path**, but in this release build the segfault frequently pre-empts it.
- Pure uncoloured path: the colour layer is empty in `--fair` (`colors = {}`), so colours are
  **not** the trigger. The churn is the SIM's per-frame allocation: the state `Map` re-`assoc`'d
  every generation, the `[:cells]` ops vector, the status string, and a fresh bitset blob per
  `bitset-life-step`.

The Life loop is a *good* reproducer because it allocates hard in one process and runs many
minor GCs per second (uncapped).

---

## The crash signature

`crates/lisp/src/core/heap.rs` `flush_oob` (~L5590), reached from the GC copy phase:

```
GC flush: <KIND> handle indexes the source slab out of bounds —
  region=0 age=<young|old> epoch=<N> index=<I> slab_len=<L>,
  collecting <nursery (minor)|old-gen (major)>.
A handle reachable from the GC roots is not a live this-pass object
  (missed rooting / use-after-GC / foreign handle).
Re-run with BROOD_GC_VERIFY=1 for the root→cell path.
```

The in-code note (heap.rs ~L5578) says it plainly: `copies()` admits a handle **by region +
generation-age but NOT by slab bound**, so a stale / foreign / mis-tagged handle in the root
set indexes the source slab out of bounds here.

### It is not type-specific or generation-specific

Captured `.brood_crash_dump` records (this machine) show the OOB across **four handle kinds**
and **both** collections:

| handle kind | age   | collection      | example (epoch / index / slab_len) |
|-------------|-------|-----------------|-------------------------------------|
| `map`       | young | nursery (minor) | 367 / 295 / 157 ; 1075 / 1753 / 69 ; 14880 / 3405 / 51 |
| `vector`    | young | nursery (minor) | 348 / 2471 / 298 ; 948 / 167 / 13 |
| `bigint`    | young | nursery (minor) | 365 / 1 / 0 |
| `env`       | old   | old-gen (major) | 16 / 199 / 23 ; 20 / 1133 / 23 |

`slab_len=0` with `index=1` (the bigint case) is a particularly clean tell: the handle points
into a slab that holds nothing this pass. This is a **general rooting fault**, not one bad
call site — any live handle held across a GC safepoint without being rooted will land here.

---

## Artifacts

- **`brood-life/.brood_crash_dump`** — 8 panic records, 2026-05-31 → 2026-06-03 (older
  sessions; the panic hook only fires for the *panic* manifestation). Backtraces are
  `<unknown>` (stripped release). The same dump in the **kernel** repo
  (`brood/.brood_crash_dump`, a debug `nest test` build) has symbolized frames showing the
  `eval::compile::exec_node` / `vm_apply` VM spine.
- **Today's crashes (2026-06-15)** were SIGSEGV/SIGABRT → no panic record; the core went to
  apport (`/var/crash/…timeout….crash`, attributed to the `timeout` wrapper around the run).
- Reproduced today on HEAD `2f350df`: SIGABRT *"corrupted double-linked list"* (iter 4 of a
  verify loop) and SIGSEGV ×3 in a following 8-run loop.

---

## Why this is a kernel bug, not the demo's

- The crash is in the **GC copy phase of the SIM process's own heap**. Pure `.blsp` code
  cannot produce a slab-OOB / heap-corruption by itself; this is runtime memory-unsafety.
- It is single-process: the SIM does all the allocation; the renderer is a separate process
  with its own heap. So it is **not** the cross-thread work-stealing race KI-1 fixed
  (per-worker pinned queues) — it reproduces with the SIM pinned to one worker.
- "Uncapped" is not new behaviour from any recent demo change — the demo always defaulted to
  uncapped; removing the fps cap only removed the *option* to throttle.

---

## Relationship to known issues

`docs/known-issues.md`:

- **KI-1** (multi-thread scheduler race / use-after-GC) — "fixed 2026-05-29"; its 2026-05-31
  re-report was the **same `flush_*` slab-OOB signature** under foobar's parallel `pstep`
  (also Game of Life), marked *"Confirmed already fixed — does not reproduce on HEAD"* (that
  capture was from a pinned **pre-fix** `nest mcp`).
- **KI-3** (RUNTIME compactor strands live VM/tree-walker constants) — "fixed 2026-06-01".

**This report contradicts the "fixed" status on current HEAD**: the identical signature
reproduces reliably, single-process, with a fresh `2f350df` build. Either the fix is
incomplete for the minor/major *local-heap* collector (KI-1/KI-3 centred on the scheduler
race and the RUNTIME compactor), or a regression landed since (a candidate: the `table`/
telemetry/proc work in `2f350df` — worth a bisect, and an audit of any new builtin that holds
a `Value` across an allocation/safepoint without rooting it).

---

## Suggested next steps for the team

1. **Get the root→cell path.** Run the repro under `BROOD_GC_VERIFY=1` until it hits the
   *panic* (not the raw segfault) and capture the verifier's root→cell trace; or run a
   **debug-assertions** build so the verifier check beats the segfault and frames symbolize.
2. **Bisect HEAD vs the KI-1/KI-3 fix commits** (`f90f0de`, `2abf05e`, the KI-3 fix
   2026-06-01) using this single-process repro — it's far simpler than the parallel `pstep`
   one and doesn't need `BROOD_GC_STRESS`.
3. **Audit `copies()` / `flush_*`**: gating a copy by region + generation-age but not slab
   bound is what lets a stale handle through. A handle that's *admitted* but OOB is a missed
   root somewhere upstream — the four-kind spread points at a generic safepoint, not one
   builtin.
4. **Suspect the `2f350df` additions** (table/ETS, telemetry, proc cwd/env) for an unrooted
   `Value` held across an allocation.

Repro harness lives in this repo (`./run brood --fair --for <ms>`); ping me and I can capture
a debug-build trace or bisect.
