# BeetleMcp guide for LLMs

A concise field manual for navigating a Beetle `.beetle` exception trace through this MCP.

## What you're looking at

A `.beetle` file is a recording of **every managed (.NET) exception** thrown by **every process** on a Windows machine during a recording window, captured via kernel + CLR ETW providers. For each exception you get:

- the **process** that threw it (full process tree, image file name, command line, parent),
- the **timestamp** (absolute UTC),
- the **exception type and message**,
- the **managed stack trace** (resolved against the JIT'd methods + module symbols recorded in the same file).

You also get every process's **loaded modules** (managed assemblies) and **native images** (DLLs/EXEs in the address space).

## Identifiers

PIDs are **reused** within a single recording (Windows recycles them quickly). Do not key off PID.

- `[pi]` is the **processIndex** — the position in `Session.Processes`. This is the canonical handle. Always pass `processIndex`.
- `[pi/ei]` identifies a single exception: process index `pi`, exception index `ei` within that process.
- The `get_process_tree` tool matches parents on `(parentPid, parentStartTimeRelativeMSec)` so reused PIDs don't cross-link. The same matching is used by `get_process_parent_chain` and `get_process_children`.
- Ids are scoped to one `.beetle` file's bytes; reload a different file → discard the ids.

## The core loop

0. **Narrow down first.** On any unfamiliar file, start by identifying the small set of processes that actually matter for the question being asked, and ignore the rest. A .beetle typically contains hundreds of processes — OS background tasks, telemetry, log collectors, post-mortem dump tools — and lumping them together with the workload makes histograms and timelines misleading. Use `list_processes` (sorted by exceptions or duration) to find the workload-bearing process(es), then pass `processIndices=[pi,...]` to every subsequent tool. Only widen the scope if narrowing produced nothing useful.
1. **`get_session_summary <path>`** — file size, recording window (UTC), eventsLost, processes, exceptions, distinct exception types. Always cheap.
2. **`list_processes <path> sortBy=exceptions`** (and/or `sortBy=duration`, or `notExitedCleanly=true`) — identify the interesting processes. Each line shows `durationMs` and `exitCode` (or `stillRunningAtSessionEnd`), so killed/timed-out processes stand out at a glance.
3. **`count_exceptions <path> processIndices=[pi,...]`** — the type histogram, scoped to the workload. groupBy='type' by default; switch to 'type+message' when many sites share a type.
4. **`query_exceptions <path> processIndices=[pi,...] ...filters...`** — sample interesting ones; copy `[pi/ei]` ids out.
5. **`get_exception <path> <pi/ei>`** — full stack trace.
6. Drill sideways: `get_process`, `get_process_tree`, `get_process_parent_chain`, `get_process_children`, `list_modules`, `list_native_images`.

Files are loaded on first use and cached. `load_beetle` / `reload_beetle` / `unload_beetle` are explicit controls.

## Filter reference (shared by query_exceptions / count_exceptions / bin_exceptions / exceptions_around_time)

| Filter | Meaning |
|--------|---------|
| `processIndices` / `excludeProcessIndices` | exact processIndex set (canonical — use this) |
| `processIds` / `excludeProcessIds` | exact PID set (note: PIDs are reused) |
| `processNameRegex` / `excludeProcessNameRegex` | regex against `ImageFileName` and `Path.GetFileName(FilePath)` |
| `commandLineRegex` | regex against `CommandLine` |
| `exceptionTypeRegex` / `excludeExceptionTypeRegex` | regex against `ExceptionType` (e.g. `^System\.OperationCanceledException$`) |
| `messageRegex` / `excludeMessageRegex` | regex against `ExceptionMessage` |
| `startTime` / `endTime` | absolute UTC bounds on exception timestamp |
| `aroundTime` + `windowMs` | center +/- windowMs (intersected with start/end if both supplied) |
| `aroundOffset` + `windowMs` | center expressed as offset from session start, e.g. `'30m'`, `'+1800s'`, `'5400000ms'`. Mutually exclusive with `aroundTime`. |

All regexes are case-insensitive. All filters AND together. `aroundOffset` accepts a leading `+` / `-` and one of `ms` / `s` / `m` / `h`; a bare number is milliseconds.

Tools vary in which subset of these they expose — `query_exceptions` and `bin_exceptions` are the most permissive; `list_processes` only has the process-side filters; `exceptions_around_time` is scoped to the around* form.

## Output projection (query_exceptions / exceptions_around_time)

Pass `fields="timestamp,type,id"` (or any comma-separated subset of `timestamp,process,type,message,id`; aliases `ts`, `time`, `proc`, `msg`) to drop columns from each line. Default is the full line. Use this when you only need timestamps for binning, or only `[pi/ei]` ids to feed into `get_exception` — it cuts response size 5–10× on large dumps.

## Counts vs samples

- `query_exceptions` is **capped + paged** (default 200 / max 5000). The header ends with `matched=N+, nextSkip=K` if the cap was hit; pass `K` as the next call's `skip` to continue.
- `count_exceptions` makes a **full pass with no cap**, sorts by frequency, and truncates the *output* (not the count). Pick a `groupBy` to control the histogram key. Use this when you need the truth.
- `bin_exceptions` makes a **full pass** and groups matching events into time buckets (default 1 minute). One row per bucket: `<bucketStartIso>\t<count>`. Use this when the question is about *when* things fired, not *what*.
- `diff_exceptions` builds full histograms on both files and produces three sections: ONLY IN LEFT, ONLY IN RIGHT, COMMON DELTA. Same `groupBy` knob.

Decision rule:
- "show me a few" → `query_exceptions`
- "what's the histogram?" → `count_exceptions` (groupBy='type' for triage; 'type+message' for finer signal)
- "how does activity change over time?" → `bin_exceptions` (binSize='1m' / '10s' / '1h' as appropriate)
- "what's different between these two runs?" → `diff_exceptions`

## Recipes

### Cold-start triage (do this first on any unfamiliar file)
1. `get_session_summary <path>` — note `eventsLost` (non-zero means the recording dropped some events under load — don't over-trust counts), session window, process count, exception count.
2. **Identify the workload.** `list_processes <path> sortBy=exceptions` — which processes throw the most? Cross-reference with `sortBy=duration` and `notExitedCleanly=true` if the question is about hangs/timeouts. The handful of processes at the top is almost always your real subject; the rest are OS / telemetry / log-collection noise to be excluded.
3. Scope every subsequent call. `count_exceptions <path> processIndices=[pi,...]` to see the type histogram for just the workload. The long tail of `OperationCanceledException` / `TaskCanceledException` is usually noise; user-defined exceptions are usually signal.
4. `query_exceptions <path> processIndices=[pi,...] exceptionTypeRegex=<theInterestingType> maxResults=20` — sample the actual events.
5. `get_exception <path> <pi/ei>` on a representative one for the stack trace.

### Diff a "good" run against a "bad" run
1. `diff_exceptions good.beetle bad.beetle` — start here. The `ONLY IN RIGHT` section is the smoking gun — exception types that appeared only in the bad run. Use `groupBy='type+message'` when types are too coarse.
2. For shared types where the count exploded, look at `COMMON DELTA` (sorted by absolute delta).
3. For each suspicious type, narrow on the bad file:
   `query_exceptions bad.beetle exceptionTypeRegex=^System\.IO\.IOException$ maxResults=20 includeStackTrace=true`
4. Apply the same filters to `good.beetle` to confirm the type really is absent there (or only present at trivial counts), to rule out it being a coincidental difference.
5. If the diff is still noisy, scope to the relevant process: add `processNameRegex=<name>` to both calls.

Tip: re-run the diff with `processNameRegex` set when one process accounts for most of the noise. The diff tool applies the same filter to both sides, so you stay apples-to-apples.

### Correlate with an external test/CI log
You have a test log line like: `[2026-04-27T13:42:18.123Z] FAIL TestFooBar`. You want to know what fired around that moment.

1. `exceptions_around_time <path> aroundTime=2026-04-27T13:42:18Z windowMs=10000` — every exception in the +/-10s window, ordered by timestamp.
2. Widen `windowMs` if nothing comes back; the test framework's reported timestamp may lag the actual failure point.
3. For the suspicious one(s): `get_exception <path> <pi/ei>`.
4. Optionally constrain by process: `exceptions_around_time <path> aroundTime=... processNameRegex=testhost`.

If you're thinking in offsets ("what fired around minute 18 of the session?") use `aroundOffset` instead: `exceptions_around_time <path> aroundOffset=18m windowMs=30000`. `aroundOffset` accepts `30m`, `+1800s`, `5400000ms`, etc.

Note: `Session.StartTime` is UTC; align your external log timestamps to UTC before querying.

### Timeline / activity over time
You want to know *when* exceptions fire, not what they are — e.g. "is there a long quiet gap?" or "where's the activity peak?".

1. Scope to the workload first: `list_processes <path> sortBy=exceptions` and pick a `[pi]`.
2. `bin_exceptions <path> processIndices=[pi] binSize=1m` — one row per minute, `<bucketStartIso>\t<count>`.
3. Adjust `binSize` (`10s`, `30s`, `5m`, `1h`) to taste. Empty buckets between activity are emitted with count 0 so a gap is visible.
4. To compare two runs, run `bin_exceptions` on each with the same filters and the same `binSize`, then read the rows side-by-side.

### Bulk-fetch only what you need
If you're going to post-process many exceptions externally (e.g. parse timestamps), use `query_exceptions` with `fields="timestamp,id"` to get a much smaller response. Pair with `nextSkip` paging:

1. `query_exceptions <path> processIndices=[pi] fields=timestamp,id maxResults=5000`
2. If the header shows `matched=5000+, nextSkip=5000`, call again with `skip=5000` and continue until the cap isn't hit.

### Root-cause walk-back from a known failure
You found exception `[42/17]` and you want to see what fired in the same process **just before** it.

1. `exceptions_before <path> 42/17 withinMs=2000 maxResults=20` — preceding exceptions in process 42 within 2s, in chronological order.
2. For each, `get_exception <path> <pi/ei>` to read the stack.
3. If the same type repeats hundreds of times before the failure, drop `withinMs` to a smaller window (e.g. `200`) — you usually want the immediate run-up, not the full history.

### "What can possibly throw <SymptomType>?"
The user describes a symptom; you want to find matching exceptions across the file.

1. `count_exceptions <path> exceptionTypeRegex=<symptom>` — first see how common it is and which exact subtypes occur.
2. `count_exceptions <path> exceptionTypeRegex=<symptom> groupBy=type+message` — usually the message disambiguates between very different bugs that share a type.
3. `query_exceptions <path> exceptionTypeRegex=<symptom> messageRegex=<phraseFromSymptom> includeStackTrace=true maxResults=10`.

### Process tree first, exceptions second
Sometimes the question is structural ("which child processes did `dotnet test.exe` spawn, and which threw?").

1. `get_process_tree <path> processNameRegex=dotnet minExceptionCount=1`.
2. From the rendered tree, copy a `[pi]` and `query_exceptions <path> processIndices=[pi]`.

## Output format reminders

- Process line: `<startTime>  <name> pid=N exitCode=E durationMs=D exceptions=N [pi]` (when the process exited within the session). For processes that were still running when the session ended, `exitCode=E durationMs=D` is replaced by `stillRunningAtSessionEnd`.
- Exception line: `<timestamp>  <name> pid=N  ExceptionType: message [pi/ei]` (the process columns are dropped inside scoped tools).
- Headers always include `(skip=A, take=B, matched=C)`. A trailing `+` on `matched` means the result cap was hit; switch to `count_exceptions` for the true total.

## Pitfalls

- **`eventsLost > 0`** means the kernel ETW session dropped events. Counts are a lower bound; assume some exceptions are missing.
- **PIDs are reused** within one recording. Use `processIndex` everywhere; surface PID for humans only.
- **Cancellation noise.** `OperationCanceledException` / `TaskCanceledException` are routine in async .NET code (HttpClient timeouts, `WaitAsync`, ASP.NET request abort). Filter them out with `excludeExceptionTypeRegex=^System\.(Operation|Threading\.Tasks\.TaskC)anceled` when triaging.
- **First-chance, not user-visible.** Beetle records *every* thrown exception, including ones that get caught and handled. A high count is not by itself a bug — what you care about is "which ones are unfamiliar / new / correlated with a failure".
- **Stacks may be partial.** Frames in unmanaged code or in code without symbols print as raw addresses (with the owning native image's file name when known). Use `get_exception` (full trace) rather than guessing from the type.
- **Time fields.** `Process.StartTimeRelativeMSec` etc. are offsets from `Session.StartTime`. The MCP returns absolute UTC ISO 8601 everywhere — use those for correlation.
- **Reload invalidates ids.** After `reload_beetle`, discard previously returned `[pi]` / `[pi/ei]`.
