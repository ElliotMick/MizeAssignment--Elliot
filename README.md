# Mize Reservations – ingestion service

An ASP.NET Core 8 Web API that sits in front of the reservations table. It receives reservation updates from external suppliers through a webhook-style feed, and it does three things:

- **Dedupes resends.** Ingest is idempotent: a supplier resending the same update changes nothing.
- **Applies real changes.** A changed reservation is updated, but an out-of-order *older* version never overwrites a newer one.
- **Throttles noisy suppliers.** Each supplier gets at most 100 requests in any rolling 60-second window.

The rules hold even when **several instances** of the service run behind a load balancer. Data access uses **Dapper** on **SQL Server LocalDB**.

## Quick start

**Prerequisites:** .NET 8 SDK and SQL Server LocalDB (installed with Visual Studio). The default instance `(localdb)\MSSQLLocalDB` is used.

```powershell
dotnet run --project src/MizeReservations.Api --launch-profile http
```

- On first start (Development) the service creates the `MizeReservations` database and applies the scripts in `Migrations/` (`001_InitialSchema.sql`, `002_AddAcceptedAndFailedCounts.sql`).
- API: `http://localhost:5080`. Swagger UI: `http://localhost:5080/swagger`.
- **Postman:** import `postman/MizeReservations.postman_collection.json`. Its description explains how to run the folders, including the 105-iteration throttling run.
- **Inspect the data:** use Visual Studio › View › SQL Server Object Explorer › `(localdb)\MSSQLLocalDB` › `MizeReservations`, or run:
  `sqlcmd -S "(localdb)\MSSQLLocalDB" -d MizeReservations -Q "SELECT * FROM dbo.Reservations"`

To apply the schema by hand instead (for example with `Database:ApplyMigrationsOnStartup=false`), run:

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "IF DB_ID('MizeReservations') IS NULL CREATE DATABASE MizeReservations"
sqlcmd -S "(localdb)\MSSQLLocalDB" -d MizeReservations -i src\MizeReservations.Api\Migrations\001_InitialSchema.sql
sqlcmd -S "(localdb)\MSSQLLocalDB" -d MizeReservations -i src\MizeReservations.Api\Migrations\002_AddAcceptedAndFailedCounts.sql
```

## API

### `POST /api/reservations/ingest`

```json
{
  "supplierId": "acme",
  "reservationId": "R-1001",
  "roomId": "room-101",
  "checkIn": "2026-08-01T00:00:00Z",
  "checkOut": "2026-08-03T00:00:00Z",
  "price": 450.00,
  "updatedAtUtc": "2026-07-01T10:15:00Z"
}
```

| Situation | Response |
|---|---|
| New reservation | **201 Created**, `outcome: "Created"` |
| Exact resend: same supplier, reservation and every detail | **200 OK**, `outcome: "Unchanged"`, *"Identical to the stored reservation; nothing was written."* |
| Same reservation, changed details, `updatedAtUtc` equal to or newer than the stored one | **200 OK**, `outcome: "Updated"` |
| Same reservation, `updatedAtUtc` **older** than the stored one | **409 Conflict** (problem+json) with `outcome: "Stale"` and `storedUpdatedAtUtc`. Nothing is written. |
| Supplier over 100 requests in the last 60 s | **429 Too Many Requests**, `Retry-After: <seconds>` |
| Missing field or malformed JSON | **400 Bad Request**, validation problem details naming each field |
| Breaks a business rule of the table (checkOut ≤ checkIn, price < 0 or more than 2 decimals, blank ID or ID over 100 chars) | **400 Bad Request** with `outcome: "Invalid"` and every broken rule per field. Nothing touches the database, and the request is not counted against the supplier. |
| Gave up after repeatedly losing races on the same reservation (practically unreachable) | **503**, `Retry-After: 1` |
| Anything unexpected | **500** problem details, with no internals leaked |

The 200 and 201 bodies have the shape `{ "supplierId", "reservationId", "outcome", "message" }`.

### `GET /api/reservations/stats/{supplierId}`

```json
{ "supplierId": "acme", "ingested": 100, "throttled": 3,
  "breakdown": { "created": 97, "updated": 1, "unchanged": 1, "stale": 1, "failed": 0 } }
```

- `ingested` counts every request that got past the throttle. `throttled` counts the 429s. Both are counted in the same transaction as the throttle decision, so together they always equal the requests the throttle saw.
- `breakdown` shows how each ingested request ended, so `ingested = created + updated + unchanged + stale + failed`. `failed` covers a request that passed the throttle but couldn't be stored (a 503 or 500, or the client disconnecting). Without it, such a request would hold a window slot without appearing anywhere in the stats.
- The outcome is recorded even if the client has already disconnected, because the stats write is not cancellable. If that stats write itself fails, the failure is logged and the supplier still gets its success response, because the reservation is already stored.
- `throttled` counts the 429s.
- An unknown supplier gets all zeros.
- Stats reads are not throttled.

## Design decisions and assumptions

**Multiple instances means all shared state lives in SQL.** An in-memory rate limiter, such as ASP.NET Core's built-in `RateLimiter`, would let a supplier through N × 100 times with N instances. Both the dedup and the throttle are therefore enforced by the database. The test `TwoInstances_ShareOneLimitPerSupplier` runs two API hosts on one database and checks that exactly 100 requests get through between them.

**Idempotency comes from the composite primary key `(SupplierId, ReservationId)`.**
- A `reservationId` is unique *per supplier*: two suppliers can use the same ID.
- "Same details" means `roomId`, `checkIn`, `checkOut`, `price` and `updatedAtUtc` are all equal.

**`updatedAtUtc` is the supplier's version number.**
- A strictly older version is a delayed retry, so it is rejected with 409 rather than overwriting newer data.
- When the timestamps are equal but the details differ, the request that arrives last wins, because timestamps can't order them.

**Business rules are checked first.** `IngestReservationRequest.FindRuleViolations()` checks the rules the table enforces: column sizes, `CHECK (CheckOutUtc > CheckInUtc)`, `CHECK (Price >= 0)` and `DECIMAL(18,2)`. `ReservationService.IngestAsync` takes the request and calls this before any database work, then maps the request to the `Reservation` model. Every broken rule is returned in one 400 response. The limits are constants on `Reservation`, next to the schema they mirror.

**Concurrency: insert first, then compare, with conditional writes.** The number of attempts is configurable as `Ingest:MaxWriteAttempts` (default 5).
- **Insert.** Every ingest first tries the INSERT, so a new reservation costs one round trip. If the reservation already exists, whether stored earlier or a moment ago by another instance, SQL Server rejects the insert with error 2627/2601. `ReservationRepository.TryInsertAsync` catches that and returns `false` instead of throwing. The service then reads the stored row and decides between Unchanged, Stale and Update.
- **Trade-off.** A resend now costs a rejected insert plus a read, where a read-first design would need only the read. Insert-first favours new reservations.
- **Update.** Updates use optimistic concurrency on a `ROWVERSION` column (`WHERE … AND RowVersion = @Expected`). If the row changed after it was read, the update affects 0 rows and the service re-evaluates. An older version can therefore never overwrite a newer one, even under races.

**The throttle is a sliding-window log in SQL, handled in one batch and one round trip:**
1. Take an `sp_getapplock` on the supplier. This is a per-supplier mutex shared by every instance.
2. Delete log entries older than 60 s.
3. Count what remains.
4. If the count is under 100, record the request. Otherwise count it as throttled and return when the oldest entry leaves the window, which becomes `Retry-After`.

Only *accepted* requests occupy the window, so a supplier that keeps sending while blocked is not locked out forever. Malformed requests get a 400 before any database work and are not counted. The limit and window are configurable under `Throttling` in appsettings.

**Data fidelity, so that a resend still matches after a database round trip:**
- Dapper sends `DateTime` parameters as the legacy `datetime` type (3 ms precision) by default. The service maps them to `DATETIME2(7)` instead (`SqlMapper.AddTypeMap`). Without that mapping, a resend with sub-millisecond timestamps would look like an update. A test proves this.
- Request timestamps are `DateTimeOffset`, normalised to UTC, so `+02:00` and `Z` forms of the same instant are equal.
- `price` is `DECIMAL(18,2)`, and more than 2 decimals is rejected rather than silently rounded.
- ID columns use a binary collation, so IDs are exact and case-sensitive (`R-1` ≠ `r-1`). IDs are trimmed.

**Migrations.** Dapper has no migrations, so the schema is in hand-written, re-runnable scripts in `Migrations/`. `002` adds the accepted and failed counters and backfills `AcceptedCount` for rows written before it.
- `DatabaseMigrator` applies unapplied scripts in name order and records them in `dbo.SchemaMigrations`.
- It holds an application lock while doing so, so instances starting together don't race.
- It runs on startup only when `Database:ApplyMigrationsOnStartup` is `true`, which is set in Development. In production, migrations would be a deployment step.
- The test suite builds its database with the same migrator, so the script itself is tested too.

**HTTPS redirection is not enabled.** TLS is expected to terminate at the load balancer or gateway. This also keeps Postman testing over plain HTTP simple.

## Project structure

```
src/MizeReservations.Api/
├─ Controllers/ ReservationsController (endpoints only), IngestActionResults (service result → HTTP response)
├─ Services/   IReservationService, ReservationService      business logic (throttle → dedup/update → stats)
├─ Repository/ IReservationRepository, ReservationRepository  Dapper calls
│  └─ Queries/ ReservationQueries, SupplierRequestLogQueries, SupplierStatsQueries  the SQL, one class per table
├─ Models/     Reservation, SupplierStats (tables) + IngestOutcome/IngestResult/ThrottleDecision
│  └─ Dtos/    request/response contracts (IngestReservationRequest holds the business-rule check)
├─ Migrations/ 001_InitialSchema.sql, 002_AddAcceptedAndFailedCounts.sql, DatabaseMigrator
└─ Infrastructure/ connection factory, Throttling/Ingest options, global exception handler
tests/MizeReservations.Tests/
├─ Unit/        service decision paths (mocked repository), request validation, exception mapping
└─ Integration/ real LocalDB: repository, idempotency incl. parallel races, throttling, HTTP API
postman/        ready-to-import Postman collection
```

## Tests

```powershell
dotnet test                                    # everything (needs LocalDB)
dotnet test --filter "Category!=Integration"   # unit tests only, no database
```

- The integration tests create a throw-away `MizeReservations_Tests_<guid>` database and drop it afterwards.
- To use another SQL Server, set the environment variable `MIZE_TEST_SQLSERVER` to a server-level connection string.

What the tests cover:
- **Unit:** every idempotency path through the service.
  - Created / Unchanged / Updated / Stale, plus the equal-timestamp tie.
  - Each business rule returns Invalid without any database call, and all broken rules are reported together.
  - The same instant sent in another UTC offset is still Unchanged.
  - An insert rejected by the key, an update race lost, and a row that vanished between the two; each is re-evaluated.
  - Giving up after the configured number of attempts.
  - A throttled request never touches reservations.
  - Request validation, and mapping of exceptions to responses.
- **Integration:**
  - A duplicate-key insert returns `false` instead of throwing.
  - Composite key, case-sensitive IDs and full-precision round trip.
  - Row-version guard.
  - The lifecycle Created → Unchanged → Updated → Stale, checking that nothing is written on the no-op outcomes.
  - 20 parallel identical requests produce exactly 1 Created and 1 row.
  - Parallel versions: the newest one wins.
  - Throttling: request 101 is rejected, the window rolls, `Retry-After` is correct, limits are per supplier, and 150 parallel requests let through exactly 100.
  - The HTTP contract, including the two-instance test.

Each concurrency safeguard was checked by removing it and confirming that a test fails:
- the applock
- the duplicate-key catch
- the `DATETIME2` mapping
- the row-version guard

## Out of scope / next steps

- **Heavy floods.** Every request costs one small database round trip. Two ways to reduce that:
  - Cache "supplier blocked until T" in each instance. This is exact, because no slot can free up before T.
  - Move the window to Redis.
- **Future-dated `updatedAtUtc`.** A supplier clock far in the future would make later genuine updates look stale. Reject versions beyond a small skew allowance.
- **Retention.** The window log prunes itself, and the stats are counters. A real system would also keep an audit log of raw payloads.
- **Auth, observability and outbox events.** Per the brief: authenticate suppliers (and derive `supplierId` from the credential), add metrics and tracing, and publish change events downstream.
