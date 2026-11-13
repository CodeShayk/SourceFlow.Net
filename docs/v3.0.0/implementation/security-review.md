# Security review (WP-5.5)

This review checks the threat model of doc 10 against the code as built on `v3/m5-security`, at the end of M5. It covers:

- every STRIDE row (§1);
- the deserialisation hardening (§2);
- the supply-chain controls (§7);
- the results of fuzzing every parser that reads untrusted bytes.

Each claim was checked in the code and, where possible, by a test. Gaps were either fixed in WP-5.5, with a test, or are
listed as open items with a recommendation.

**Status: ready for maintainer sign-off.** The M5 exit criteria (plan 09) also need the first 2-hour libFuzzer run per
target in CI, and the signing certificate (open items O-1 and O-2).

## 1. Threat model (doc 10 §1)

| Threat | Mitigation claimed | Evidence | Result |
|--------|--------------------|----------|--------|
| **Spoofing:** a forged message in a service's queue | Broker IAM; the topology export includes the policies | `deploy/topology/terraform/aws/main.tf`: the endpoint queue policy allows `sqs:SendMessage` only from SNS, for the subscribed topic ARNs (`aws:SourceArn`). On Azure and Google Cloud, namespace and project RBAC is the boundary (the IaC modules don't grant send rights) | Verified |
| **Tampering:** a message modified in transit or in the broker | TLS; AES-256-GCM with associated data `id\|type\|source` | `PayloadProtector.AssociatedData`. UT-SEC-01..20, TCK-41..45 with protection on. Fuzz target `protected-payload` | Verified |
| **Tampering:** events edited in the database | The runtime role can't `UPDATE` or `DELETE` `events` | The runtime never updates or deletes `events` (`SqlText.cs`). No grant script shipped, and the test fixtures granted full DML (**F-05**) | Fixed for PostgreSQL; SQL Server open (O-3) |
| **Repudiation:** "who caused this change?" | `actor`, `causationId`, `correlationId`, `source` on every event; dead-letter replay and discard audited with the operator's identity | Event metadata: `CommandProcessor.Metadata`. Dead-letter audit notes carried no identity (**F-06**) | Fixed |
| **Information disclosure:** PII in the log, traces or broker | Crypto-shredding, redaction, SF0013, optional payload encryption, no payloads in spans | WP-5.2 (UT-PII, E2E-29..31), WP-5.3 (UT-RED), WP-5.1. No span sets a body or payload attribute (code search) | Verified |
| **Denial of service:** oversized or poison messages, retry storms | Size limits before deserialisation, bounded channels, retry budgets and parking, circuit breakers, `MaxDepth = 64` | `MaxDepth`, strict numbers and duplicate rejection in `MessageSerializer`. A malformed attribute stopped the receivers (**F-01**). Bodies are bounded by broker limits only (**F-08**). No circuit breakers (**F-07**) | F-01 fixed; F-07 and F-08 recorded |
| **Tampering / disclosure:** injection through the query API | Allow-list grammar, catalog names, bound parameters, read-only role, RLS, limits | Doc 14 §9.3; QRY tests; the nightly DAST scan (WP-4.7) has no findings. Fuzz targets `sfql` and `sql` | Verified |
| **Elevation of privilege:** wire messages instantiating arbitrary types | Registry allow-list; no `$type`; unknown types parked unread | Banned APIs in every `src` project: `Type.GetType`, `Activator.CreateInstance`, `Assembly.Load*`, `MakeGenericType`. The v2 bridge maps only allow-listed names and loads no types. Fuzz target `registry` | Verified |
| **Elevation of privilege:** over-privileged runtime credentials | `ProvisioningMode.Validate` in Production; DML-only database role | `DefaultProvisioning` validates outside Development. The DML-only role is F-05 | Fixed (PostgreSQL) |

## 2. Findings

| ID | Severity | Finding | Resolution | Tests |
|----|----------|---------|------------|-------|
| F-01 | **High** | Malformed JSON in `ce_sfheaders` or `ce_sfext`, or an overflowing `sfstreamversion`, made `CloudEvents.FromAttributes` throw `JsonException` or `OverflowException`. The AWS, Google Cloud and Azure receivers only catch `FormatException`, so one crafted message made every receive that returned it throw, which stalled the endpoint. Found by fuzzing. | `FromAttributes` and `FromStructured` now report every malformed input as `FormatException`, as documented | UT-FUZZ-01, UT-FUZZ-02, UT-AWS-34 |
| F-02 | Medium | A malformed id in a message body threw `FormatException` from the generated JSON converter, so the message was parked as `PermanentFailure` instead of `DeserializationFailed`. Fuzzing. | Generated converters throw `JsonException`. Guid ids still read the empty Guid, as before | UT-FUZZ-03, generator snapshot |
| F-03 | Medium | Invalid UTF-8, or a lone-surrogate escape, in a body holding personal data threw `InvalidOperationException`. Unknown exceptions are transient, so the read was retried and the message then parked as `Exhausted`. Fuzzing. | `PersonalDataProtector.UnprotectAsync` validates UTF-8 and reports both cases as `JsonException` | UT-FUZZ-04 |
| F-04 | Low | Structured CloudEvents, and the `sfheaders`/`sfext` objects, accepted duplicate JSON properties, with the last one winning. Doc 10 §2 disallows duplicates. | Parsed with `AllowDuplicateProperties = false` and `MaxDepth = 64` | UT-FUZZ-01, UT-FUZZ-02 |
| F-05 | Medium | No least-privilege grants shipped. Doc 10 §1 relies on the runtime role being unable to edit `events`. | `dotnet sourceflow db script --grants <role>` (`PostgreSqlSchemaMigrator.GrantsScript`): DML on the SourceFlow tables, `UPDATE`/`DELETE`/`TRUNCATE` revoked on `events` and every partition, and default privileges that keep new partitions append-only. Partition maintenance then runs as the owner (`db partitions ensure`) | SEC-REV-01 (plain and partitioned) |
| F-06 | Low | Dead-letter replay, discard and skip recorded a note, but not who acted. | The CLI records `by <user>@<host>`. `IDeadLetterManager` records `by <actor>` from `IActorAccessor` | SEC-REV-02, UT-CLI-07, UT-CLI-09 |
| F-07 | Info | Doc 10 §1 lists "Polly circuit breakers on outbound calls". v3 has none. | The equivalent controls exist: SDK retries (`MaxSdkRetries`), per-message relay backoff with `deliver_at`, retry budgets and parking. Doc 10 is corrected (as built); a relay breaker is a 3.x candidate (O-4) | — |
| F-08 | Low | Received bodies are not checked against a per-type size limit before parsing. Broker limits bound them (SQS 256 KiB; Pub/Sub 10 MB; Service Bus up to 100 MB on Premium). `MaxDepth` bounds nesting. | Accepted for 3.0 and recorded in doc 10 as built. A per-type limit from the registry is a 3.x candidate (O-5) | — |

## 3. Deserialisation hardening (doc 10 §2)

- **Allow-list.** The registry is frozen at startup. `Type.GetType` and the activators are banned APIs in every runtime
  project. The v2 wire bridge (WP-5.4) resolves v2 names through an explicit map and never loads a type.
- **JSON options.** `MessageSerializer` uses source-generated contexts with `MaxDepth = 64`, duplicate properties
  disallowed and strict number handling. CloudEvents parsing now matches (F-04).
- **Limits.** User headers are capped at 8 and 256 bytes each (`EnvelopeLimits`). Send-side bodies are checked against
  the transport's limit. On receive, bodies are bounded by the broker (F-08).

## 4. Fuzzing (doc 10 §7)

The harness is in `fuzz/SourceFlow.Fuzz`, with ten targets: the CloudEvents binary and structured parsers, the three
wire decoders (with the v2 bridge), the registry lookup with deserialization, the protected-payload decoder, personal-data
markers, and both SFQL parsers with the request guard. See `fuzz/README.md`.

| Run | Inputs | Result |
|-----|--------|--------|
| First in-process runs, 20 s per target | ≈ 1M per target | F-01..F-03 found within seconds |
| After the fixes, 60 s per target | 1.5M to 3.9M per target | No findings |
| After the fixes, 10 min per target in parallel | 5M to 15M per target, about 100M in all | No findings |
| Every PR (`SourceFlow.Fuzz.Tests`, UT-FUZZ-10..20) | Seeds plus 25,000 seeded mutations per target | Green |
| Weekly coverage-guided libFuzzer, 2 h per target (`v3-weekly.yml`) | — | **Not run yet** (O-1) |

The in-process fuzzer is coverage-blind. The libFuzzer runs are the M5 evidence ("fuzzing ran 2 h per target with no
crashes").

## 5. Supply chain (doc 10 §7)

| Control | As built |
|---------|----------|
| Deterministic builds, SourceLink | `Deterministic`, `ContinuousIntegrationBuild` and `EmbedUntrackedSources` in CI; `PublishRepositoryUrl`; the SDK's built-in SourceLink |
| Symbols | `snupkg` symbol packages, not the embedded PDBs doc 10 names (doc corrected) |
| NuGet author signing | `v3-release.yml` signs with `dotnet nuget sign`, using a timestamp, and verifies. Release tags fail without the certificate; alpha packages warn (O-2) |
| SBOM | One CycloneDX JSON SBOM per package (`dotnet-CycloneDX`), uploaded with the packages and attached to every GitHub release |
| Provenance | `actions/attest-build-provenance` for every package (Sigstore, keyless) |
| CodeQL | `v3-codeql.yml` (security-extended) |
| Dependency review | Added to `v3-pr.yml` (fails on moderate or worse) |
| Updates | `renovate.json`: weekly grouped PRs into `v3.0.0`, with a 14-day minimum release age |
| OpenSSF Scorecard | Added as `v3-scorecard.yml` (SARIF to code scanning); the target is ≥ 7 |

## 6. Open items

| ID | Item | Owner | When |
|----|------|-------|------|
| O-1 | First weekly libFuzzer run, 2 h per target, with no crashes (M5 exit) | CI on `v3.0.0` | After merge |
| O-2 | Provision `NUGET_SIGNING_CERTIFICATE`, `NUGET_SIGNING_PASSWORD` and `NUGET_API_KEY` | Maintainer | Before `v3.0.0-rc.1` |
| O-3 | SQL Server least-privilege script (`DENY UPDATE, DELETE ON events`), like F-05 | WP follow-up | Before RC |
| O-4 | A circuit breaker on the outbox relay (F-07) | 3.x backlog | — |
| O-5 | Per-type body size limits checked on receive (F-08) | 3.x backlog | — |
| O-6 | Sign-off of this review | Maintainer | M5 exit |
