# 05 — Unit Test Plan

Unit tests run in-process without Docker. In-memory adapters are real implementations, not mocks. Columns:

- **Covers**: the design rule, ADR or gap the test proves;
- **v2 lineage**: the v2 test whose scenario it continues (`→` = rewritten). "new" means no v2 equivalent existed.

Areas with fully enumerated cases are the correctness-critical ones. Other areas list case ranges with their scenarios; the WP owner expands them into individual tests within the given ID range.

## 1. Contracts — `SourceFlow.Core.Tests` (UT-ABS, WP-1.1)

| ID | Test | Covers | v2 lineage |
|----|------|--------|------------|
| UT-ABS-01 | `Decision_Accept_Single_ExposesEvent` | ADR-003 | new |
| UT-ABS-02 | `Decision_Accept_EmptySpan_Throws` | ADR-003 | new |
| UT-ABS-03 | `Decision_Accept_NullEvent_Throws` | — | → `Saga.Raise_NullEvent_Throws` |
| UT-ABS-04 | `Decision_Reject_NullError_Throws` | ADR-018 | new |
| UT-ABS-05 | `Decision_NoOp_HasNoEventsOrError` | ADR-018 | new |
| UT-ABS-06 | `DecisionContext_IsNew_WhenVersionZero` | doc 03 §4.1 | new |
| UT-ABS-07 | `CommandResult_ThrowIfNotAccepted_ThrowsForRejectedAndConflict` | ADR-018 | new |
| UT-ABS-08 | `CommandResult_IsSuccess_ForAcceptedNoOpDuplicateDispatched` | doc 03 §5.1 | new |
| UT-ABS-09 | `ConsistencyToken_RoundTripsThroughString` | doc 14 §6 | new |
| UT-ABS-10 | `Reaction_Send_AndSend_Schedule_AndBecome_AndComplete_Accumulate` | DC-3, ADR-020 | new |
| UT-ABS-11 | `Reaction_Ignore_IsIgnored` | ADR-020 | new |
| UT-ABS-12 | `Reaction_NullArguments_Throw` | — | → `Saga.Publish_NullCommand_Throws` |
| UT-ABS-13 | `EventContext_AggregateKey_IsSegmentAfterLastDash` | doc 06 §5 | new |
| UT-ABS-14 | `MessageTypeAttribute_DefaultsVersionToOne` | ADR-005 | new |
| UT-ABS-15 | `ProjectionAttribute_DefaultsVersionToOne` | ADR-016 | new |
| UT-ABS-16 | `Error_InvalidCommand_HasStableCode` | ADR-015 | new |
| UT-ABS-17 | `UnhandledMessageException_CarriesType` | — | new |
| UT-ABS-18 | `PublicApi_IsApproved` | plan 04 §6 | new |

## 2. `SourceFlow.Generators.Tests` (GEN, WP-1.2, 1.12, 1.13)

| ID | Test | Covers |
|----|------|--------|
| GEN-01 | Aggregate: emits `IAggregateRuntime<T>` with `AggregateName` from the attribute | ADR-004 |
| GEN-02 | Aggregate: `HandledCommands` lists every `Decide(TCmd, DecisionContext)` | ADR-004 |
| GEN-03 | Aggregate: `Decide` switch dispatches each command; unknown → `UnhandledMessageException` | ADR-003 |
| GEN-04 | Aggregate: `Evolve` switch calls `When`; unknown events → `this` (tolerant) | doc 03 §4.2 |
| GEN-05 | Aggregate: `TargetOf` uses the single id-typed member | DM-R3 |
| GEN-06 | Aggregate: `[AggregateId]` on a positional parameter disambiguates | DM-R3 |
| GEN-07 | Aggregate: private `When` methods are callable from the generated partial | — |
| GEN-08 | Aggregate: file-scoped and global namespaces are both emitted correctly | — |
| GEN-09 | Projection: emits `Apply` switch and `HandledEvents`; `Version` named arg honoured | ADR-016 |
| GEN-10 | Projection: `When` with nullable document return matched | — |
| GEN-11 | Process manager: `React` switch; `Reaction.Ignore` default | ADR-020 |
| GEN-12 | Process manager: `CorrelateBy` returning a strongly typed id → `ToStreamSegment()` | ADR-020 |
| GEN-13 | Process manager: `CorrelateBy` returning a string is used as is | — |
| GEN-14 | Module: `Register` lists message types sorted, with kind inferred (Command / Event / IntegrationEvent) | ADR-005 |
| GEN-15 | Module: registers aggregates, projections, process managers, event and command handlers found in the compilation | ADR-004, GAP-16 |
| GEN-16 | Module: `AddJsonTypeInfoResolver(Context.Default)` emitted when `jsonContext` is given | DC-1 |
| GEN-17 | Module: types with errors (not partial) are not registered | — |
| GEN-18 | Strongly typed id: `New()` uses UUIDv7 via `TimeProvider`; `Parse`/`TryFormat` round trip | ADR-015 |
| GEN-19 | Strongly typed id: JSON converter writes the scalar, not an object | ADR-015 |
| GEN-20 | Strongly typed id: string-backed id honours `MaxLength` | ADR-015 |
| GEN-21..22 | **SF0001**: message without `[MessageType]` reported once per type (partial declarations); abstract types ignored | ADR-005 |
| GEN-23 | **SF0002**: duplicate `(name, version)` in one compilation | ADR-005 |
| GEN-24..25 | **SF0003**: no id member / two id members without `[AggregateId]` | DM-R3 |
| GEN-26..27 | **SF0004**: `When` assigns fields / calls I/O → error; pure `with` expression → no diagnostic | ADR-003 |
| GEN-28 | **SF0005**: event produced in `Decide` without a `When` → warning | — |
| GEN-29 | **SF0006**: command with no Decide or handler and no route | ADR-007 |
| GEN-30 | **SF0007**: `Decide` returning `Task`/`ValueTask` → error, nothing emitted | ADR-003 |
| GEN-31..32 | **SF0008**: non-partial or non-record aggregate; nested type | — |
| GEN-33 | **SF0009**: `DateTime.UtcNow`, `Guid.NewGuid`, `Random` in `Decide`/`When`/`React` → warning | ADR-003 |
| GEN-34..35 | **SF0010**: type missing from the module JSON context → error; code fix adds `[JsonSerializable(typeof(T))]` | DC-1 |
| GEN-36 | **SF0014**: command owned and routed in the same compilation (constant routes) | ADR-007 |
| GEN-37 | Generated sources compile with `TreatWarningsAsErrors` and nullable enabled | — |
| GEN-38 | **Incrementality**: editing an unrelated file produces no new outputs (pipeline steps cached) | ADR-004 perf |
| GEN-39 | Approved-file snapshot of the Bank sample's generated output | review aid |
| GEN-40 | Generator doesn't crash on incomplete or erroneous code (fuzzed syntax) | robustness |
| GEN-41..46 | Schema catalog: JSON Schema per type; `x-sourceflow-personal-data`, `-queryable`, `-tag`, `-renamed-from` extensions; decimal as string; fingerprint stability | doc 14 §2 |
| GEN-47..48 | **SF0015** (query API in Decide/When/React/projection) / **SF0016** (in an `ICommandHandler`) | Q-R1 |
| GEN-49..50 | **SF0018**: `[DomainQuery]` that doesn't bind / whose result columns mismatch | Q-B4 |

## 3. Runtime — `SourceFlow.Core.Tests`

### 3.1 Messaging (UT-MSG, WP-1.3)

| ID | Test | Covers | v2 lineage |
|----|------|--------|------------|
| UT-MSG-01 | `MessageIds_New_IsVersion7AndTimeOrdered` | ADR-015 | new |
| UT-MSG-02 | `MessageIds_FromKey_MatchesRfc9562GoldenVector` | ADR-006 | new |
| UT-MSG-03 | `MessageIds_FromKey_SameKeySameId_DifferentScopeDifferentId` | GAP-09 | new |
| UT-MSG-04 | `Registry_DuplicateName_ThrowsWithSF0002Guidance` | ADR-005 | new |
| UT-MSG-05 | `Registry_DuplicateClrType_Throws` | — | new |
| UT-MSG-06 | `Registry_TryGetUnknown_ReturnsFalse` | GAP-10 | → `AwsSqsCommandListenerTests.ProcessMessage_UnresolvableCommandType…` |
| UT-MSG-07 | `Registry_GetJsonTypeInfo_MissingType_ThrowsWithJsonContextHint` | DC-1 | new |
| UT-MSG-08 | `Serializer_RoundTripsRecordWithStronglyTypedId` | ADR-005 | → `PropertyBasedTests.CommandSerializationRoundTrip` |
| UT-MSG-09 | `Serializer_UsesCamelCase` | doc 04 §2.2 | → `AwsJsonConverterTests` |
| UT-MSG-10 | `Serializer_DeserializeMessage_UnknownType_ThrowsUnknownMessageType_WithoutParsing` | GAP-10 | → `PolymorphicJsonConverterTests.Read_UnknownTypeName…` |
| UT-MSG-11 | `Serializer_MaxDepthExceeded_Throws` | doc 10 §2 | new |
| UT-MSG-12 | `Serializer_DuplicateProperty_Rejected` | doc 10 §2 | new |
| UT-MSG-13..16 | Upcaster chain v1→v2→v3 on read; missing link → startup error; upcasters not applied to wire messages; tolerant reader for added optional fields | ADR-005 | new |
| UT-MSG-17..22 | CloudEvents mapping: binary round trip; structured round trip; `type` = `name.vN`; extensions (`sfcorrelationid`…); `sf_ext` overflow when the header budget is exceeded; trace context preserved | ADR-011 | → `SqsMessageAttributesIntegrationTests` scenarios |
| UT-MSG-23..25 | Size limits: body over the capability limit → `MessageTooLarge` before outbox insert; header count and size limits | NFR-PERF-07 | → `SqsMessageAttributesIntegrationTests.MessageAttributes_ShouldRespectSizeLimits` |
| UT-MSG-26..30 | Envelope defaults (tenant `-`, content type), correlation defaults to message id, causation set from the triggering message, actor propagation, immutability | doc 04 §1 | → `MetadataTests` |

### 3.2 In-memory store (UT-STO, WP-1.4)

These are additional to the SCK, which the in-memory store also runs.

| ID | Test | Covers |
|----|------|--------|
| UT-STO-01 | `Append_ExpectedVersionMatches_AssignsSequentialVersionsAndPositions` | ADR-008 |
| UT-STO-02 | `Append_ExpectedVersionStale_ThrowsConcurrency_AndWritesNothing` | ADR-008, GAP-05 |
| UT-STO-03 | `Append_SecondStreamInSession_ThrowsEsR2` | ES-R2 |
| UT-STO-04 | `Commit_ConcurrencyFailure_DiscardsOutboxInboxDocumentsCheckpoint` | ADR-006 atomicity |
| UT-STO-05 | `Dispose_WithoutCommit_DiscardsEverything` | rollback |
| UT-STO-06 | `ReadLog_ReturnsCommitOrder_AndBucketFilter` | ADR-010 |
| UT-STO-07 | `ReadLog_AfterPosition_IsExclusive` | ADR-010 |
| UT-STO-08 | `Inbox_DuplicateInSameCommit_ThrowsDuplicateMessage` | ADR-006 |
| UT-STO-09 | `Inbox_Seen_ReturnsStoredResult` | doc 04 §6 |
| UT-STO-10 | `Checkpoint_WithStaleLeaseToken_ThrowsLeaseLost` | doc 05 §7.3 |
| UT-STO-11 | `Checkpoint_WithExpiredLease_ThrowsLeaseLost` | doc 05 §7.4 |
| UT-STO-12 | `Lease_NewHolderGetsHigherToken` | fencing |
| UT-STO-13 | `Lease_RenewBySameOwner_KeepsToken` | fencing |
| UT-STO-14 | `Lease_HeldByOther_ReturnsNull` | — |
| UT-STO-15 | `Outbox_Claim_ReturnsOnlyHeadPerKey` | doc 05 §7.1 |
| UT-STO-16 | `Outbox_FailedHeadBlocksLaterSameKey_NotOtherKeys` | per-key order |
| UT-STO-17 | `Outbox_DeliverAtInFuture_NotClaimed` | scheduler |
| UT-STO-18 | `Outbox_ClaimWithoutLease_ThrowsLeaseLost` | — |
| UT-STO-19 | `Outbox_PartitionAssignment_IsStableForKey` | doc 04 §7 |
| UT-STO-20 | `ProcessState_StaleExpectedVersion_ThrowsConcurrency` | ADR-020 |
| UT-STO-21 | `Document_ReadOwnQueuedWrite` | doc 05 §1 |
| UT-STO-22 | `Document_OlderVersionWrite_Ignored` | doc 06 §5 |
| UT-STO-23 | `Document_ReadReturnsVersion` | DC-2 |
| UT-STO-24 | `Notifications_SignalledAfterCommit` | doc 06 §2 |
| UT-STO-25 | `FaultHook_BeforeCommit_ThrowLeavesNoTrace` | chaos support |
| UT-STO-26..30 | Tenant scoping of streams, documents, process state; same stream id in two tenants is isolated | ADR-022 |
| UT-STO-31..35 | Bucket math: `Buckets.Range` for P ∈ {1, 2, 4, …, 1024}; invalid P throws; tenant included in hash; distribution sanity (χ² over 100k ids) | doc 06 §3 |

### 3.3 Runtime model and routing (UT-RT, WP-1.5)

| ID | Scenario | Covers | v2 lineage |
|----|----------|--------|------------|
| UT-RT-01..04 | Builder rejects empty names and URLs or ARNs as endpoint and topic names (provider-neutral names only) | RT-R5 | → `BusConfigurationTests.Builder_Rejects*` |
| UT-RT-05 | Command both owned and routed → startup error naming the type (SF0014) | ADR-007 | new (fixes hybrid Q3) |
| UT-RT-06 | Command neither owned nor routed → `DestinationFor` throws with guidance | ADR-007 | → `BusConfiguration.GetQueueName_…Throws` |
| UT-RT-07 | Two aggregates deciding the same command → error | — | new |
| UT-RT-08 | `ICommandHandler` for an undeclared command → error | — | new |
| UT-RT-09 | Handled type not registered in the registry → error with SF0001 hint | ADR-005 | new |
| UT-RT-10..12 | Publications: `As<T>` mapping, `AsIs`, several topics per event | doc 03 §7 | → `BusConfigurationTests.Builder_RegistersEventRoutes` |
| UT-RT-13..14 | Subscriptions deduplicated; endpoint defaults to the service name | RT-R4 | → `Builder_RegistersSubscribedTopics` |
| UT-RT-15..20 | `SourceFlowOptions` validation: each invalid value fails `ValidateOnStart` with a precise message (no inert options) | GAP-03 | → `GcpOptionsTests`, `SourceFlowEfOptionsTests` |
| UT-RT-21..25 | Descriptors built with generics only (no reflection); projection mode and partitions applied; inline projection with a different store → startup error | ADR-004, ADR-016 | new |

### 3.4 Command processing (UT-CMD, WP-1.6)

| ID | Test | Covers | v2 lineage |
|----|------|--------|------------|
| UT-CMD-01 | `Process_Accepted_AppendsEventsWithCausationAndCorrelation` | ADR-002 | → `CommandBus.Publish_ValidCommand_AppendsToStore` |
| UT-CMD-02 | `Process_Accepted_ReturnsNewVersionAndToken` | doc 14 §6 | → `Publish_ValidCommand_SetsSequenceNumber` |
| UT-CMD-03 | `Process_Rejected_AppendsNothing_ReturnsError` | ADR-018 | → `E2E` validation path |
| UT-CMD-04 | `Process_NoOp_AppendsNothing` | ADR-018 | new |
| UT-CMD-05 | `Process_DefaultTargetId_RejectedInvalidCommand` | ADR-015, GAP-20 | new |
| UT-CMD-06 | `Process_ConcurrencyConflict_RedecidesOnFreshState` | ADR-008, GAP-05 | new |
| UT-CMD-07 | `Process_ConflictBeyondMaxRetries_ReturnsConflict` | ADR-008 | new |
| UT-CMD-08 | `Process_ExpectedVersionMismatch_ReturnsConflictWithoutDeciding` | doc 03 §5.1 | new |
| UT-CMD-09 | `Process_InboxSeen_ReturnsDuplicateWithStoredResult` | ADR-006 | new |
| UT-CMD-10 | `Process_ConcurrentDuplicateCommit_ReturnsDuplicate` | ADR-006 | new |
| UT-CMD-11 | `Process_RejectedWithIdempotencyKey_RecordsInbox_ReplayReturnsSameError` | doc 04 §6 | new |
| UT-CMD-12 | `Process_PublicationMapped_EnqueuedToOutboxInSameCommit` | ADR-006 | → AWS `Dispatch_WhenRouteToAwsIsTrue…` |
| UT-CMD-13 | `Process_PublicationAsIs_EnqueuesDomainEvent` | doc 03 §7 | new |
| UT-CMD-14 | `Process_NoPublicationRoute_NothingEnqueued` | RT-R3 | → `Dispatch_WhenRouteToAwsIsFalse…` |
| UT-CMD-15 | `Process_InlineProjection_WrittenInSameCommit` | ADR-016 | → `Projections/EventSubscriber.Subscribe_WithMatchingProjection…` |
| UT-CMD-16 | `Process_InlineProjectionThrows_CommandFailsNothingAppended` | doc 06 §8 | new (v2 swallowed: GAP-07) |
| UT-CMD-17 | `Process_CommandHandler_ResolvedPerScope_ReceivesStateAndContext` | GAP-08 | new |
| UT-CMD-18 | `Process_CommandHandler_RerunOnConflict` | doc 03 §5.3 | new |
| UT-CMD-19 | `Process_UnknownStoredEventType_FailsLoudly` | GAP-04 | new |
| UT-CMD-20 | `Process_DecisionContextNow_FromTimeProvider` | ADR-003 | new |
| UT-CMD-21 | `Process_EventsGetUuidV7Ids_AndStreamVersionsContiguous` | — | new |
| UT-CMD-22 | `Process_RecordsDurationMetricOnce_WithOutcomeTag` | ADR-019, GAP-14 | new |
| UT-CMD-23 | `Sender_RoutedCommand_SentDirect_ReturnsDispatched` | ADR-007 | new |
| UT-CMD-24 | `Sender_LocalCommand_RunsHandleStageMiddleware` | ADR-012 | → `CommandDispatchMiddlewareTests.ThreeMiddleware_ExecuteInCorrectNestingOrder` |
| UT-CMD-25 | `Sender_IdempotencyKey_BecomesUuidV5MessageId` | GAP-09 | new |
| UT-CMD-26 | `Sender_MiddlewareShortCircuitWithoutResult_Throws` | — | → `Publish_MiddlewareShortCircuits…` |
| UT-CMD-27 | `Sender_NullCommand_Throws` | — | → `CommandPublisher.Publish_NullCommand_Throws` |
| UT-CMD-28 | `Reader_AsOfVersion_FoldsUpToVersion` | doc 03 §5.4 | new |
| UT-CMD-29 | `Reader_AsOfTime_FoldsEventsAtOrBeforeTime` | doc 03 §5.4 | new |
| UT-CMD-30 | `Reader_UnknownAggregate_Throws` | — | new |
| UT-CMD-31 | `Load_FoldIsPure_NoSideEffectsOnReplay` | ADR-002, GAP-06 | → `CommandBus.Replay_*` (replaced concept) |
| UT-CMD-32 | `Load_RecordsLoadEventsHistogram` | doc 08 §3 | new |

### 3.5 Middleware and diagnostics (UT-MW, UT-DIAG, WP-1.7)

| ID | Scenario | v2 lineage |
|----|----------|------------|
| UT-MW-01..03 | Three middleware nest in registration order, for each stage (Send, Handle, Receive, Project) | → the four `*MiddlewareTests.ThreeMiddleware_ExecuteInCorrectNestingOrder` |
| UT-MW-04..05 | Second middleware short-circuits → third and terminal not called | → `SecondMiddleware_ShortCircuits_*` |
| UT-MW-06..07 | Middleware catches the terminal exception / exception propagates | → `Middleware_CanCatchAndHandleException*`, `Middleware_ExceptionPropagates` |
| UT-MW-08 | Middleware receives the same message instance | → `Middleware_ReceivesSameCommandInstance` |
| UT-MW-09 | Stage filtering: Handle-only middleware not invoked at Receive | new (ADR-012) |
| UT-MW-10 | Calling `next` twice invokes the terminal twice (documented, discouraged) | → `Middleware_CallingNextTwice_DispatchesTwice` |
| UT-MW-11 | Middleware can add envelope-scoped items; can't mutate the envelope (immutable) | → `Middleware_CanModifyCommandMetadataBeforeNext` (behaviour changed: metadata is immutable) |
| UT-MW-12..14 | Chains cached per stage; middleware resolved from the per-message scope; singleton registration honoured | new |
| UT-MW-15..20 | Built-ins: telemetry (span name and kind, parent from `traceparent`), logging scope fields, error classification table (doc 04 §9.1) | → `CloudTelemetryTests` |
| UT-DIAG-01..10 | Each instrument in doc 08 §3 recorded **exactly once** per operation (MeterListener) with the expected tags; no stream or message ids as metric tags | new (GAP-14) |
| UT-DIAG-11..15 | `[LoggerMessage]` event ids per range; payload never logged unless `LogPayloads`; levels per doc 08 §4 | → `SensitiveDataMaskerTests` (concept moves to redaction) |

### 3.6 Outbox, receive pipeline, dead letters (UT-OUT, UT-RX, UT-DLQ, WP-1.8)

| ID | Scenario | Covers | v2 lineage |
|----|----------|--------|------------|
| UT-OUT-01..04 | Relay publishes claimed rows in sequence and deletes after acknowledgement; no lease → no work; lease lost mid-batch → stop | ADR-006 | new |
| UT-OUT-05..07 | Transport failure → attempts++ and backoff `deliver_at`; later same-key row waits; other keys proceed | per-key order | → `AwsRetryPolicyTests` (concept) |
| UT-OUT-08 | After `OutboxMaxAttempts` → parked as `OutboxPublishFailed` and removed | doc 04 §7 | new |
| UT-OUT-09..10 | Wake-up on commit signal; adaptive poll when idle | doc 04 §7 | new |
| UT-OUT-11..14 | Scheduler: `deliver_at` in the future isn't sent until `FakeTimeProvider` advances; timer to own endpoint; cancel before send deletes the row | doc 04 §8 | new |
| UT-OUT-15..18 | Partition spread across two relay instances by leases; failover after lease expiry; no duplicate sends while both alive | doc 06 §4 | new |
| UT-RX-01 | Unknown `(type, version)` → parked `UnknownMessageType`, body never deserialised, acknowledged | GAP-10 | → `AwsSqsCommandListenerTests.ProcessMessage_UnresolvableCommandType…` |
| UT-RX-02 | Malformed JSON → parked `DeserializationFailed`, acknowledged | — | → `ProcessMessage_InvalidJson_Deletes…` |
| UT-RX-03 | Received command → owner's command processor with inbox consumer = service | GAP-19 | → `ProcessMessage_ValidCommand_CallsSubscriberAndDeletesMessage` |
| UT-RX-04 | Duplicate delivery → inbox duplicate, handler not re-run, acknowledged | ADR-006 | → `AwsSqsCommandListenerEnhancedTests.ProcessMessage_DuplicateMessage…` |
| UT-RX-05 | Rejected command → acknowledged, not retried; `ReplyOnReject` sends `CommandRejected<T>` | ADR-018 | new |
| UT-RX-06 | Transient failure → `RetryLater(delay[attempt])` | doc 04 §9.2 | → `AwsSqsCommandListenerEnhancedTests.ProcessMessage_HighReceiveCount…` |
| UT-RX-07 | Attempts exhausted → parked `RetriesExhausted` | doc 04 §9 | same |
| UT-RX-08 | Permanent exception classes → parked immediately | doc 04 §9.1 | new |
| UT-RX-09 | Same partition key processed sequentially; different keys concurrently | RX-R1 | new |
| UT-RX-10 | Bounded channel applies backpressure to the receiver | RX-R2 | new |
| UT-RX-11 | Acknowledge only after commit: a crash between commit and ack → redelivery → duplicate absorbed | RX-R3 | new |
| UT-RX-12 | Commands and events on one endpoint told apart by `type` (no competing listeners) | RX-R5, GAP-02 | new |
| UT-RX-13 | Integration event → process managers and event handlers with per-consumer inbox | doc 06 §1 | → `AwsSnsEventListenerTests.ProcessMessage_ValidSnsNotification…` |
| UT-RX-14..16 | Shutdown: stop pulling; in-flight complete within timeout; remainder released | RX-R6 | new |
| UT-RX-17..19 | Trace: consumer span linked to the producer `traceparent`; consumed and process-duration metrics | ADR-019 | → `CloudTelemetryTests.ExtractTraceParent_*` |
| UT-RX-20..22 | Tenant restored from the envelope; actor restored; CESQL subscription filter applied client-side when not native | ADR-022, doc 07 | → `SnsMessageFilteringIntegrationTests` (concept) |
| UT-DLQ-01..10 | Dead-letter manager: list and filter (consumer, reason, type, date), show, replay keeps the message id (the inbox still protects), discard with an audited reason, purge older than a cutoff, metrics | GAP-11 | → `InMemoryDeadLetterStoreTests` (11), `AwsDeadLetterQueuePropertyTests` (replay concepts) |

### 3.7 Subscriptions, projections, process managers (UT-SUB, UT-PRJ, UT-PM, WP-1.9)

| ID | Scenario | Covers |
|----|----------|--------|
| UT-SUB-01..04 | Worker reads after checkpoint, applies, checkpoints in the same commit; empty batch waits for wake-up; adaptive batch size | ADR-016 |
| UT-SUB-05..07 | Unhandled event types still advance the checkpoint; bucket range respected per partition | doc 06 §3 |
| UT-SUB-08..10 | Lease acquisition up to fair share; release of excess; rebalancing after node join and leave | doc 06 §4 |
| UT-SUB-11 | Lease expires mid-batch → commit fails `LeaseLost` → batch discarded, no partial apply | fencing |
| **UT-SUB-12** | **Stale token can't commit after a new owner took over** (paused-node simulation) | fencing |
| UT-SUB-13..16 | Stop policy: poison event retried with backoff; partition stalled (metric = 1); other partitions continue; `projection skip` advances past it (audited) | doc 06 §7 |
| UT-SUB-17..20 | Park policy: after `SubscriptionMaxAttempts`, the event is parked with consumer = subscription and the checkpoint moves past it; batch failure falls back to per-event isolation | doc 06 §7, GAP-07 |
| UT-SUB-21..24 | Repartition: new partitions start from the minimum old checkpoint; refused for non-idempotent projections | doc 06 §3 |
| UT-SUB-25..28 | Rebuild: new generation from position 0; alias switch when lag < threshold; old generation dropped after retention; handlers and PMs excluded unless `IncludeInReplay` | doc 06 §6, GAP-06 |
| UT-PRJ-01..06 | Applier: creates on first event; updates; skips when stored version ≥ event version (**DC-2**); null return leaves the document; inline and async share the code path; collection = `name@version` | DC-2, ADR-016 |
| UT-PRJ-07..12 | Batch-aware document projection: one `GetMany` per batch; one upsert per changed document; rebuild throughput sanity | doc 06 §6 |
| UT-PM-01..05 | Correlation: `CorrelateBy` → id; fallback to envelope correlation; correlation GUID ↔ id round trip; non-GUID ids → UUIDv5 | ADR-020 |
| UT-PM-06..09 | State: initial on first message; persisted with version; optimistic conflict → retried; completed instances ignore later messages | ADR-020 |
| UT-PM-10..13 | Commands from a reaction go to the outbox with destination from routing (local endpoint or remote owner), correlation = process, causation = event | ADR-007 |
| UT-PM-14..16 | Timers: `Schedule` → outbox `deliver_at`; delivered to own endpoint; matched back to the instance | doc 04 §8 |
| UT-PM-17..18 | Ignored reaction persists nothing; state type mismatch → error | — |

### 3.8 Hosting, testing library, catalog, query (UT-HOST, UT-TST, UT-CAT, UT-QL)

| ID range | Scenarios | v2 lineage |
|----------|-----------|------------|
| UT-HOST-01..08 | `AddSourceFlow` registers the expected services; no store configured → error; roles enable or disable hosted services; options validated on start; reflection JSON fallback is opt-in only | → `IocExtensionsTests` (12), `GcpIocExtensionsTests`, `AWS IocExtensionsTests` |
| UT-HOST-09..12 | Lifecycle ordering: validate → provision → start → ready; **drain within `ShutdownTimeout`** (UT-HOST-09); ready health check flips only after start | doc 01 §9 |
| UT-HOST-13..16 | `ITenantResolver` sets the ambient tenant for sends; `IActorAccessor` propagates; tenant flows into envelope and session | ADR-022 |
| UT-TST-01..20 | `AggregateSpec` Then / ThenRejected / ThenNoOp failure messages; round-trip and determinism checks; `ProcessManagerSpec.WhenTimeAdvances`; `ProjectionSpec`; `WaitForIdleAsync` covers relay, subscriptions, broker and timers; fault injector hooks | → `E2E` style |
| UT-CAT-01..18 | Catalog registration idempotent; fingerprint mismatch for an existing `(type, version)` fails startup; compatibility checker rules (add optional ✓, remove ✗, retype ✗, rename without `[RenamedFrom]` ✗, with ✓, integration-event baseline checks); AsyncAPI export shape | new (doc 14 §2) |
| UT-QL-01..20 | Lexer and parser: every production in `sfql/1`; error positions and expected tokens (SFQ0101); CESQL expression precedence and operators; durations, decimals, datetimes, parameters | new |
| UT-QL-21..40 | Binder: SFQ0201–0204 (incl. **Q-B1 ownership**); version rename rewrites; missing-field semantics; stage rules G-1…G-6; policy: tenant injection, PII rejection (SFQ0301), permissions (SFQ0501), cost guard (SFQ0401–0403) | doc 14 |
| UT-QL-61..68 | **Request guard**: body and text size, invalid UTF-8, NFC normalisation, NUL/control chars, bidi overrides and isolates, zero-width outside literals, token and depth limits → SFQ0103/0104 | doc 14 §9.2 |
| UT-QL-69..78 | **SQL syntax parser**: every production in §4.2; quoted identifiers vs single-quoted strings; one statement only; `;`, comments, DML/DDL/`EXEC`/`UNION`/CTE/window → SFQ0102 naming the construct; error positions; pipeline ↔ SQL **AST equivalence** for paired queries | doc 14 §4.2, I-2 |
| UT-QL-79..86 | **Joins**: inner/left; G-7 indexed-key rule (SFQ0405); G-8 bounded event joins (SFQ0406); G-9 tenant equality injected and PII join keys rejected; G-10 ambiguity (SFQ0205); `MaxJoins` (SFQ0404); `lookup` desugars to left join | doc 14 G-7…G-10 |
| UT-QL-87..92 | **Grouping, distinct, subqueries, offset**: G-11 (SFQ0206); `HAVING`; `count(distinct)`; `IN`/`EXISTS` subqueries depth and correlation (G-12); `OFFSET` read-model-only (SFQ0409); `MaxGroups` (SFQ0408) | doc 14 G-11, G-12, §7.4 |
| UT-QL-93..95 | **Compiler safety**: every request value becomes a parameter (literals, type names, durations, `LIKE`, `IN`, `LIMIT`/`OFFSET`); identifiers only from the catalog, quoted; function allow-list (I-5) | doc 14 I-3…I-5 |
| UT-QL-41..60 | In-memory interpreter: where / project / summarize (count, sum, avg, min, max, count_distinct, bin) / order / take / lookup; keyset continuation; `at_least` consistency wait with `FakeTimeProvider`; `from aggregate … as of`; `from schema`; domain queries; typed builder → same AST | doc 14 |

## 4. Adapter and edge-package unit tests (summary)

| ID range | Package | Scenarios | v2 lineage |
|----------|---------|-----------|------------|
| UT-SNAP-01..10 | Core + PostgreSQL | Snapshot every N; hash invalidation; snapshot loss harmless; cache `ReadTail` vs `Optimistic` correctness | new |
| UT-EF-01..10 | EntityFrameworkCore | `DbContext` bound to the session transaction; `IdempotentUpsert`; rollback on checkpoint fencing failure | → `EfViewModelStoreIntegrationTests` (concepts) |
| UT-AWS-01..30 | AmazonSqs | Options → `AWSOptions` and client config (**UT-AWS-12: Region honoured**, GAP-03); attributes ≤ 10 with `sf_ext` overflow; FIFO group and dedup ids; batch splitting at 10; visibility heartbeat; `ChangeMessageVisibility` delay ≤ 12 h then fallback; provisioner plan (FIFO/standard compatibility, **UT-AWS-05: raw delivery + one endpoint receiver**, GAP-02); health check | → `AwsSqsCommandDispatcherTests`, `AwsSnsEventDispatcherTests`, `AwsBusBootstrapperTests`, `AwsHealthCheckTests`, `IocExtensionsTests` |
| UT-GCP-01..25 | GooglePubSub | Options and ADC; emulator detection; ordering keys + publisher ordering; subscription naming `{svc}.{topic}` (GAP-13); ack-deadline extension; nack-based delay; dead-letter policy; health | → `GcpIocExtensionsTests`, `GcpOptionsTests`, `PubSub*DispatcherTests` |
| UT-ASB-01..25 | AzureServiceBus | `TokenCredential` vs connection string (Development only); sessions for ordered endpoints; duplicate detection; `ForwardTo` subscription; correlation filters; scheduled re-send delay; lock renewal | → `azure-cloud` `AzureServiceBus*DispatcherTests`, `AzureBusBootstrapperTests`, `AzureIocExtensionsTests` |
| UT-TOP-01..15 | Core | Topology from routing; naming convention (env prefix, `.fifo`, length and charset limits); `Validate` diff report; JSON export schema | → `BusConfigurationTests` (resolution concepts) |
| UT-CLI-01..30 | Tool | Command parsing; each command's handler against in-memory adapters; exit codes; `--explain`; output formats | new |
| UT-ASP-01..20 | AspNetCore | `MapCommand`: Idempotency-Key, If-Match → 412, rejection → 422 ProblemDetails, conflict → 409, ETag and consistency header; `MapDomainQueries` exposes named queries | new |
| UT-ASP-21..40 | AspNetCore query API | Fail-closed mapping without policy (SFQ0504); `/query`, `/query/validate` (never executes), `/query/explain` (permission, placeholders only), `/query/schema` (caller-visible fields); status mapping (400/403/409/413/415/429/503/504); problem+json shape **without input echo** (S-3); security headers (S-2); NDJSON trailer; CSV formula neutralisation (S-4); rate and concurrency limits with `Retry-After`; JSON depth and body limits; tenant from principal | new (ADR-026) |
| UT-HC-01..14 | Core + adapters | Every health check state in doc 08 §5 | → `AwsHealthCheckTests` |
| UT-OTEL-01..08 | OpenTelemetry | Source and meter names; `InstrumentAdvice` histogram boundaries seen by the SDK; semconv opt-in switch | new |
| UT-SEC-01..20 | Security.* | AES-GCM round trip; associated-data tamper → `DecryptionFailed` (permanent); DEK reuse limits (count and age); key zeroing; rotation transparent; provider error mapping | → `AwsKmsMessageEncryptionTests` (8), KMS property tests |
| UT-PII-01..18 | Core + generator | `[PersonalData]` encrypted per subject; erase → `[erased]`; `$personal-data.erased` event; document scrubbing; snapshot deletion via `subject_streams`; SF0011/13/17 | → `SensitiveDataMaskerTests` (concept moves) |
| UT-RED-01..06 | Core | Taxonomy mapping; redactor per environment; `LogPayloads` gating | → `SensitiveDataMaskerTests` |
