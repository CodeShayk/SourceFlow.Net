# 10 — Security and Compliance

Design drivers: [ADR-005](ADRs/ADR-005-logical-message-type-names-and-registry.md), [ADR-013](ADRs/ADR-013-use-platform-libraries-delete-bespoke-equivalents.md) and [ADR-017](ADRs/ADR-017-provisioning-modes-and-topology-as-data.md). Fixes: GAP-10, GAP-12, SEC-03/04/06, NFR-SEC-02/03/04.

## 1. Threat model (STRIDE)

The assets are domain events (the system of record), command and integration messages in flight, read models, encryption keys, and broker and database credentials.

| Threat | Vector | Mitigation in v3 |
|--------|--------|------------------|
| **Spoofing** | A forged message written into a service's queue | Broker IAM: only named principals can send or publish (the topology export includes the policies). An optional `sfsig` HMAC envelope signature is a 3.x candidate (YAGNI for 3.0, because broker auth is the boundary). |
| **Tampering** | A message modified in transit or in the broker | TLS to every broker and database. Payload protection uses AES-256-GCM with **associated data** binding `(id, type, source)`, so a ciphertext can't be replayed under another message's header (§3). |
| **Tampering** | Events edited in the database | The runtime role can't `UPDATE` or `DELETE` `events` (separate grants; the migration role owns the DDL). Detection: optional periodic checksum job (3.x). |
| **Repudiation** | "Who caused this change?" | Every event's metadata records `causationId`, `correlationId`, `source` and **`actor`** (from `IActorAccessor`, for example the authenticated user or workload identity). Dead-letter replay and discard are audited with operator identity. |
| **Information disclosure** | PII in the immutable log, logs, traces or broker | Crypto-shredding (§4). Redaction (§5). Integration events are a separate contract without PII (doc 03 §7, SF0013). Optional payload encryption. Payloads are never put in span attributes. |
| **Denial of service** | Oversized or poison messages, retry storms | Size limits before deserialisation. Bounded channels. Retry budgets and parking. Polly circuit breakers on outbound calls. JSON `MaxDepth = 64`. |
| **Tampering / information disclosure** | SQL or script injection through the per-service query API | The API is never a SQL pass-through: allow-list grammar, catalog-only names, every request value bound as a parameter, no dynamic evaluation, safe encoding and headers, read-only role and transaction, RLS, limits and rate limiting ([doc 14 §9.3](14-Query-Side-and-SFQL.md#93-injection-defence), ADR-026) |
| **Elevation of privilege** | A wire message instantiating arbitrary types (v2 GAP-10) | Registry allow-list: only `(type, version)` pairs registered at compile time are deserialisable. No polymorphic `$type` from the wire. Unknown types are parked unread. |
| **Elevation of privilege** | Over-privileged runtime credentials (v2 GAP-12) | `ProvisioningMode.Validate` in Production needs describe-only rights. The runtime database role has DML only on SourceFlow tables. |

**As built (WP-5.5 security review, [implementation/security-review.md](implementation/security-review.md)).**

- **Events in the database.** The append-only runtime role comes from `dotnet sourceflow db script --grants <role>` (PostgreSQL).
  - **Grants.** DML on the SourceFlow tables. `UPDATE`, `DELETE` and `TRUNCATE` are revoked on `events` and every partition, and default privileges keep new partitions append-only.
  - **Partitions.** Partition maintenance then runs as the owner (`db partitions ensure`).
  - **SQL Server.** The equivalent script is open (O-3).
- **Dead letters.** Replay, discard and skip record the operator: the CLI writes `by <user>@<host>`, and `IDeadLetterManager` writes the `IActorAccessor` actor.
- **Denial of service.**
  - **Breakers.** There are no Polly circuit breakers. Outbound calls rely on SDK retries, per-message relay backoff, retry budgets and parking.
  - **Body size.** Received bodies are bounded by the broker's limit, not by a per-type limit (3.x candidates O-4 and O-5).
  - **Poison messages.** A malformed CloudEvents attribute no longer stops a receiver (F-01, found by fuzzing).

## 2. Deserialisation hardening

- **Allow-list.** The registry is frozen at startup from generated registrations ([ADR-005](ADRs/ADR-005-logical-message-type-names-and-registry.md)). There is no `Type.GetType` on wire input.
- **JSON options.** Source-generated contexts only. `MaxDepth = 64`. Duplicate properties are disallowed. Strict number handling. Unmapped members are ignored for stored events (tolerant reader) and configurable for wire messages.
- **Limits.** Body size is checked against the registry and transport limits before parsing. Header count and size limits apply (doc 04 §2.3).

**As built (WP-5.5).**

- **Contracts.** Every parser now rejects malformed input only with its documented exception. The CloudEvents decoders throw `FormatException`; the generated id converters and the personal-data decoder throw `JsonException`.
- **Duplicate attributes.** Structured CloudEvents and the `sfheaders`/`sfext` objects are parsed with duplicate properties disallowed.

## 3. Payload protection (in transit and at rest in the broker)

```csharp
sf.Security(s =>
{
    s.UseAwsKms(k => k.KeyId = "alias/accounts-messages");          // or UseAzureKeyVault / UseGoogleKms
    s.Protect(p => p.Destinations("accounts.events", "payments"));  // policy: which destinations are encrypted
});
```

- **Envelope encryption.** A data encryption key (DEK) from `IKeyProvider.GenerateDataKey` is used with AES-256-GCM and a 96-bit random nonce. The associated data is `id|type|source`. The wrapped DEK, nonce and tag travel in the body. The `sfenc` header names the scheme and key reference.
- **DEK caching.** A DEK is reused for at most `MaxMessagesPerKey` (default 10,000) **and** at most `MaxKeyAge` (default 5 min), well inside AES-GCM's safe limit for random nonces. Keys are held in pinned arrays and zeroed when evicted.
- **Decryption** happens in the Receive-stage middleware. Failure is **permanent** and the message is parked with reason `DecryptionFailed`, never retried in a loop.
- **Key rotation** is transparent: the wrapped DEK names its key version, and the KMS handles it.
- **Outbox at rest.** The outbox stores the *protected* body, so pending messages aren't plaintext in the database either.

**As built (WP-5.1).**

- **API.** `sf.Security(s => s.UseAwsKms(…) | UseAzureKeyVault(…) | UseGoogleKms(…) | UseKeyProvider(…).Protect(p => p.Protect("accounts.events") | p.ProtectAll()))`. Protection without a key provider fails at startup.
- **Wire format.**
  - Body: `version(1) | wrapped DEK length(2) | wrapped DEK | nonce(12) | tag(16) | ciphertext`.
  - Header: `sfenc` = `sf-aes256gcm-v1;{provider};{key reference}`.
  - Associated data: `id|type|source`.
- **Outbox at rest.** A session decorator holds outbox rows for protected destinations until flush or commit. It then encrypts them and checks the size again, because the wrapped key adds bytes, and writes them in the same transaction. If the key service fails at that point, the command fails and nothing is committed.
- **Receive.** A built-in Receive-stage middleware decrypts the message.
  - **Permanent.** A tampered body, a tampered id, type or source, a foreign provider or a refused wrapped key fails with `DecryptionFailedException`. The message is parked as `DecryptionFailed` after one attempt.
  - **Transient.** A key service outage (`KeyProviderException` with `IsTransient`) is retried like any transient failure.
  - **Required protection.** `RequireOnReceive` parks unprotected messages on protected endpoints as `ProtectionRequired`, so a sender cannot downgrade to plaintext. It is off by default, so protection can be rolled out sender first.
- **Keys in memory.** DEKs live in pinned arrays.
  - A replaced DEK is zeroed after the next rotation, and every operation copies the key into its own `AesGcm` instance, so zeroing never races an operation in flight.
  - Unwrapped DEKs are cached, `UnwrappedKeyCacheSize` (256), so a receiver makes one key service call per DEK.
- **Providers.**

  | Provider | Generates the DEK | Key reference | Unwraps with |
  |----------|-------------------|---------------|--------------|
  | AWS KMS | `GenerateDataKey` (AES-256) | the key ARN | `Decrypt` |
  | Azure Key Vault | locally, then `wrapKey` (RSA-OAEP-256 by default) | the versioned key id, so DEKs wrapped before a rotation still unwrap | `unwrapKey` |
  | Google Cloud KMS | locally, then `Encrypt` | the crypto key; the ciphertext names the version | `Decrypt` |

  Each provider maps throttling and outages as transient, and refusals as permanent.
- **`LocalKeyProvider`.** Holds in-memory master keys with rotation, for tests and development.
- **Tests.**
  - **Unit:** UT-SEC-01..20.
  - **Providers:** UT-SEC-21..25. AWS KMS runs on LocalStack; Key Vault and Cloud KMS run through fakes of their SDK clients, since neither has an emulator.
  - **Real services:** the nightly `real-cloud` job runs `RealKmsTests` when `SOURCEFLOW_KMS_*` is set.
  - **End to end:** Accounts to Notifications and Loyalty, protected, in memory and on LocalStack with KMS (TCK-41 and TCK-43 with protection on).

## 4. Personal data in an immutable log (crypto-shredding)

An event log can't be edited, yet regulations such as GDPR Art. 17 require erasure. The standard answer is to encrypt personal data with a per-subject key and delete the key to erase it.

```csharp
[MessageType("bank.account.opened", 2)]
public sealed record AccountOpened(
    AccountId AccountId,
    [PersonalData(Subject = nameof(CustomerId))] string GivenName,
    [PersonalData(Subject = nameof(CustomerId))] string FamilyName,
    CustomerId CustomerId,
    decimal InitialDeposit,
    DateTimeOffset OpenedAt) : IEvent;
```

| Step | Behaviour |
|------|-----------|
| Write | The generated serialiser encrypts each `[PersonalData]` member with the subject's DEK (created on first use and stored wrapped by KMS in `data_keys`). The stored JSON is `"givenName": {"$pd": "v1:<keyRef>:<base64>"}`. |
| Read | DEKs are unwrapped through `IKeyProvider` with a bounded cache, and members are decrypted transparently. |
| Erase | `IPersonalDataEraser.EraseSubjectAsync(customerId)`: (1) marks `data_keys.shredded_at` and destroys the wrapped key; (2) appends a system event `$personal-data.erased {subject}`; (3) deletes snapshots of streams that reference the subject (tracked in `sourceflow.subject_streams`). |
| After erase | The members deserialise to `PersonalData.Erased` (for strings, the constant `"[erased]"`; for other types, the default value plus `IsErased` when the member type is `PersonalData<T>`). Projections receive `$personal-data.erased` and scrub their read models. Built-in document projections do it automatically for members marked `[PersonalData]`. |
| Integration events | They are a separate contract. The SF0013 analyzer discourages PII in them. When it can't be avoided, the receiving service must apply its own retention. |

The analyzer SF0011 makes sure every `[PersonalData]` member has a resolvable subject key. Aggregates whose *state* holds personal data serialise those members the same way in snapshots.

**As built (WP-5.2).**

- **Opt-in.** Encryption is enabled with `sf.Security(s => s.UseKeyProvider(…).EncryptPersonalData())`. Without it, `[PersonalData]` only classifies members for redaction and query policy, and events are stored as they were.
- **Subject.** `[PersonalData(Subject = nameof(CustomerId))]` names the subject member. It is looked up in the member's own object first, then in the enclosing objects. Without `Subject`, the event's stream is the subject, so the aggregate's personal data can be erased by stream id.
- **Write.**
  - **Format.** After serialization, each member's JSON value is encrypted with AES-256-GCM under the subject's DEK. Associated data: `subject|path`. Marker: `{"$pd":"v1:{subject (base64url)}:{kind}:{nonce|tag|ciphertext}"}`.
  - **Keys.** A subject's DEK is created on first use, wrapped by the key provider, and stored in `data_keys`.
  - **Streams.** The streams holding the subject's data are recorded in `subject_streams` before the commit.
  - **Snapshots.** Aggregate state is encrypted the same way.
  - **Integration events.** An integration event mapped with `.As(…)` is a separate contract. `.AsIs()` publishes the domain event's plaintext, which SF0013 warns about.
- **Read.** The aggregate store (events and snapshots), subscriptions, `IAggregateReader` and the SFQL aggregate fold decrypt markers before deserializing. Unwrapped keys are cached in pinned memory, and an `AesGcm` copy is made per operation.
- **Erase.** `IPersonalDataEraser.EraseSubjectAsync(subject)` runs four steps:
  1. Shreds the key: the wrapped key is destroyed and `shredded_at` is set.
  2. Deletes the snapshots of the subject's streams and evicts them from the aggregate cache.
  3. Scrubs the `[PersonalData]` members of document projections keyed by those streams' aggregates.
  4. Appends `PersonalDataErased` (type `sourceflow.personal-data.erased`) on stream `personal-data-{subject}`. Type names cannot contain `$`.
- **After erase.** Markers read as `"[erased]"` for strings, 0 for numbers, false for booleans, and null otherwise.
  - New personal data written for an erased subject is stored as the erased value, and no key is made.
  - A marker that was altered, or moved to another member, fails with `DecryptionFailedException`.
- **Analyzers.**
  - SF0011 (error): a `Subject` that names no member of the event.
  - SF0013 (warning): `.AsIs()` on an event with `[PersonalData]`. Test projects suppress it.
  - SF0017 (warning): `[Queryable]` together with `[PersonalData]`.
- **Not decrypted: SFQL event fields.** SFQL `from events` returns personal data fields as stored, so they are ciphertext markers when encryption is on. Read models hold decrypted values and are scrubbed on erasure.
- **Not decrypted: custom read models.** Custom (session) projections must handle `PersonalDataErased` themselves.
- **Tests.**
  - **Core:** UT-PII-01..18 and 24, in memory.
  - **Storage contract:** UT-PII-19/20 on PostgreSQL and SQL Server (`PersonalDataKeyStoreConformance`).
  - **Analyzers:** UT-PII-21..23.
  - **End to end:** E2E-29..31 on PostgreSQL, plus the same scenario on SQL Server.

## 5. Redaction in logs and telemetry

- `Microsoft.Extensions.Compliance.Redaction` is wired to a SourceFlow taxonomy (`SourceFlowTaxonomy.PersonalData`, `SourceFlowTaxonomy.Secret`). `[PersonalData]` implies the PersonalData classification.
- The default redactor in Production is `ErasingRedactor`, and in Development it is HMAC (correlatable but not reversible). This is configurable.
- Spans and metrics never carry payload values (doc 08 §2.1). Log payloads are off by default (doc 08 §4).

## 6. Identity and secrets

| Resource | Recommended identity | Not supported or discouraged |
|----------|----------------------|------------------------------|
| AWS | IAM roles (IRSA, ECS task role) through the SDK credential chain | Access keys in options (no option exists) |
| Azure | `DefaultAzureCredential` / managed identity | Connection strings outside Development (warning) |
| GCP | Application Default Credentials / Workload Identity | Key files in configuration |
| Databases | Managed identity tokens (Entra ID for SQL Server, IAM auth for RDS/Cloud SQL) through `NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider`, or a secret store | Plaintext passwords in `appsettings` |

## 7. Supply chain and secure development

- **Builds**: deterministic builds, SourceLink, embedded PDBs, NuGet **author signing**, and a CycloneDX SBOM attached to every release.
- **Scanning**: CodeQL (already in v2 CI), dependency review on PRs, Renovate for updates, and an OpenSSF Scorecard target of ≥ 7.
- **Policy**: the [SECURITY.md](../../SECURITY.md) disclosure process carries over. Security fixes are backported to the latest 3.x minor and, for 12 months after 3.0 GA, to v2.
- **Fuzzing**: SharpFuzz harnesses for the envelope parsers (CloudEvents binary and structured), the registry lookup, and the protected-payload decoder run nightly.

**As built (WP-5.5).**

- **Builds.** Deterministic CI builds with SourceLink. Symbols ship as `snupkg` rather than embedded PDBs.
- **Release pipeline** (`v3-release.yml`):
  - one CycloneDX SBOM per package, attached to each GitHub release;
  - NuGet author signing with a timestamp, required for release tags;
  - Sigstore provenance attestations;
  - nuget.org publishing behind the protected `nuget-release` environment.
- **Scanning.** Dependency review runs in `v3-pr.yml`, Renovate is configured in `renovate.json`, and the OpenSSF Scorecard runs in `v3-scorecard.yml`.
- **Fuzzing.**
  - **Targets.** `fuzz/SourceFlow.Fuzz` covers ten targets: the CloudEvents binary and structured parsers, the AWS, Google Cloud and Azure wire decoders with the v2 bridge, the registry lookup, the protected-payload decoder, personal-data markers, and both SFQL parsers with the request guard.
  - **Schedule.** Coverage-guided libFuzzer runs weekly for 2 hours per target (`v3-weekly.yml`) rather than nightly. Every PR runs the seeds and 25,000 seeded mutations per target.
  - **Results.** Fuzzing found F-01..F-04, which are fixed with regression tests.

## 8. Compliance mapping

| Obligation | Mechanism |
|------------|-----------|
| GDPR Art. 17 (erasure) | Crypto-shredding plus read-model scrubbing (§4) |
| GDPR Art. 30 / audit trails | Immutable events with actor, causation and correlation metadata |
| Data minimisation across services | Integration events as explicit contracts without PII (SF0013) |
| Encryption at rest and in transit | TLS, broker-side encryption (SSE-SQS/KMS, ASB, Pub/Sub CMEK) plus SourceFlow payload protection |
| Least privilege | Validate-only provisioning and DML-only runtime database roles |
