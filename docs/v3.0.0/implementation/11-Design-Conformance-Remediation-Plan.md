# 11 — Design conformance remediation

| | |
|---|---|
| **Source** | Design-conformance review of `v3.0.0` at `eae6564` (2026-11-14): the code read against ADR-001…033 and design docs 01–15 |
| **Scope** | 60 register entries (13 high, 30 medium, 15 low, 2 tracking) grouped into 18 work packages, `WP-C.1` … `WP-C.18` |
| **Branch** | `v3.0.0`, committed directly (no work branches); one commit or a short series per work package, `fix(WP-C.n): …` |
| **Size** | about 31 engineer-weeks (ew) in total: P0 5, P1 10.5, P2 4, P3 3, P4 8.5 |

## 1. How to use this plan

Each finding has a stable id (`CR-nn`) in the register (§6). Each work package fixes a group of findings that touch the
same code, and lists:

- the design rule it restores;
- the deliverables;
- the regression tests that must fail before the fix and pass after it;
- the acceptance criteria.

Two kinds of resolution are allowed:

- **Fix the code.** This is the default: the design stands and the code is brought in line.
- **Amend the design.** Where the design itself is wrong or over-promises, the ADR or design doc is changed. The
  amendment is recorded as an accepted deviation (`DC-n`, plan 03 §1) in the same commit. Packages where this is the
  expected outcome say so.

The Definition of Done is plan 09's, plus three additions for this track:

1. **The regression test is committed first.** It must fail on the commit before the fix, and the fix's commit message
   says so.
2. **The test that should have caught the finding is strengthened.** Where a conformance-kit case or an architecture or
   telemetry test missed the finding, that test is tightened (the conformance kit, ARCH, UT-HC and spec fixes are listed
   per package).
3. **No coverage floor drops.** The floors in `tools/CoverageGate/thresholds.json` must hold.

## 2. Priorities

| Priority | Meaning | Gate |
|---|---|---|
| **P0 — Security** | A tenant, permission, personal-data or authorization guarantee can be bypassed | Before the next release candidate (RC). Nothing else ships first |
| **P1 — Delivery and data integrity** | A message can be lost, duplicated or misordered; or the fix changes stored data, so it costs more after GA | Blocks 3.0.0 GA |
| **P2 — Workflow engine** | Workflow instances compensate, time out or start wrongly | Blocks Studio 1.0, and workflow support in GA |
| **P3 — Semantics and parity** | Providers or code paths disagree, or a documented behaviour is missing, with no data loss | Should land before GA; may slip to 3.0.x with a recorded deviation |
| **P4 — Design debt and hygiene** | Duplicated rules, ADR decisions not implemented, weak checks, low-severity gaps | 3.0.x or 3.1; each item either fixed or recorded as an accepted deviation |

## 3. Schedule

Two lanes, in this order (approximate elapsed weeks from the start of the track, one engineer per lane):

| Weeks (approx.) | Lane A: runtime, storage, query | Lane B: security, ASP.NET Core, receive path, transports, workflows |
|---|---|---|
| 0–2 | WP-C.1 query policy (1), WP-C.2 PostgreSQL row-level security (1) | WP-C.3 personal data (2) |
| 2–4 | WP-C.5 routed sends through the outbox (2) | WP-C.4 API and host hardening (1), then WP-C.6 receive path starts |
| 4–6 | WP-C.7 fencing and upserts (1.5) | WP-C.6 receive path (2.5, finishes at 5.5) |
| 6–9 | WP-C.8 stored formats (3) | WP-C.10 workflow timers and pivot (2), WP-C.11 workflow semantics starts |
| 9–11 | WP-C.9 partition count (1.5) | WP-C.11 workflow semantics (2, finishes at 9.5), WP-C.13 transport parity (1) |
| 11–13 | WP-C.12 query parity (2) | P4 packages begin |

P4 packages (`WP-C.14` … `WP-C.18`) follow in either lane. Ordering rules:

- **P0 first.** `WP-C.1` … `WP-C.4` land before anything else.
- **`WP-C.5` (routed sends through the outbox) before `WP-C.15` (middleware stages).** The Publish stage that WP-C.15
  adds runs in the outbox relay, which WP-C.5 makes the only sender.
- **`WP-C.8` and `WP-C.9` before GA without exception.** After GA they need data migrations and a deprecation cycle.
- **`WP-C.10` before `WP-C.11`.** Both change the engine shared with Studio. Each lands with regenerated workflow golden
  files (WF-GOLD) and the generated-versus-simulator differential tests (WF-DIFF) green.

## 4. Work packages

### P0 — Security

#### WP-C.1 Query policy covers subqueries · 1 ew · lane A · findings CR-01, CR-25
- **Design restored:** doc 14 §8 (each source needs its permission), §7.3 (no unbounded scan, SFQ0401), G-12 (no
  filtering on personal data); ADR-026 decision 1.
- **Deliverables:**
  - `PolicyChecker` runs for every bound subquery as well as the outer query. Permissions, scan bounds, personal-data
    use and limits are checked per subquery, with diagnostics at the subquery's span.
  - The personal-data walk goes into subquery `WHERE` clauses.
  - Named domain queries get a per-query authorization policy: `[DomainQuery(Policy = …)]`, or the group policy by
    default, evaluated through `IAuthorizationService` (doc 14 §9.1, CR-25).
- **Tests:**
  - UT-QL-97: an `EXISTS` subquery on events without the events permission is refused.
  - UT-QL-98: an unbounded subquery scan is refused.
  - UT-QL-99: a subquery that filters on personal data is refused.
  - QRY-61: the same three cases through the HTTP API on PostgreSQL.
  - UT-ASP-41: a named query with its own policy refuses a caller who passes only the group policy.
  - GEN-78: the generator emits the policy only when `Policy` is set.
- **Acceptance:** none of the three subquery forms (`EXISTS`, `IN`, scalar) reaches the engine without the source
  permission.

#### WP-C.2 Row-level security on every tenant table · 1 ew · lane A · findings CR-02, CR-03, CR-26, CR-49
- **Design restored:** ADR-022 ("can't leak across tenants"); doc 05 §2 and §9; doc 05 §11 (`db script` and `db migrate`
  give the same schema).
- **Deliverables:**
  - `sf_ensure_event_partitions` and the partition conversion enable and force row-level security, and create the
    `tenant_isolation` policy, on every partition they create, including `events_initial`. A new migration does the same
    for partitions that already exist.
  - `MismatchedIsolationAsync` and `VerifyAsync` include partitions (`pg_inherits`) and `data_keys`. `data_keys` gets the
    `tenant_isolation` policy (CR-49).
  - The detach check runs on `SystemDataSource` (BYPASSRLS). If no system source is configured under row-level security,
    the detach is refused (CR-03).
  - `Script()` emits the partition SQL before the isolation SQL, the same order as `MigrateAsync` (CR-26).
- **Tests:**
  - SCK-77: under row-level security, a direct `SELECT` on a monthly partition returns only the current tenant's rows.
  - SCK-78: after an upgrade (row-level security on first, partitioning added later), a detach with open streams is
    refused.
  - SCK-M08: `db script` and `db migrate` produce the same catalog (compare `pg_policies`, `relrowsecurity` and
    `relforcerowsecurity` for every table).
- **Acceptance:** a startup verification under row-level security fails if any tenant table or partition lacks the
  policy.

#### WP-C.3 Personal data: discovery, erasure and audit · 2 ew · lane B · findings CR-04, CR-05, CR-27, CR-55
- **Design restored:** doc 10 §4 (erasure, bounded cache), §5 (redaction), §1 (repudiation) and §8 (Art. 30).
- **Deliverables:**
  - **Positional records:** discovery reads `JsonPropertyInfo.AssociatedParameter`'s attributes as well as the
    property's, so `[PersonalData]` on a positional record parameter works as doc 10 shows it. The analyzers SF0011 and
    SF0017 do the same.
  - **Collections and depth:** discovery and redaction go into collections and below the current depth limits. Where a
    personal-data member sits in a shape the plan cannot encrypt, startup fails (fail closed) instead of skipping it.
  - **Cluster-wide erasure:**
    - the unwrapped-key cache gets a TTL, `PersonalDataOptions.KeyCacheTtl` (default 5 minutes); an expired entry is
      read again from the store, which shows it shredded;
    - with encryption on, aggregate-cache entries expire after the same TTL;
    - *as built:* eviction on every node through `PersonalDataErased` is not done. Subscriptions are partitioned and
      leased, so one node, not every node, processes the event. The TTL is the bound on how long other nodes keep
      decrypting, and doc 10's "bounded cache" is met that way.
  - **Audit metadata:** the erasure event's metadata comes from the shared builder (`CommandProcessor.Metadata`), with
    actor, causation and trace context (CR-27).
  - **Key provider guard:** `LocalKeyProvider` refuses to run outside Development unless the host sets
    `AllowLocalKeyProvider` explicitly (CR-55).
- **Tests:**
  - UT-SEC-30: `[PersonalData]` on a positional parameter is encrypted, redacted and analyzed.
  - UT-SEC-31: a personal-data member inside a list is encrypted.
  - UT-SEC-32: an unsupported shape fails startup.
  - UT-SEC-34 (in place of E2E-32): two protectors over one key store, as two nodes; after an erasure on A, B still
    decrypts within the TTL and reads the erased value after it.
  - UT-SEC-33: the erasure event carries the actor.
  - UT-SEC-35: `LocalKeyProvider` outside Development needs `AllowLocalKeyProvider()`.
  - GEN-79: SF0011 and SF0017 on positional parameters.
- **Acceptance:** doc 10 §4's example compiles unchanged and behaves as documented, on both nodes.

#### WP-C.4 API and Studio host hardening · 1 ew · lane B · findings CR-06, CR-36, CR-37, CR-38, CR-48
- **Design restored:** doc 03 §5.1 (the route identifies the aggregate); doc 14 §7.4 and §12; I-3 / QRY-60; doc 15 §6.4.
- **Deliverables:**
  - **`MapCommand` route values:** when a route value and a body member disagree, the request is refused with 400.
    When the body omits a member, the route value fills it, as today.
  - **Continuation tokens:** checked against the plan: one key per sort item, each of its sort item's type, and the rows
    remaining present and within the limit. A forged or mismatched token returns 409 `SFQ0601`, never a database error.
    *As built:* no HMAC signature. Tokens must work on every node behind a load balancer, which would need a shared
    key; a forged position only moves the cursor, which the caller can do with a filter anyway.
  - **No request text in SQL:** opaque JSON paths in `exists` and `is null` are bound as parameters (`text[]` with `#>`
    on PostgreSQL, a path parameter on SQL Server). The compilers' comments are corrected.
  - **Status codes:** SFQ0602 returns 503 with `Retry-After`, SFQ0103 returns 413, and the concurrency limit returns 429
    (doc 14 §9.1).
  - **Studio host:**
    - the asset and framework containment check uses `Inside()`;
    - the manifest export write checks for links (`ThroughLink`) and is atomic (write to a temporary file, then move).
- **Tests:**
  - UT-ASP-42: a body and route mismatch returns 400.
  - QRY-62: a CsCheck property over forged tokens gives 400 and never a 500.
  - QRY-60: extended to opaque paths.
  - UT-STU-HOST-13: a sibling folder named like the assets folder is refused.
  - UT-STU-HOST-14: a manifest export through a link is refused.

### P1 — Delivery and data integrity

#### WP-C.5 Every outbound message goes through the outbox · 2 ew · lane A · findings CR-07, CR-28, CR-40
- **Design restored:** ADR-006; doc 03 §5.2 and §6.2; doc 10 §3; doc 04 §2.3; doc 08 §2.1 and §3.1.
- **Deliverables:**
  - **Ambient session:** an ambient storage session (`AsyncLocal`) is set by `MessageDispatcher.ConsumeAsync`,
    `SubscriptionEngine.ApplyAsync` and `CommandProcessor` while a handler runs. `ICommandSender` enqueues a routed
    command into that session through `runtime.EnqueueCommand`, with a deterministic id (the cause's id plus an
    ordinal), so a redelivery produces the same id.
  - **Short-session outbox write:** with no ambient session (an API call), the routed send opens a short session,
    enqueues, commits and wakes the relay. It no longer calls `Transport.SendAsync` directly. This brings payload
    protection, the size check (`MessageTooLarge`) and the relay's retries to the path. The result stays `Dispatched`.
  - **Telemetry:** a `send` span, and `sent.messages` with `messaging.system`, the destination and `error.type` on
    failure (CR-28, together with WP-C.16).
  - **Handler context:** `ctx.Session.Send(...)` on the handler context, as doc 03 §6.2 shows it.
- **Tests:**
  - UT-CMD-33: a handler that sends and then throws leaves no outbox row and sends nothing.
  - UT-CMD-34: redelivering the same message sends a command with the same id, and the owner's inbox absorbs it.
  - UT-CMD-35: a routed command to a protected destination is encrypted.
  - UT-CMD-36: an oversized routed command returns `MessageTooLarge`.
  - E2E-33: a cross-service send on PostgreSQL survives a transport outage during the API call.
- **Acceptance:** `Transport.SendAsync` is called only by `OutboxRelay`. A new architecture test, ARCH-09, asserts this.

#### WP-C.6 Receive path: order, settlement, shutdown, retention · 2.5 ew · lane B · findings CR-08, CR-09, CR-10, CR-11, CR-12, CR-13, CR-42, CR-43
- **Design restored:**
  - ADR-033 §2 and §7; doc 07 §4 and §7 (head-of-line ordering per key; recovery without a restart);
  - RX-R6 (drain on shutdown) and doc 01 §9;
  - ADR-006 and doc 04 §6 (retention checked at startup);
  - doc 07 §4 (core fallback when a delay exceeds the broker's maximum).
- **Deliverables:**
  - **Per-key order on retry (CR-08):** on an ordered endpoint, when a key's head message is abandoned for a delayed
    retry, the worker abandons with zero delay the messages of the same key that are already buffered, instead of
    handling them. Messages of other keys are unaffected.
    *As built:* with the head's delay, not zero: LocalStack hands a released message out while the head still waits
    (TCK-46 failed with zero), and with the head's delay no broker can deliver them before it.
  - **Azure session leak (CR-09):**
    - the session slot's hold is released in a `finally`;
    - a failed settlement disposes the session receiver, so the lock lapses and the broker redelivers;
    - session locks are renewed only for slots that have live deliveries.
  - **Parking scope (CR-10):** `ReceiveEndpoint` takes `PayloadRedaction` in its constructor, and parking never resolves
    from a disposed scope.
    *As built:* the last attempt's scope stays open until the message is settled, so parking resolves from a live scope
    (and a user-registered `PayloadRedaction` still applies); the constructor is unchanged.
  - **Draining shutdown (CR-11):**
    - `SourceFlowHost` becomes an `IHostedLifecycleService`;
    - one token stops pulling and a separate drain token stops in-flight work after `ShutdownTimeout`;
    - queued messages are abandoned with zero delay;
    - one shared deadline covers the whole stop.
  - **Startup checks:**
    - `InboxRetention` is validated against the transport's `MaxRetention`, failing startup in Validate mode and warning
      in CreateIfMissing (CR-12). Doc 05 §9 and doc 04 §6 are aligned on one default.
    - `DelayedRetries` is validated against `MaxDelay`. A longer delay is re-abandoned for the remainder (the core
      fallback) rather than silently truncated (CR-13).
      *As built:* doc 04 §9.2's fallback, not a re-abandon (each abandon would spend a broker delivery and reach the
      native dead-letter limit): the message is re-sent through the outbox with `deliver_at` and `sfattempt`, and the
      original completed. On an ordered endpoint a re-send would let the key's next message overtake the head, so there
      the delay is capped at `MaxDelay` and startup logs it (event 1404).
  - **Mid-receive errors (CR-42):** in the AWS and GCP receivers, a failure partway through a receive releases the
    messages already taken (visibility or deadline 0) before rethrowing, and the stale `_unsettled` entry is removed.
  - **Relay lease (CR-43):** `OutboxRelay` renews its lease during long drains, and publishes each group with the
    transport's batch API (doc 04 §7).
    *As built:* renewal re-acquires the lease under the same token every third of `LeaseTtl`; a lapsed lease ends the
    drain. A partial batch fails only the rows the transport names. Azure also releases what a receive that fails
    partway took (as CR-42 asks of AWS and Pub/Sub). TCK-47 uses an outage during `Complete` as the settle fault.
- **Conformance kit:**
  - TCK-46: receive with max 10 on an ordered endpoint; the head fails once with a delayed retry; order is preserved
    end to end, run through the core.
  - TCK-47: a fault on settle; the key is delivered again without a restart.
  - TCK-48: a delay longer than `MaxDelay` is honoured.
  - TCK-49: Validate creates nothing.
- **Tests:**
  - UT-RX-26: parking with `LogPayloads` on.
  - UT-RX-27: shutdown completes in-flight handlers and abandons queued messages.
  - UT-RT-26: the retention and delay validation.
- **Acceptance:** TCK-46…49 pass on in-memory, LocalStack, the Pub/Sub emulator and the Service Bus emulator.

#### WP-C.7 Storage fencing and upsert atomicity · 1.5 ew · lane A · findings CR-14, CR-15, CR-29, CR-50
- **Design restored:** doc 05 §7.4 (a paused former owner cannot commit); doc 05 §5.5 and §1 (MERGE with HOLDLOCK, one
  batch); doc 06 §2 (backoff before parking); doc 05 §5.3 (ES-R1 by construction).
- **Deliverables:**
  - **Fenced writes lock the lease row:** `FOR SHARE` on PostgreSQL, `WITH (UPDLOCK, HOLDLOCK)` on SQL Server, so a
    concurrent acquire waits. COMMIT is folded into the SQL Server batch. **Design change:** doc 05 §7.3's SQL is
    corrected in the same commit.
  - **SQL Server single-document upsert:** uses `MERGE … WITH (HOLDLOCK)`, the same as the batch path (CR-15).
  - **Process-manager and handler subscriptions (CR-29):**
    - a transient error stalls the partition with backoff (as the Stop policy does), up to a delayed-retry budget,
      before parking;
    - parking and the checkpoint commit in one session.
  - **ES-R1 guard (CR-50):** `FlushAsync` and the `Connection` and `Transaction` getters close the append window.
  *As built:*
  - parking and the checkpoint are not one session (the dead-letter store is not a session write); instead the dead
    letter's id is derived from the consumer and the event, so a re-park after a crash replaces it (UT-SUB-30);
  - the backoff applies to process managers and handlers; a projection that chose Park parks at once;
  - SQL Server's single-document upsert is not a `MERGE … WITH (HOLDLOCK)`: its key-range locks deadlocked two writers
    of a new document (SCK-80), with or without `UPDLOCK`. It updates if newer, else inserts, and on a duplicate key
    updates again if newer, with no range locks;
  - the raw getters also throw while an append is queued and unsent; SCK-63 now flushes before its custom SQL;
  - SCK-80 is a 25-round race in the kit rather than a CsCheck property;
  - COMMIT is not folded into the SQL Server batch: SqlClient sends a parameterized batch through `sp_executesql`, and
    committing the client's transaction there fails (error 266). The lease-row lock is held for the COMMIT round trip;
    fencing is unaffected.
- **Tests:**
  - SCK-79: a stale lease holder that paused after the fence check cannot commit once a new owner has acquired the
    lease. Uses an injected delay; run on all three stores.
  - SCK-80: two concurrent upserts of the same document; the newer version always wins. CsCheck, on SQL Server and
    PostgreSQL.
  - UT-SUB-29: a transient handler error backs off and does not park.
  - UT-SUB-30: a crash between parking and the checkpoint commit leaves one dead letter.
  - SCK-81: `Append` after raw SQL on `Connection` is refused.

#### WP-C.8 Stored formats: ids, fingerprints, backing types · 3 ew · lane A · findings CR-16, CR-17, CR-30, CR-31
**Must land before GA:** it changes persisted schema fingerprints and id validation.
- **Design restored:** ADR-015 (stream id `{aggregate}-{id}`, a malformed id is `InvalidCommand`); ADR-005 ("renaming
  the CLR type doesn't affect stored data"); ADR-029 §2 and §4–5 (stable fingerprints; the catalog describes what the
  serializer writes); doc 06 §5 (global position for cross-stream models).
- **Deliverables:**
  - **Ids (CR-16):**
    - generated ids validate in `ToStreamSegment()` and in the JSON converter's `Write`;
    - `IStronglyTypedId` gains `IsValid`;
    - `StreamOf` turns any invalid id into `InvalidCommandException`;
    - Int64 ids reject values ≤ 0;
    - `StreamId` rejects a dash in the id segment.
  - **Fingerprints (CR-17):**
    - `title`, enum CLR names and `upcastsTo` move out of the fingerprinted canonical text into a non-hashed annotations
      block;
    - the fingerprint covers only the structural schema;
    - a one-time migration recomputes the stored fingerprints of `(type, version)` pairs whose structure is unchanged.
    - **Design change:** ADR-029 is amended to say which members are fingerprinted.
    *As built:* the migration runs at startup, on every store: a conflict whose stored schema, fingerprinted under the
    current rule, matches is re-registered (`ISchemaStore.ReplaceAsync`) instead of reported.
  - **Backing types (CR-30):**
    - the generator emits a static `Backing` member on each id, and the schema catalog and the SFQL catalog read it;
      *As built:* they read it through the id's generated converter (`IStronglyTypedIdConverter` in the serializer
      options), since reflecting over the id type is not AOT-safe;
    - a new analyzer error, SF0022, fires when `[StronglyTypedId(Backing = …)]` disagrees with `Value`'s type.
  - **Cross-stream documents (CR-31):**
    - cross-stream document projections store the global position, and skip events at or below it;
    - `projection repartition` exempts only stream-keyed document projections from `--idempotent`.
    *As built:* every document projection is now guarded, by stream version or by position, so the exemption stays for
    all of them. The position is two nullable columns (PostgreSQL V008, SQL Server V003) written through an
    `IDocumentSession.Upsert` overload; a store without it falls back to the version guard.
- **Tests:**
  - GEN-66: a string id containing a dash is refused at construction or send, never stored.
  - GEN-67: negative Int64 ids are refused.
  - UT-CAT-19: renaming the CLR type keeps the fingerprint.
  - UT-CAT-20: adding v2 with an upcaster keeps v1's fingerprint.
  - UT-CAT-21: Int64 ids are `integer` in the schema and `Number` in SFQL.
  - GEN-68: SF0022.
  - UT-PRJ-13: repartitioning a cross-stream total does not double count.
  - E2E-34: doc 03 §2.1's rename-and-upcast procedure deploys without `SchemaConflictException`.

#### WP-C.9 Per-subscription partition count · 1.5 ew · lane A · finding CR-18
**Must land before GA:** it changes checkpoint storage.
- **Design restored:** doc 06 §3 (repartition is per subscription), §4 (fencing means two instances can't commit the same
  partition).
- **Deliverables:**
  - **Stored count:** `P` is stored with each subscription's checkpoints (a `subscription_partitions` row, through
    migrations on PostgreSQL and SQL Server, and in the in-memory store).
  - **Startup check:** a node whose configured `P` disagrees with the stored value refuses to run that subscription and
    reports `sourceflow.subscription.partition_mismatch`.
  - **Per-subscription override:** `Projections(p => p.Add<T>().Partitions(n))`. `SubscriptionPartitions` becomes the
    default for new subscriptions only.
  - **CLI:** `projection repartition` updates the stored count in the same transaction as the checkpoints.
- **Tests:**
  - UT-SUB-31: changing the global default does not change an existing subscription.
  - UT-SUB-32: a node with a mismatched `P` refuses that subscription.
  - SCK-82: the repartition write is atomic.
  - E2E-35: a rolling deploy with a mixed `P` processes each event once.

### P2 — Workflow engine

#### WP-C.10 Workflow timers and the pivot · 2 ew · lane B · findings CR-19, CR-20
- **Design restored:** doc 15 §5.3 (only the current attempt's timeout counts), §2 and WF-V07 (after the pivot, forward
  recovery only); ADR-020.
- **Deliverables:**
  - **Timer identity (CR-19):**
    - compensation timers get their own identity: kind `compensate-timeout`, step `compensate:{step}`;
    - stale-timer matching also compares the due time;
    - the bridge acts on `Cancel` effects by recording cancelled timer ids in state, so a cancelled timer that is
      delivered later is ignored by id.
  - **Pivot (CR-20):**
    - **Validator:** a new rule, WF-V17, refuses a `next` or `goto` from a step at or after the pivot to an earlier step.
    - **Engine:** records that the pivot passed, and refuses to start compensation afterwards. The only exception is an
      operator `abort --compensate`, which is audited.
    - **CLI:** `workflow resume --step <step before the pivot>` is refused after the pivot.
  - **Specs keep every timer:** the simulator, `WorkflowSpec` and the differential tests keep every scheduled timer.
    `FireTimer(stale)` becomes available in specs.
- **Tests:**
  - UT-WF-77: doc 15 §5.1's example (charge-payment rejected within 5 minutes) gives release-stock its own 15-minute
    timeout.
  - UT-WF-78: a cancelled step timer delivered late is ignored.
  - WF-GOLD-31: WF-V17 positive and negative cases.
  - UT-WF-79: compensation after the pivot is refused.
  - WF-DIFF-06: the generated engine and the simulator agree with several timers pending.

#### WP-C.11 Workflow replies, versions and correlation · 2 ew · lane B · findings CR-32, CR-33, CR-34, CR-35, CR-44
- **Design restored:**
  - doc 15 §5.3 ("a rejection the step doesn't declare goes to ManualIntervention"; "a rejected compensation counts as a
    failed attempt");
  - doc 15 §5.5 (late replies ignored) and §5.6 (new instances start on the latest version);
  - ADR-024 §3 (one semantic core); doc 11 §2 (specs test what runs in production).
- **Deliverables:**
  - **Rejections (CR-32):** `CommandRejected<T>` is registered for every command a workflow sends, compensations
    included. A compensation with `timeout: none` gets the validator warning WF-V18.
  - **Late replies (CR-33):** rejection replies carry their causation id, and the engine compares it with the current
    attempt's deterministic id from `WorkflowIds.Send` or `WorkflowIds.Compensation`.
  - **New versions and new workflows (CR-34):**
    - a new process-manager subscription starts at the head of the log, unless `StartFrom = Beginning` is set
      explicitly;
    - instances are claimed with a shared key per `(workflow name, instance)`, so v1 and v2 nodes can't both start one.
  - **Correlation path (CR-35):** one `CorrelationPath` helper in the DSL core, used by the reader, validator, simulator,
    path enumerator, spec generator and the generator. Nested paths are supported everywhere.
  - **Specs on the production path (CR-44):**
    - `ProcessManagerSpec` and `WorkflowSpec` run through the process-manager runner over in-memory storage;
    - specs correlate through `TryCorrelate`, apply the `MayStart` and completed rules, and round-trip state through
      JSON;
    - doc 11 §2's example is updated.
- **Tests:**
  - UT-PM-19: a rejected compensation is a failed attempt.
  - UT-PM-20: a late attempt-1 rejection is ignored.
  - E2E-36: deploying v2 does not start instances for past events.
  - UT-WF-80: a nested `correlate` path keeps its timers.
  - UT-TST-21: a spec fails where production would fail to correlate.

### P3 — Semantics and parity

#### WP-C.12 Query semantics across providers and least privilege · 2 ew · lane A · findings CR-21, CR-22, CR-23
- **Design restored:** doc 14 §7.1 (read-only connection, statement timeout), I-7, I-8, Q-R2, Q-R3; ADR-023 §4; doc 14 §12
  (identical rows on every provider).
- **Deliverables:**
  - **Bounded interpreter fallback (CR-21):** when a SQL Server query falls back to the interpreter, the interpreter is
    time-boxed to `QueryTimeout`, pushes tenant, type and time filters down to the log read, and caps the rows. Event
    and read-model constructs the compiler can't express are refused up front (`SFQ0410`) instead of falling back. The
    PostgreSQL and SQL Server fallback rules come from one shared `CanCompile` visitor.
  - **SQL Server string semantics (CR-22):** `LIKE` gets `ESCAPE N'\'`, and string equality guards against T-SQL's
    trailing-space padding (compare `DATALENGTH` too) in equality, `IN`, joins, `GROUP BY` and `DISTINCT`.
  - **Least privilege (CR-23):**
    - PostgreSQL gets a query data source (`QueryConnectionString`) with a SELECT-only role;
    - SQL Server uses a snapshot, read-only transaction and requires `ApplicationIntent=ReadOnly` for the query
      connection, with no fallback to the write connection.
    - `db script --grants` emits the query role.
- **Tests:**
  - QRY-63: the cross-provider equivalence suite extended with escaped `LIKE` patterns and trailing-space strings.
  - QRY-64: a fallback query times out at `QueryTimeout`.
  - QRY-65: the query role cannot write.

#### WP-C.13 Transport parity · 1 ew · lane B · findings CR-24, CR-41
- **Design restored:** doc 07 §5 ("existing resources are never changed"; Validate checks encryption); ADR-017.
- **Deliverables:**
  - **AWS queue policy (CR-24):** `CreateIfMissing` merges its statement into an existing queue policy by Sid, keeping
    every other statement.
  - **Encryption (CR-41):** an at-rest encryption requirement (`TransportTopology.Encryption`) is checked by Validate on
    all three clouds (SSE or CMK on AWS, CMK on Azure Premium, CMEK on Pub/Sub). **If the requirement isn't adopted:** doc
    07 §5 and ADR-017 are amended instead.
- **Tests:**
  - UT-AWS-35: an existing cross-account statement survives `CreateIfMissing`.
  - UT-AWS-36, UT-ASB-26, UT-GCP-26: Validate reports a missing key.

### P4 — Design debt and hygiene

#### WP-C.14 One resilience engine (ADR-013) · 2 ew · findings CR-39, CR-45
- **Design restored:** ADR-013; doc 01 ("one retry and dead-letter policy engine"); doc 04 §9.1; ADR-008 (full-jitter
  5–50 ms re-decide).
- **Deliverables:** keyed `ResiliencePipeline`s from `Microsoft.Extensions.Resilience` (Polly v8) driven by
  `TimeProvider`:
  - **`concurrency`:** for re-decide;
  - **`immediate`:** for receive and subscription;
  - **`outbox`:** exponential with jitter;
  - **`ingest`:** for the dead-letter ingestor.

  The six hand-written policies are deleted, and `sf.Resilience(...)` exposes the pipelines, as doc 12 §3 promises.
  Circuit breakers close security-review finding F-07 (CR-45).
- **Tests:** UT-RES-01..06 (one per pipeline: delays, jitter bounds, attempt counts on a fake clock); ARCH-10 (no
  hand-written backoff: banned-API checks on `Task.Delay` inside retry loops).

#### WP-C.15 Middleware stages (ADR-012) · 2 ew · depends: WP-C.5 · finding CR-46
- **Design restored:** ADR-012 (stages Send, Handle, Receive, Publish, Project; built-ins are middleware; one pipeline
  per stage and message type in a `FrozenDictionary`); doc 04 §4.1; RT-R2.
- **Deliverables:**
  - **Publish and Project stages:** the pipeline runs in `OutboxRelay` (Publish) and `SubscriptionEngine` (Project).
  - **Handle stage:** runs inside `CommandProcessor` for both local and remote commands.
  - **Deserialisation:** a built-in step before user middleware. `MessageContext.Session` is set.
  - **Built-ins as middleware:** telemetry and the logging scope run on every stage. Inbox, transaction and retry become
    built-in middleware.
  - **Pipeline cache:** keyed by stage and message type, and filters are evaluated once when the pipeline is composed.
  - **Interim, if the package slips:** registering middleware for a stage that isn't built yet fails startup.
- **Tests:** UT-MW-21..27: one per stage; a Handle-stage authorisation middleware sees remote commands; Receive
  middleware sees the deserialised message.

#### WP-C.16 Telemetry conventions (ADR-019, doc 08) · 1 ew · finding CR-47
- **Deliverables:**
  - **Required attributes:** `messaging.system` and `messaging.destination.name` on all four standard instruments and
    the process span; `error.type` on failures.
  - **No listener, no allocation:** span names are built only when a listener is attached.
  - **Logical names:** `sourceflow.command.type` uses the logical message name.
  - **Log conventions:** event ids by area as doc 08 §4 lists them, and `SourceFlow.*` logger categories.
  - **Per-tenant metric tags:** bounded to the top N tenants (ADR-022).
- **Tests:** UT-HC-11 asserts the required attributes per instrument, not just a subset; UT-HC-16 checks that no
  allocation happens without a listener (BenchmarkDotNet memory diagnoser).

#### WP-C.17 Analyzers and generator consistency · 2 ew · findings CR-51, CR-52, CR-53, CR-54, CR-56, CR-57
- **Deliverables:**
  - **SF0004 (CR-51):** an I/O deny-list (`System.IO`, `HttpClient`, `DbContext`, `Console` and others); mutation
    through method calls on members reachable from `this`; lambdas and local functions inside `When`; a warning on
    mutable collection types in aggregate state.
  - **`Decide` target (CR-52):** a `Decide` for a command that doesn't implement `ICommand<ThisAggregate>` is an error.
    Startup asserts that every decided command is also a targeting command.
  - **Determinism (CR-53):**
    - `DecisionContext` and `ReactionContext` get a deterministic id factory, `ctx.NewId<TId>()`, built on
      `MessageIds.Derive`;
    - SF0009 flags `Guid.CreateVersion7()` and generated `X.New()` inside `Decide`, `When` and `React`;
    - the parameterless `New()` is documented as non-deterministic.
  - **One helper each (CR-54):** a single shared helper for SF0003 target resolution, and one definition of the
    `{name}.rejected` naming rule.
  - **Registration and emitted code (CR-56):**
    - `AddMessage` validates the name and version against `[MessageType]`;
    - an upcaster without a public parameterless constructor gets a diagnostic, SF0023;
    - generated ids implement `IUtf8SpanFormattable`.
  - **SF0012 (CR-57):** doc 03 §9 is corrected to say it is not built.
- **Tests:** GEN-69..77.

#### WP-C.18 Boundaries, options and documentation alignment · 1.5 ew · findings CR-58, CR-59, CR-60
- **Deliverables:**
  - **Architecture tests (CR-58):**
    - ARCH-01 uses an explicit allow-list and also checks the package graph (`project.assets.json`);
    - ARCH-03 (own SDK only) is implemented;
    - ARCH-06 is added to `tests/traceability.csv`.
    - The cloud adapters get an AOT publish fixture (`Cloud.AotCheck`). **If the AOT warnings cannot be cleared:** their
      `IsAotCompatible` marking is removed and the waiver is recorded in plan 09 IR-08.
  - **Runtime namespace (CR-59), one of two options:**
    - move the SPI types (`StoragePorts`, `ITenantResolver`, `IActorAccessor`) out of `SourceFlow.Runtime` and make the
      engine types internal, updating the public API approvals; or
    - amend ADR-027 §1 to make the namespace public SPI.
  - **Options and tenancy (CR-60):**
    - builder slots refuse a second `UseStorage`, `UseTransport` or `UseKeyProvider` (doc 01 §3, GAP-03);
    - a key provider with no consumer warns;
    - `OnFailure` on an Inline projection is refused;
    - `SourceFlowQueryApiOptions` is validated at startup;
    - the query API resolves the tenant through `ITenantResolver`, the same as commands.
  - **CLI outbox partitions:** `--outbox-partitions` is read from the store (the service records its partition count),
    or the relay sweeps partitions outside its range.
  - **SQL Server `Script()`:** guarded batches (`:on error exit`, `IF @@TRANCOUNT = 0 SET NOEXEC ON`).
  - **`RequestAsync`:** compare-and-set (`WHERE target = @target`) in all three stores (ADR-032 §2).
  - **CloudEvents doc drift:** ADR-011 and doc 04 are aligned with the code (`type` carries the version; `sfext`).

## 5. Release gates

| Gate | Packages that must be done | Check |
|---|---|---|
| Next release candidate | WP-C.1 … WP-C.4 | Security review (`security-review.md`) re-run on the P0 areas; no open P0 |
| 3.0.0 GA | WP-C.5 … WP-C.9, plus WP-C.12 and WP-C.13 or their recorded deviations | TCK-46…49 and SCK-77…82 green on every provider; the E2E suite green |
| Studio 1.0 | WP-C.10, WP-C.11 | WF-GOLD and WF-DIFF green; the TravelBooking and OrderFulfilment workflows re-verified in Studio |
| 3.0.x / 3.1 | WP-C.14 … WP-C.18 | Each finding fixed, or recorded as an accepted deviation with an ADR amendment |

STATUS.md gains a "Track C" table with one row per work package. It is updated in the same commit that completes each
package.

## 6. Findings register

Severity is the review's (H = high, M = medium, L = low). "Area" names where the code lives. "Re-checked" means the
finding was re-read against the code during the review; the others come from the review's reading and are confirmed by
the regression test in their package.

| Id | Sev | Area | Finding | Design | WP | Re-checked |
|---|---|---|---|---|---|---|
| CR-01 | H | Query | Subqueries skip the policy checker (permissions, scan bounds, personal data) | doc 14 §8, ADR-026 | C.1 | Yes |
| CR-02 | H | PostgreSQL | Monthly event partitions have no row-level security or policy | ADR-022 | C.2 | Yes |
| CR-03 | H | PostgreSQL | The detach check runs under row-level security and sees no open streams | doc 05 §9 | C.2 | |
| CR-04 | H | Security | `[PersonalData]` on a positional record parameter is ignored; collections are skipped | doc 10 §4–5 | C.3 | Yes |
| CR-05 | H | Security | Erasure only evicts keys on the node that runs it | doc 10 §4 | C.3 | |
| CR-06 | M | ASP.NET Core | `MapCommand` lets the request body override route values | doc 03 §5.1 | C.4 | Yes |
| CR-07 | H | Runtime | A routed `SendAsync` skips the outbox, payload protection, the size check and retries | ADR-006, doc 10 §3 | C.5 | Yes |
| CR-08 | H | Receive / cloud | Ordered endpoints lose per-key order after a delayed retry | ADR-033 §7 | C.6 | Yes |
| CR-09 | H | Azure | A failed settlement leaks its session until the process restarts | doc 07 §7 | C.6 | |
| CR-10 | M | Receive | Parking resolves from a disposed scope when `LogPayloads` is on | doc 04 §9.1 | C.6 | Yes |
| CR-11 | M | Hosting | Shutdown cancels in-flight work instead of draining it | RX-R6, doc 01 §9 | C.6 | |
| CR-12 | M | Runtime | `InboxRetention` is never checked against broker retention | ADR-006, doc 04 §6 | C.6 | |
| CR-13 | M | GCP / core | Delays over `MaxDelay` are silently truncated; the core never reads it | doc 07 §4 | C.6 | |
| CR-14 | M | Storage | Fenced writes do not lock the lease row; SQL Server COMMIT is a separate round trip | doc 05 §7.4, §1 | C.7 | |
| CR-15 | M | SQL Server | The single-document upsert can drop a newer version | doc 05 §5.5 | C.7 | |
| CR-16 | H | Domain | String and Int64 ids can produce unreadable streams or wrong keys | ADR-015 | C.8 | Yes |
| CR-17 | H | Catalog | The fingerprint includes the CLR name and `upcastsTo`, breaking doc 03 §2.1 | ADR-005, ADR-029 | C.8 | Yes |
| CR-18 | H | Subscriptions | The partition count is global and not stored | doc 06 §3–4 | C.9 | |
| CR-19 | H | Workflows | A compensation's timer collides with its step's leftover timeout | doc 15 §5.3 | C.10 | Yes |
| CR-20 | H | Workflows | A `goto` or `next` can get around the pivot | doc 15 §2, WF-V07 | C.10 | |
| CR-21 | M | SQL Server query | The interpreter fallback drops the timeout and cost bounds | doc 14 §7.1 | C.12 | |
| CR-22 | M | SQL Server query | `LIKE` has no `ESCAPE`; `=` ignores trailing spaces | ADR-023 §4, doc 14 §12 | C.12 | |
| CR-23 | M | Query | No read-only role or replica for queries | Q-R2, Q-R3, I-7 | C.12 | |
| CR-24 | M | AWS | `CreateIfMissing` overwrites an existing queue policy | doc 07 §5 | C.13 | |
| CR-25 | M | Query API | Named domain queries have no per-query authorization | doc 14 §9.1 | C.1 | |
| CR-26 | M | PostgreSQL | `db script` emits row-level security before the partition conversion | doc 05 §11 | C.2 | |
| CR-27 | M | Security | The erasure audit event drops the actor and causation | doc 10 §1, §8 | C.3 | |
| CR-28 | M | Telemetry | The direct send has no span and its metric lacks attributes | doc 08 §2.1, §3.1 | C.5 | |
| CR-29 | M | Subscriptions | Process-manager and handler subscriptions park after immediate retries; park and checkpoint are separate | doc 06 §2 | C.7 | |
| CR-30 | M | Catalog | Three rules for an id's backing type disagree | ADR-029 §2 | C.8 | |
| CR-31 | M | Projections | Cross-stream documents have no version guard; repartition treats them as guarded | doc 06 §3, §5 | C.8 | |
| CR-32 | M | Workflows | Rejections of compensations and `on:`-only steps never reach the workflow | doc 15 §5.3 | C.11 | |
| CR-33 | M | Workflows | A late rejection is applied to the current attempt | doc 15 §5.5 | C.11 | |
| CR-34 | M | Workflows | A new version (or new workflow) starts instances for past events | doc 15 §5.6 | C.11 | |
| CR-35 | M | Workflows | The correlation path is worked out four different ways | ADR-024 §3 | C.11 | |
| CR-36 | L | Query API | Continuation tokens are unsigned and unchecked | doc 14 §7.4, §12 | C.4 | |
| CR-37 | L | Query compilers | Opaque JSON paths reach the SQL text as escaped constants | I-3, QRY-60 | C.4 | |
| CR-38 | L | Query API | Three HTTP status codes differ from the design | doc 14 §9.1 | C.4 | |
| CR-39 | M | Runtime | Polly is not used; six hand-written retry policies have diverged | ADR-013, doc 04 §9.1 | C.14 | |
| CR-40 | — | Runtime | `ctx.Session.Send` on the handler context is missing (part of CR-07) | doc 03 §6.2 | C.5 | |
| CR-41 | L | Transports | Validate checks no encryption on any cloud | ADR-017, doc 07 §5 | C.13 | |
| CR-42 | L | AWS / GCP | A broker error partway through a receive strands messages already pulled | ADR-033 §2 | C.6 | |
| CR-43 | L | Runtime | The outbox relay doesn't renew its lease mid-drain and publishes one row at a time | doc 04 §7 | C.6 | |
| CR-44 | M | Testing | Workflow and process-manager specs don't run the production path | doc 11 §2 | C.11 | |
| CR-45 | — | Runtime | Circuit breakers (security-review F-07) come with the resilience engine | ADR-013 | C.14 | |
| CR-46 | M | Pipeline | ADR-012 stages and built-in middleware are only partly built | ADR-012, doc 04 §4.1 | C.15 | |
| CR-47 | M | Telemetry | Standard instruments lack required attributes; span names allocate; naming drift | ADR-019, doc 08 | C.16 | |
| CR-48 | L | Tool | Studio host asset containment and manifest write gaps | doc 15 §6.4 | C.4 | |
| CR-49 | L | PostgreSQL | `data_keys` has no tenant policy | ADR-022 | C.2 | |
| CR-50 | L | Storage | The ES-R1 guard misses raw SQL on `Connection` before `Append` | doc 05 §5.3 | C.7 | |
| CR-51 | M | Analyzers | SF0004 misses I/O and indirect mutation | ADR-003, doc 03 §9 | C.17 | |
| CR-52 | M | Generator | A `Decide` for another aggregate's command compiles and passes startup | DM-R3, doc 01 | C.17 | |
| CR-53 | M | Domain | Id creation in pure methods is non-deterministic and unflagged | ADR-003, ADR-015 | C.17 | |
| CR-54 | L | Generator | SF0003 target resolution and the rejected-name rule are duplicated | doc 01 (DRY) | C.17 | |
| CR-55 | L | Security | `LocalKeyProvider` has no production guard | ADR-027 | C.3 | |
| CR-56 | L | Domain | `AddMessage` accepts mismatched names; skipped upcasters; no `IUtf8SpanFormattable` | ADR-005, doc 03 §3 | C.17 | |
| CR-57 | L | Docs | Doc 03 §9 lists SF0012, which isn't built | doc 03 §9 | C.17 | |
| CR-58 | M | Architecture | ARCH-01 is weaker than its name; cloud adapters are AOT-marked but unchecked | ADR-027, doc 01 §2 | C.18 | |
| CR-59 | L | Architecture | The "internal" `SourceFlow.Runtime` namespace is public SPI | ADR-027 §1 | C.18 | |
| CR-60 | L | Hosting | Builder slots and options accept conflicts silently; tenant resolution is split | doc 01 §3, ADR-022 | C.18 | |

Three smaller items are folded into packages without their own ids:

- the CLI's `--outbox-partitions` default (WP-C.18);
- SQL Server `Script()` batch guards (WP-C.18);
- `RequestAsync` compare-and-set (WP-C.18).

`CR-40` and `CR-45` are tracking entries for work that is part of a larger finding, so they carry no severity.
