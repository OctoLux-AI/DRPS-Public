# DRPS — Digital Repeatable Parametric System

DRPS is a five-layer, C#/.NET systematic trading pipeline: it ingests market data from
multiple independent vendors, cross-verifies it before trusting it, computes technical
indicators from the verified data, scores candidates against a quality model, sizes
positions, and fires orders through a broker API with its own layer of pre-trade safety
checks. The engineering discipline behind it is summed up by one rule applied at every
layer: **Don't Trust, Audit** — every value that crosses a trust boundary (a second data
vendor, a broker response, an assumption about what "should" be true) gets independently
verified against real evidence before anything downstream is allowed to depend on it,
because a plausible-looking value and a correct one are not the same thing, and treating
them as the same thing is how systems fail quietly instead of loudly.

**Honest framing, stated plainly:** this is currently a supervised paper-trading validation
system, not a live trading system. Execution runs against a broker's paper account, with a
dry-run default that has to be deliberately turned off, and a human watching. Nothing here
claims to be production-proven with real capital.

## Architecture: five layers, one direction

```
Ingestion → Calculator → Gate → Adjuster → Execution
```

- **Ingestion** — pulls raw OHLCV bars and reference data (sector, earnings, insider
  filings, regime indices) from multiple vendors (Alpaca, Tiingo, Finnhub, FRED, SEC
  EDGAR), cross-verifies overlapping sources against each other, and tracks per-vendor
  reliability so a source that starts failing gets flagged and stopped, not silently
  trusted forever.
- **Calculator** — computes DMA, RSI, RVOL, and ATR from the verified bars. Nothing here
  reads raw, unverified data directly; every computed value carries its own verification
  provenance forward.
- **Gate** — the candidate-scoring layer. Runs a two-tier verification policy (some
  indicators are a hard pass/fail gate, others contribute partial credit under
  uncertainty) and produces a composite score and bucket assignment for each candidate.
  The actual scoring formula is not included in this repository — see "What's
  intentionally not public" below.
- **Adjuster** — position sizing. Takes a scored candidate and computes how much capital
  to commit, subject to sector caps, a capital reserve schedule, and a concurrent-position
  cap, combining multiple independent sizing signals (insider activity, sentiment, options
  flow) without letting any single signal dominate. Sizing logic is also not included — see
  below.
- **Execution** — fires the actual order: quote-based marketable-limit pricing, retry
  classification for ambiguous broker failures, fractional-share handling, and a set of
  pre-fire safety gates (market hours, cash floor, a kill switch, a concurrent-open-position
  cap enforced with atomic reservation to close a real race condition — see the case
  studies below).

**Tech stack:** C# / .NET, Entity Framework Core, SQL Server, xUnit. Market and reference
data from Alpaca (execution venue + primary OHLCV), Tiingo (independent cross-verification
source), Finnhub (sector classification, earnings calendar), FRED (volatility index
history), and SEC EDGAR (insider Form 4 filings).

## Engineering discipline in practice

These are real bugs found and fixed during development, not illustrative examples. Each
one was caught by actually testing against live data or exercising a real failure path,
not by inspection alone.

**A TOCTOU race in the concurrent-position cap.** The pre-fire gate that enforces a hard
cap on simultaneously-open positions read the current open-position count from the
database, compared it against the limit, and allowed or rejected the fire — but every
candidate from one scan cycle fires as an independent, concurrent task. Two candidates
could each read the identical "14 of 15 slots open" count at the same moment and both pass
the check, landing the account two positions over its own hard limit. The fix wasn't a
database-level lock — it was an atomic in-process reservation (`TryReserveOpenSlot`) that
accounts for every other ticker's in-flight attempt, not just what's already committed to
the database, closing the race at the point where concurrent tasks actually contend rather
than trying to serialize the database read.

**A silent ~50% transport failure rate that looked like a data-source problem.** A
diagnostic pass measured actual reliability of `HttpClient` calls against a specific
external API and found roughly half of all attempts silently failing — not with a clean
error, but by hanging until timeout and eventually failing after burning through the full
retry budget. The instinct would be to blame the vendor. Direct comparison against `curl`
issuing the identical request to the identical URL showed the vendor was fine — every
`curl` call succeeded in one to two seconds. The failure was specific to how the .NET
HTTP stack was behaving against that particular host in that environment. The fix was
routing that vendor's calls through a shell-out to `curl` instead of `HttpClient` — an
unusual fix, arrived at only because the diagnosis didn't stop at "which layer looks
guilty" and went and measured both sides independently.

**A rounding-direction bug in live order pricing.** Marketable-limit order prices are
computed as `ask × (1 + buffer)` for a buy or `bid × (1 − buffer)` for a sell, then rounded
to the nearest cent — except the rounding wasn't direction-aware, and a live order was
rejected by the broker with "sub-penny increment does not fulfill minimum pricing
criteria" the first time a real, non-round-number price hit the code path. A buy limit that
rounds down risks landing below where it needs to be to actually cross the spread; a sell
limit that rounds up has the same problem in reverse. The fix uses explicit
ceiling-to-cent for buy orders and floor-to-cent for sell orders — the default banker's
rounding in the runtime was wrong for both directions, just never wrong in a way the test
fixtures (which happened to divide evenly) had ever exercised.

**A missed consolidated closing print, caught by cross-vendor verification.** The primary
execution venue's free-tier market data feed is not the full consolidated tape — it's a
single-exchange feed, and on a genuine subset of volatile trading sessions, its own
reported closing price for a given bar diverged meaningfully from the real, publicly
verifiable consolidated close. This wasn't found through documentation; it was found by
independently confirming a handful of flagged discrepancies against real published market
data and finding the primary source's Close was wrong, not the secondary vendor's, in every
case checked. The system now applies a narrow, evidence-scoped exception: when a bar's
Open/High/Low all agree closely between the two vendors and only the Close disagrees, the
secondary vendor's Close is trusted for that field specifically — a targeted correction
based on a confirmed, repeatable pattern, not a blanket "trust vendor B more" rule.

**A build-collision race condition in unattended scheduling.** Three worker processes were
each launched via a bare `dotnet run`, scheduled to start at the same time every night —
and `dotnet run` triggers an implicit build/restore on every launch. When all three
processes' start times coincided, they collided on the same solution tree mid-build, and
two of the three exited with a generic failure code before writing a single log line,
making the actual cause invisible from the failure alone. The fix was publishing each
worker to a fixed output folder ahead of time and having the scheduler launch the compiled
executable directly, removing the implicit build — and therefore the collision — from the
unattended path entirely.

## Test coverage

**999 tests, 999 passing.** Coverage spans data-verification logic (cross-vendor
reconciliation, dead-source tracking, tolerance rules), the indicator computation pipeline,
candidate-discovery and staleness-detection logic in Gate, concurrency-safety and
retry-classification behavior in Execution, and the order-firing path end to end against a
faked broker transport (no live network calls in the suite — every HTTP-backed component
is tested against a real, unmodified call chain with only the transport faked, not a
mocking framework).

## What's intentionally not public, and why

The actual scoring formula (how technical indicators combine into a single composite
score and bucket assignment) and the position-sizing formula (how a scored candidate's
capital allocation is computed, including how multiple independent signals combine into
one sizing multiplier) are not included in this repository. The classes, method
signatures, and surrounding architecture are — verification logic, candidate discovery,
staleness handling, the pre-fire safety gate stack, order firing and reconciliation — but
the specific tuned formulas are redacted, replaced with a clear marker rather than
silently omitted.

This is a deliberate choice to protect the actual mechanism the system trades on, not an
attempt to hide weakness — the parts that are here are here specifically because they're
the parts worth showing: what it looks like to build a data pipeline that doesn't trust
its own inputs, and what it looks like to find and fix the kind of bug that only shows up
once real, live, concurrent, or malformed data actually hits the system.

## About

Built by Kent Rice — AI Solutions Architect, USMC Veteran.
[LinkedIn](https://www.linkedin.com/in/kent-rice-sweng)
