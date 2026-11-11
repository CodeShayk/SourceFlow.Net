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

## 2. Deserialisation hardening

- **Allow-list.** The registry is frozen at startup from generated registrations ([ADR-005](ADRs/ADR-005-logical-message-type-names-and-registry.md)). There is no `Type.GetType` on wire input.
- **JSON options.** Source-generated contexts only. `MaxDepth = 64`. Duplicate properties are disallowed. Strict number handling. Unmapped members are ignored for stored events (tolerant reader) and configurable for wire messages.
- **Limits.** Body size is checked against the registry and transport limits before parsing. Header count and size limits apply (doc 04 §2.3).

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

## 8. Compliance mapping

| Obligation | Mechanism |
|------------|-----------|
| GDPR Art. 17 (erasure) | Crypto-shredding plus read-model scrubbing (§4) |
| GDPR Art. 30 / audit trails | Immutable events with actor, causation and correlation metadata |
| Data minimisation across services | Integration events as explicit contracts without PII (SF0013) |
| Encryption at rest and in transit | TLS, broker-side encryption (SSE-SQS/KMS, ASB, Pub/Sub CMEK) plus SourceFlow payload protection |
| Least privilege | Validate-only provisioning and DML-only runtime database roles |
