# Security and Compliance Guide

This guide shows how to use the security features of SourceFlow.Net v3.0.0 as they are built:

- classifying personal data and secrets;
- encrypting personal data per subject, and erasing it (crypto-shredding);
- encrypting message payloads in the outbox and on the wire;
- the key providers for AWS KMS, Azure Key Vault, Google Cloud KMS and local development;
- redaction in logs;
- transport, database and query API security;
- secrets, audit and the supply chain.

It ends with the threat model, a compliance mapping and a production checklist.

For how the pieces fit together inside the runtime, read
[Architecture: Security and multi-tenancy](Architecture/10-Security-and-Multi-Tenancy.md). For the reasoning behind the
design, read [design doc 10](../Design/10-Security-and-Compliance.md) and the
[WP-5.5 security review](../implementation/security-review.md).

## Contents

1. [What is in 3.0](#1-what-is-in-30)
2. [A secure configuration at a glance](#2-a-secure-configuration-at-a-glance)
3. [Classifying data](#3-classifying-data)
4. [Personal data and the right to erasure (crypto-shredding)](#4-personal-data-and-the-right-to-erasure-crypto-shredding)
5. [Payload protection](#5-payload-protection)
6. [Key providers](#6-key-providers)
7. [Redaction in logs](#7-redaction-in-logs)
8. [Message integrity and deserialisation hardening](#8-message-integrity-and-deserialisation-hardening)
9. [Transport security and cloud identities](#9-transport-security-and-cloud-identities)
10. [Database security](#10-database-security)
11. [Query API security](#11-query-api-security)
12. [Audit: who did what](#12-audit-who-did-what)
13. [Secrets handling](#13-secrets-handling)
14. [Supply chain](#14-supply-chain)
15. [Threat model summary](#15-threat-model-summary)
16. [Compliance mapping](#16-compliance-mapping)
17. [Production checklist](#17-production-checklist)
18. [Known limitations and open items](#18-known-limitations-and-open-items)

## 1. What is in 3.0

| Control | Built in 3.0 | API or tool | Package |
|---------|--------------|-------------|---------|
| Personal data classification | Yes | `[PersonalData]`, `[Secret]`, `SourceFlowTaxonomy` | `SourceFlow.Net` |
| Per-subject encryption of personal data in events and snapshots | Yes, opt in | `sf.Security(s => s.EncryptPersonalData())` | `SourceFlow.Net` + a storage adapter |
| Erasure by key destruction (crypto-shredding) | Yes | `IPersonalDataEraser.EraseSubjectAsync` | `SourceFlow.Net` |
| Payload encryption in the outbox and on the wire | Yes, opt in | `sf.Security(s => s.Protect(...))` | `SourceFlow.Net` |
| Key providers | AWS KMS, Azure Key Vault, Google Cloud KMS, local (development) | `UseAwsKms`, `UseAzureKeyVault`, `UseGoogleKms`, `UseKeyProvider` | `SourceFlow.Cloud.*`, `SourceFlow.Net` |
| Redaction of classified members in logs | Yes | `sf.UseRedaction(...)`, `SourceFlowOptions.LogPayloads` | `SourceFlow.Net` |
| Message type allow-list | Always on | Generated registry | `SourceFlow.Net` |
| Envelope signature (`sfsig`) | **No** (3.x candidate) | — | — |
| Append-only runtime database role | PostgreSQL | `dotnet sourceflow db script --grants <role>` | `SourceFlow.Tool` |
| Tenant isolation with row-level security | PostgreSQL | `TenantIsolation.RowLevelSecurity` | `SourceFlow.Storage.PostgreSQL` |
| Query API authorization, permissions, PII redaction | Yes, fails closed | `AddSourceFlowQueryApi`, `QueryPermission` | `SourceFlow.AspNetCore` |
| Actor on every event and on dead-letter operations | Yes | `IActorAccessor` | `SourceFlow.Net` |
| Signed packages, SBOMs, provenance | Release pipeline | `v3-release.yml` | — |

Everything that encrypts is off until you configure it. Classification and redaction work without any key provider.

## 2. A secure configuration at a glance

A service on PostgreSQL with Azure Service Bus, Key Vault, personal data encryption, payload protection, tenant isolation,
an actor and a tenant from the authenticated user, and a query API behind a policy:

```csharp
using SourceFlow.AspNetCore;
using SourceFlow.Cloud.Azure;
using SourceFlow.Hosting;
using SourceFlow.Runtime;
using SourceFlow.Storage.PostgreSQL;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ITenantResolver, ClaimsTenantResolver>();
builder.Services.AddSingleton<IActorAccessor, ClaimsActorAccessor>();

builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "accounts";
    sf.AddModule<AccountsModule>();

    sf.UsePostgreSql(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("SourceFlow");          // DML-only runtime role
        o.SystemConnectionString = builder.Configuration.GetConnectionString("SourceFlowSystem"); // BYPASSRLS role
        o.TenantIsolation = TenantIsolation.RowLevelSecurity;
    });

    sf.UseAzureTransport(o => o.FullyQualifiedNamespace = "contoso.servicebus.windows.net");  // DefaultAzureCredential

    sf.Security(s => s
        .UseAzureKeyVault(k => k.KeyId = new Uri("https://contoso-kv.vault.azure.net/keys/accounts"))
        .EncryptPersonalData()
        .Protect(p =>
        {
            p.Protect("accounts.events");
            p.RequireOnReceive = true;   // once every sender protects
        }));
});

builder.Services.AddAuthorization(o => o.AddPolicy("sourceflow-query", p => p.RequireAuthenticatedUser()));
builder.Services.AddSourceFlowQueryApi();
builder.Services.AddHealthChecks().AddSourceFlow();

WebApplication app = builder.Build();
app.MapSourceFlowQueryApi().RequireAuthorization("sourceflow-query");
app.MapDomainQueries().RequireAuthorization("sourceflow-query");
app.Run();

sealed class ClaimsTenantResolver(IHttpContextAccessor http) : ITenantResolver
{
    public string? TenantId => http.HttpContext?.User.FindFirst("tenant")?.Value;
}

sealed class ClaimsActorAccessor(IHttpContextAccessor http) : IActorAccessor
{
    public string? Actor => http.HttpContext?.User.Identity?.Name is { } name ? "user:" + name : null;
}
```

Each part is explained below. `AccountsModule` stands for your generated module.

## 3. Classifying data

Two attributes in the `SourceFlow` namespace classify members. Both are
`Microsoft.Extensions.Compliance.Classification.DataClassificationAttribute`s in the `SourceFlow` taxonomy
(`SourceFlowTaxonomy.TaxonomyName = "SourceFlow"`):

| Attribute | Classification | Effect |
|-----------|----------------|--------|
| `[PersonalData]` | `SourceFlowTaxonomy.PersonalData` | Redacted in logs. Encrypted per subject when `EncryptPersonalData()` is on. Never a filter, sort, group or join key in SFQL (SFQ0301). Redacted in query results without the `sourceflow.query.pii` permission. |
| `[Secret]` | `SourceFlowTaxonomy.Secret` | Redacted in logs. |

On positional records, target the property, as the Bank sample does:

```csharp
[MessageType("bank.account.opened")]
public sealed record AccountOpened(
    AccountId AccountId,
    [property: PersonalData] string Holder,
    decimal InitialDeposit,
    DateTimeOffset OpenedAt) : IEvent;
```

### Subjects

A subject is whoever the data is about, for example a customer. Erasure works per subject.

- `[PersonalData(Subject = nameof(CustomerId))]` names the member that holds the subject id. It is looked up in the
  member's own object first, then in the enclosing objects, so a nested address can point at the customer id on the event.
- Without `Subject`, the event's **stream** is the subject. That suits data that belongs to one aggregate: erase it by
  stream id.

```csharp
public sealed record PatientAddress([property: PersonalData(Subject = "Customer")] string Street, string City);

[MessageType("clinic.patient.registered")]
public sealed record PatientRegistered(
    PatientId PatientId,
    string Customer,
    [property: PersonalData(Subject = nameof(PatientRegistered.Customer))] string Name,
    [property: PersonalData(Subject = nameof(PatientRegistered.Customer))] int Age,
    PatientAddress Home) : IEvent;
```

The subject id itself (`Customer` above) is stored in clear text, so it can be found again for erasure. Use an opaque id
for it, never a name or an e-mail address.

### Analyzers

| Id | Severity | Rule |
|----|----------|------|
| SF0011 | Error | `[PersonalData(Subject = …)]` names no member of the event. |
| SF0013 | Warning | `.AsIs()` publishes a domain event that has `[PersonalData]` members as an integration event. |
| SF0017 | Warning | `[Queryable]` on a `[PersonalData]` member has no effect: encrypted values can't be indexed. |

## 4. Personal data and the right to erasure (crypto-shredding)

An event log is immutable, but GDPR Article 17 requires erasure. SourceFlow encrypts each subject's personal data with a
key of its own and erases the subject by destroying that key.

### 4.1 Enable it

```csharp
sf.Security(s => s
    .UseAwsKms(k => k.KeyId = "alias/accounts-personal-data")
    .EncryptPersonalData());
```

Startup fails when:

- `EncryptPersonalData()` has no key provider ("Personal data encryption needs a key provider …");
- the storage adapter does not keep data keys (`IPersonalDataKeyStore`). The in-memory, PostgreSQL and SQL Server stores
  all do.

Without `EncryptPersonalData()`, `[PersonalData]` only classifies members (redaction and query policy) and events are
stored as they are.

### 4.2 What is stored

After the event is serialised, each `[PersonalData]` member's JSON value is encrypted with AES-256-GCM under the
tenant's data key for the subject, with `tenant|subject|path` as associated data, and replaced by a marker
(`v2:{tenant}:{subject}:{kind}:{payload}`, tenant and subject base64url-encoded; `LQ` is the default tenant `-`):

```json
{
  "patientId": "0193…",
  "customer": "cust-1",
  "name": { "$pd": "v2:LQ:Y3VzdC0x:s:…base64 nonce|tag|ciphertext…" },
  "age":  { "$pd": "v2:LQ:Y3VzdC0x:n:…" },
  "home": { "street": { "$pd": "v2:LQ:Y3VzdC0x:s:…" }, "city": "London" }
}
```

- **Keys.** A subject's data key belongs to its tenant: it is created on first use, wrapped by the key provider, and
  stored in `data_keys` under (tenant, subject). One key service call is made per new subject and tenant; unwrapped keys
  are cached in pinned memory. Markers of the earlier `v1` format (no tenant) still read, with the default tenant's key.
- **Where the subject's data lives.** The streams that hold a subject's data are recorded in `subject_streams` before the
  commit.
- **Snapshots.** Aggregate state that holds `[PersonalData]` members is encrypted the same way in snapshots.

### 4.3 Reading

Decryption is transparent wherever the runtime reads events or state:

- aggregate loads (events and snapshots) and the aggregate cache;
- subscriptions, so projections, event handlers and process managers see clear values;
- `IAggregateReader` and the SFQL aggregate fold.

Two places are deliberately **not** decrypted:

- **SFQL `from events`** returns personal data fields as stored, which is the `$pd` marker when encryption is on.
- **Read models** hold whatever the projection wrote. Built-in document projections store the decrypted values and are
  scrubbed on erasure (below). Custom projections must handle erasure themselves.

### 4.4 Erasing a subject

```csharp
using SourceFlow.Security;

public sealed class ErasureEndpoint(IPersonalDataEraser eraser)
{
    public async Task<ErasureResult> EraseAsync(string customerId, string? tenantId, CancellationToken ct)
        => await eraser.EraseSubjectAsync(customerId, tenantId, ct);
}
```

`EraseSubjectAsync(subjectId, tenantId = null, cancellationToken)` runs four steps. A null `tenantId` uses
`SourceFlowOptions.DefaultTenant`.

1. **Shred the key.** The wrapped key is destroyed and `shredded_at` is set. From now on every member encrypted for the
   subject reads as erased, wherever it is stored.
2. **Delete snapshots** of the subject's streams and evict their cached state, because both were built from decrypted data.
3. **Scrub documents.** The `[PersonalData]` members of built-in document projections keyed by those streams' aggregates
   are overwritten with erased values, in every live read-model generation.
4. **Notify.** A `PersonalDataErased` event (type `sourceflow.personal-data.erased`) is appended on stream
   `personal-data-{subject}`, so custom projections can scrub their own read models.

`ErasureResult` reports `SubjectId`, `Streams`, `SnapshotsDeleted` and `DocumentsScrubbed`.

### 4.5 After erasure

| Member type | Reads as |
|-------------|----------|
| `string` | `"[erased]"` (`PersonalDataProtector.Erased`) |
| number | `0` |
| `bool` | `false` |
| anything else | `null` |

- Personal data written later for an erased subject is stored as the erased value, and no new key is made.
- A marker that was altered, or copied to another member, fails with `DecryptionFailedException`.

### 4.6 Custom read models

Handle `PersonalDataErased` in any projection that copies personal data somewhere other than a built-in document
projection (EF Core tables, search indexes, files). The event carries only `Subject`.

### 4.7 Integration events

Integration events are separate contracts. Publish them with `.As(…)` and leave personal data out; the receiving service
can't erase what it was sent. `.AsIs()` on an event with `[PersonalData]` publishes the domain event's clear text and
raises SF0013.

### 4.8 Things to know

- **Key loss is data loss.** If the master key in your KMS is deleted, every subject's data becomes unreadable. Use the
  KMS's deletion protection and backups.
- **Erasure is per tenant.** Data keys are stored per tenant and subject id, so `EraseSubjectAsync(subject, tenantId)`
  erases the subject in that tenant only; the same subject id in another tenant keeps its own key. To erase a person
  everywhere, erase them in each tenant that holds their data. See
  [Architecture 10 §3.5](Architecture/10-Security-and-Multi-Tenancy.md#35-crypto-shredding-and-tenants).
- **Workflow state is not shredded.** Generated workflows keep their data as JSON; personal data fields in workflow state
  are not encrypted per subject in 3.0.
- **Imported v2 data** is encrypted like any append when `EncryptPersonalData()` is on
  ([migration guide](../guides/migrating-from-v2.md)).

## 5. Payload protection

Payload protection encrypts the bodies of messages sent or published to chosen destinations. It protects them **in the
outbox table** (so pending messages are not clear text in the database) and **on the wire and in the broker**. It is
independent of personal data encryption and can be used with or without it.

### 5.1 Enable it

```csharp
sf.Security(s => s
    .UseAwsKms(k => k.KeyId = "alias/accounts-messages")
    .Protect(p => p.Protect("accounts.events", "payments")));   // or p.ProtectAll()
```

Startup fails when protection has no key provider, or when it protects nothing.

### 5.2 Options (`PayloadProtectionOptions`)

| Option | Default | Meaning |
|--------|---------|---------|
| `Protect(params string[] destinations)` | — | Protects messages sent or published to these endpoints and topics. |
| `ProtectAll()` | — | Protects every message this service sends or publishes. |
| `MaxMessagesPerKey` | 10,000 | A data key encrypts at most this many messages (1 to 1,000,000). |
| `MaxKeyAge` | 5 minutes | A data key is used for at most this long (at most one day). |
| `UnwrappedKeyCacheSize` | 256 | Unwrapped data keys a receiver keeps, each saving one key service call per message. |
| `RequireOnReceive` | `false` | Parks unprotected messages that arrive on a protected destination as `ProtectionRequired`. |

### 5.3 How it works

- **Sending.** Outbox rows for protected destinations are held in the storage session until flush or commit, then
  encrypted with AES-256-GCM under a data key from the key provider, size-checked again (the wrapped key adds bytes), and
  written in the same transaction. If the key service fails at that moment, the command fails and nothing is committed.
- **Format.** The body becomes `version(1) | wrapped key length(2) | wrapped key | nonce(12) | tag(16) | ciphertext`. The
  associated data is `id|type|source`, so a ciphertext can't be replayed under another message's header. The `sfenc`
  CloudEvents attribute carries `sf-aes256gcm-v1;{provider};{key reference}`.
- **Receiving.** A built-in Receive-stage middleware decrypts before the message is dispatched:

  | Failure | Classified as | Result |
  |---------|---------------|--------|
  | Tampered body, id, type or source; unknown scheme or provider; wrapped key refused | Permanent (`DecryptionFailedException`) | Parked as `DecryptionFailed` after one attempt |
  | Key service throttled or down (`KeyProviderException.IsTransient`) | Transient | Retried like any transient failure |
  | Unprotected message on a protected destination with `RequireOnReceive` | Permanent (`ProtectionRequiredException`) | Parked as `ProtectionRequired` |

- **Key rotation** is transparent: each message names the key version that wrapped its data key, and the KMS unwraps it.
- **Keys in memory** live in pinned arrays and are zeroed when replaced; each operation copies the key into its own
  `AesGcm` instance.

### 5.4 Rolling it out

1. Deploy every receiver with the same key provider configured (receivers need `Unwrap` rights).
2. Turn protection on at the senders.
3. When every sender protects, set `RequireOnReceive = true` on the receivers so nobody can downgrade to plaintext.

### 5.5 Health

With protection on, the `sourceflow.keys` health check asks the key service for a data key at most once per
`SourceFlowOptions.Health.TransportCacheDuration` (default 30 seconds) and reports unhealthy when it is unavailable or
refuses. Register the checks with `builder.Services.AddHealthChecks().AddSourceFlow()`.

## 6. Key providers

A key provider implements `SourceFlow.Security.IKeyProvider`: it issues 256-bit data keys wrapped by a master key it
never reveals, and unwraps them again. One provider instance serves both payload protection and personal data.

| Provider | Registration | Generates the data key | Key reference | Unwraps with | `sfenc` name |
|----------|--------------|------------------------|---------------|--------------|--------------|
| AWS KMS | `s.UseAwsKms(k => …)` (`SourceFlow.Cloud.Aws`) | `GenerateDataKey` (AES-256) | the key ARN | `Decrypt` | `aws-kms` |
| Azure Key Vault | `s.UseAzureKeyVault(k => …)` (`SourceFlow.Cloud.Azure`) | locally, then `wrapKey` | the versioned key id | `unwrapKey` | `azure-keyvault` |
| Google Cloud KMS | `s.UseGoogleKms(k => …)` (`SourceFlow.Cloud.Gcp`) | locally, then `Encrypt` | the crypto key; the ciphertext names the version | `Decrypt` | `gcp-kms` |
| Local | `s.UseKeyProvider(new LocalKeyProvider())` (`SourceFlow.Net`) | locally, AES-256-GCM wrap | `{keyName}/v{n}` | in memory | `local` |
| Your own | `s.UseKeyProvider(sp => …)` | your implementation | — | — | your `Name` |

Every cloud provider maps throttling, timeouts and outages to a transient `KeyProviderException`, and refusals (access
denied, disabled or missing key, invalid wrapped key) to a permanent one.

### 6.1 AWS KMS

```csharp
sf.Security(s => s.UseAwsKms(k =>
{
    k.KeyId = "alias/accounts-messages";   // key id, ARN, alias name or alias ARN (required)
    k.Region = "eu-west-1";                 // optional; SDK resolution otherwise
}));
```

| `AwsKmsOptions` | Default | Meaning |
|-----------------|---------|---------|
| `KeyId` | required | The KMS key. |
| `Region` | SDK resolution | Region. |
| `Profile` | SDK credential chain | A named profile. |
| `ServiceUrl` | — | A custom endpoint, for example LocalStack `http://localhost:4566`. |
| `MaxSdkRetries` | 3 | SDK retries per call (0 to 10). |
| `Timeout` | 10 s | Timeout of one call. |

The options are validated when `UseAwsKms` runs, so a bad key id or region fails at startup. Grant the runtime identity
`kms:GenerateDataKey` and `kms:Decrypt` on the key.

### 6.2 Azure Key Vault

```csharp
sf.Security(s => s.UseAzureKeyVault(k =>
{
    k.KeyId = new Uri("https://contoso-kv.vault.azure.net/keys/accounts");   // required
    // k.Credential = new ManagedIdentityCredential();                        // default: DefaultAzureCredential
    // k.Algorithm = KeyWrapAlgorithm.A256KW;                                 // Managed HSM AES keys
}));
```

| `AzureKeyVaultOptions` | Default | Meaning |
|------------------------|---------|---------|
| `KeyId` | required | The key URI. Without a version, the current version wraps new data keys. |
| `Credential` | `DefaultAzureCredential` | The `TokenCredential`. |
| `Algorithm` | `KeyWrapAlgorithm.RsaOaep256` | The wrap algorithm (`A256KW` for Managed HSM AES keys). |

Data keys wrapped before a rotation still unwrap, because the stored reference is the versioned key id. Grant the
identity the wrap and unwrap key operations (for example the "Key Vault Crypto User" role).

### 6.3 Google Cloud KMS

```csharp
sf.Security(s => s.UseGoogleKms(k =>
    k.KeyName = "projects/acme/locations/europe-west1/keyRings/sourceflow/cryptoKeys/accounts"));
```

| `GoogleKmsOptions` | Default | Meaning |
|--------------------|---------|---------|
| `KeyName` | required | `projects/{project}/locations/{location}/keyRings/{ring}/cryptoKeys/{key}`; its primary version wraps new data keys. |
| `Credential` | Application Default Credentials | The `GoogleCredential`. |

Grant the identity encrypt and decrypt on the key (for example `roles/cloudkms.cryptoKeyEncrypterDecrypter`).

### 6.4 Local key provider (tests and development)

`LocalKeyProvider` keeps master keys in memory and wraps data keys with AES-256-GCM. `Rotate()` adds a master key
version; every version still unwraps. `Failure` makes every call fail, to simulate an outage. **Never use it in
production**: the master keys die with the process, and so does every encrypted value.

```csharp
using var keys = new LocalKeyProvider();
await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
{
    sf.ServiceName = "clinic";
    sf.AddModule<ClinicModule>();
    sf.Security(s => s.UseKeyProvider(keys).EncryptPersonalData());
});
```

### 6.5 Testing against real key services

The nightly `real-cloud` job runs `RealKmsTests` (`tests/Cloud/SourceFlow.Cloud.Tests/Security`) when these variables
name a key: `SOURCEFLOW_KMS_AWS_KEY`, `SOURCEFLOW_KMS_AZURE_KEY`, `SOURCEFLOW_KMS_GCP_KEY`. AWS KMS also runs on
LocalStack in every PR; Key Vault and Cloud KMS have no emulator and run through fakes of their SDK clients. See
[Cloud integration testing](Cloud-Integration-Testing.md).

## 7. Redaction in logs

SourceFlow wires `Microsoft.Extensions.Compliance.Redaction` to its taxonomy.

- **Default.** Without a call to `UseRedaction`, members classified `PersonalData` or `Secret` are erased (the
  `ErasingRedactor`), in every environment.
- **Payloads in logs are off.** `SourceFlowOptions.LogPayloads` is `false` by default; logs then show
  `[payload omitted]`. When on, parked message payloads are logged with every classified member passed through its
  redactor.
- **Spans and metrics** never carry payload values.

To make redacted values correlatable but not reversible, for example in a development or test environment, use the HMAC
redactor (marked experimental upstream, hence the pragma):

```csharp
sf.UseRedaction(r =>
{
#pragma warning disable EXTEXP0002
    r.SetHmacRedactor(o =>
    {
        o.Key = builder.Configuration["Redaction:HmacKey"];   // base64, from a secret store
        o.KeyId = 1;
    }, SourceFlowTaxonomy.PersonalData);
#pragma warning restore EXTEXP0002
});
sf.Configure(o => o.LogPayloads = true);
```

## 8. Message integrity and deserialisation hardening

- **No envelope signature in 3.0.** Broker identity and access control is the boundary against forged messages. An
  optional `sfsig` HMAC signature is a 3.x candidate.
- **Protected payloads are authenticated.** AES-GCM's tag detects any change to a protected body, and the associated data
  binds it to the message id, type and source (section 5).
- **Type allow-list.** Only `(type, version)` pairs registered at compile time can be deserialised. There is no
  `$type` on the wire and no `Type.GetType` on wire input; `Type.GetType`, `Activator.CreateInstance`, `Assembly.Load*`
  and `MakeGenericType` are banned APIs in every runtime project. Unknown types are parked unread.
- **JSON.** Source-generated contexts only, `MaxDepth = 64`, duplicate properties rejected (including in structured
  CloudEvents and the `sfheaders`/`sfext` objects), strict number handling.
- **Limits.** At most 8 user headers of 256 bytes each (`EnvelopeLimits`). Sent bodies are checked against the
  transport's size limit before they enter the outbox. Received bodies are bounded by the broker's own limit.
- **Poison input.** Every parser that reads untrusted bytes is fuzzed (section 14). Malformed CloudEvents attributes
  are reported as `FormatException` and parked, and never stop a receiver.

## 9. Transport security and cloud identities

- **TLS.** The AWS, Azure and Google Cloud SDKs connect over TLS. Use TLS for the database too (`SSL Mode=Require` or
  stricter for Npgsql, `Encrypt=True` for SqlClient).
- **No keys in options.** No transport option accepts an access key. Credentials come from the SDKs:

  | Cloud | Default credential | Notes |
  |-------|--------------------|-------|
  | AWS | the SDK credential chain (IAM roles such as IRSA or an ECS task role) | `Profile` selects a named profile; `ServiceUrl` targets LocalStack |
  | Azure | `DefaultAzureCredential` with `FullyQualifiedNamespace` | A `ConnectionString` is meant for Development and the emulator; outside Development it logs a warning |
  | Google Cloud | Application Default Credentials (Workload Identity) | `PUBSUB_EMULATOR_HOST` selects the emulator |

- **Least privilege for provisioning.** Outside Development, the default `ProvisioningMode` is `Validate`, so the
  runtime only needs describe rights. Create resources from infrastructure as code with
  `dotnet sourceflow topology export` and the modules in [`deploy/topology`](../../../deploy/topology). On AWS, the
  Terraform module's queue policy allows `sqs:SendMessage` only from SNS for the subscribed topics. On Azure and Google
  Cloud, namespace and project RBAC is the boundary.
- **Broker-side encryption.** SQS/SNS with KMS, Service Bus and Pub/Sub encrypt at rest. Payload protection adds
  encryption the broker operator can't read.

See [Cloud transports](Architecture/07-Cloud-Transports.md) and the per-cloud READMEs.

## 10. Database security

### 10.1 Two roles

| Role | Used by | Rights |
|------|---------|--------|
| Migration role (owner) | `dotnet sourceflow db migrate`, `db partitions ensure` | DDL on the SourceFlow schema |
| Runtime role | the service | DML only; `events` is append-only |
| System role (row-level security only) | subscriptions reading the global log, monitoring, cross-tenant SFQL | DML and `BYPASSRLS` |

`AutoMigrate` is on only in Development by default, so production services never need DDL rights.

### 10.2 The append-only runtime role (PostgreSQL)

```bash
dotnet sourceflow db script --schema sourceflow --grants sourceflow_app > grants.sql   # review, then run as the owner
```

The script grants DML on the SourceFlow tables, revokes `UPDATE`, `DELETE` and `TRUNCATE` on `events` and every events
partition, and sets default privileges so partitions created later are append-only too. Re-run it after every migration
that adds tables. With `PartitionEvents`, run `db partitions ensure` as the owner on a schedule, because the runtime role
can't create partitions.

SQL Server has no equivalent script in 3.0 (open item O-3). Deny `UPDATE` and `DELETE` on the `events` table to the
runtime role yourself.

### 10.3 Tenant isolation

Conjoined multi-tenancy (a `tenant_id` column on every tenant table) is always on. On PostgreSQL you can add row-level
security, so a query that forgets the tenant still sees only the session's tenant:

```csharp
sf.UsePostgreSql(o =>
{
    o.ConnectionString = runtimeConnection;
    o.SystemConnectionString = systemConnection;   // required: a BYPASSRLS role
    o.TenantIsolation = TenantIsolation.RowLevelSecurity;
    o.AutoMigrate = false;
});
```

```bash
dotnet sourceflow db migrate --connection "$OWNER_CONNECTION" --rls
```

The details (tenant resolution, envelope propagation, policies, the system role) are in
[Architecture 10 §4](Architecture/10-Security-and-Multi-Tenancy.md#4-conjoined-multi-tenancy). SQL Server isolates tenants
in the queries only (`TenantIsolation.Runtime` semantics); row-level security there is not in 3.0.

## 11. Query API security

The per-service query API (`SourceFlow.AspNetCore`) is never a SQL pass-through: queries are parsed against an
allow-list grammar, names come from the catalog, every value is a bound parameter, and execution uses a read-only
transaction with a statement timeout. See [Query side and SFQL](Architecture/08-Query-Side-and-SFQL.md) and
[the AspNetCore README](SourceFlow.AspNetCore-README.md).

### 11.1 Authorization fails closed

Every endpoint mapped by `MapSourceFlowQueryApi` or `MapDomainQueries` must carry an authorization decision, or the
application fails at startup with SFQ0504:

```csharp
builder.Services.AddSourceFlowQueryApi(o =>
{
    o.RequestsPerMinute = 60;          // per principal, SFQ0502 when exceeded
    o.ConcurrencyPerPrincipal = 4;     // SFQ0503
    o.ConcurrencyPerService = 32;      // SFQ0503 (503)
});

app.MapSourceFlowQueryApi().RequireAuthorization("sourceflow-query");
app.MapDomainQueries().RequireAuthorization("sourceflow-query");
```

`.AllowAnonymous()` is accepted as an explicit decision; the Bank sample uses it for its local demo only.

### 11.2 Permissions

By default, permissions are read from the `permission`, `permissions`, `scope` and `scp` claims (space-separated values are
split; only values starting with `sourceflow.query.` count). Replace `SourceFlowQueryApiOptions.Permissions` to map them
differently. In-process calls through `IQueryService` use `QueryPermissions.Service`, which holds every permission.

| `QueryPermission` | Value | Allows |
|-------------------|-------|--------|
| `Events` | `sourceflow.query.events` | `from events` and `from stream` |
| `ReadModels` | `sourceflow.query.readmodels` | read models |
| `Aggregates` | `sourceflow.query.aggregates` | aggregate state |
| `Schema` | `sourceflow.query.schema` | the catalog |
| `PersonalData` | `sourceflow.query.pii` | personal data in clear text in results |
| `Scan` | `sourceflow.query.scan` | `allow_scan` past the cost guard |
| `CrossTenant` | `sourceflow.query.cross-tenant` | `options (tenants = 'all')` |
| `Adhoc` | `sourceflow.query.adhoc` | ad-hoc queries through the API |
| `Validate` | `sourceflow.query.validate` | `/query/validate` |
| `Explain` | `sourceflow.query.explain` | `/query/explain` (shows the physical schema: operators only) |

A missing permission is SFQ0501.

### 11.3 Personal data in results

- A `[PersonalData]` field can't be a `where`, `order by`, group or join key (SFQ0301); `count(x)` of it is allowed.
- Without `sourceflow.query.pii`, personal data values in results come back as `"[redacted]"`.
- With it, read-model values come back as stored (decrypted), erased values as `"[erased]"`, and `from events` fields as
  `$pd` markers when encryption is on (section 4.3).

### 11.4 Tenants

The caller's tenant comes from `SourceFlowQueryApiOptions.Tenant`, which by default reads the `sourceflow:tenant` or
`tenant` claim; null falls back to the ambient tenant (`ITenantResolver`). The binder adds the tenant predicate to every
source. With PostgreSQL row-level security, the database enforces it again.

### 11.5 HTTP hardening

- Requests: 64 KB body (`MaxBodyBytes`, 413), JSON only (415), JSON depth 16 (`MaxJsonDepth`, 400).
- Errors are `application/problem+json` and never echo the input.
- Responses carry `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`,
  `Cache-Control: no-store` and `Cross-Origin-Resource-Policy: same-origin`.
- CSV output neutralises spreadsheet formulas.
- The nightly OWASP ZAP API scan of the Bank sample's query API fails on any medium or high finding.

## 12. Audit: who did what

- Every message carries `correlationId`, `causationId`, `source` and an **actor** (`sfactor`), which is copied into each
  event's metadata. The actor comes from the cause (for messages produced while handling another) or from
  `IActorAccessor` (for new commands). Register your own; the default returns null.
- Dead-letter replay, discard and skip record who acted: `IDeadLetterManager` writes `by <actor>` from `IActorAccessor`,
  and the CLI writes `by <user>@<host>`.
- v2 imports use the actor `sourceflow-migrate-v2`.

```csharp
public sealed class WorkloadActor : IActorAccessor
{
    public string? Actor => "svc:" + Environment.GetEnvironmentVariable("HOSTNAME");
}
```

## 13. Secrets handling

| Resource | Recommended | Not supported or discouraged |
|----------|-------------|------------------------------|
| AWS | IAM roles (IRSA, ECS task role) through the SDK credential chain | Access keys in options (no such option exists) |
| Azure | `DefaultAzureCredential` or a managed identity with `FullyQualifiedNamespace` | Connection strings outside Development (warning) |
| Google Cloud | Application Default Credentials or Workload Identity | Key files in configuration |
| PostgreSQL | IAM or Entra tokens through an `NpgsqlDataSource` with a periodic password provider, passed as `PostgreSqlStorageOptions.DataSource`; or a secret store | Plaintext passwords in `appsettings` |
| SQL Server | Entra ID authentication in the connection string, or a secret store | Plaintext passwords in `appsettings` |
| HMAC redaction key | a secret store | Source control |

In CI, the nightly real-cloud job authenticates by OIDC federation and stores no long-lived cloud secrets
([GitHub Actions setup](GitHub-Actions-Setup.md)).

## 14. Supply chain

| Control | As built |
|---------|----------|
| Deterministic builds and SourceLink | `Deterministic`, plus `ContinuousIntegrationBuild` and `EmbedUntrackedSources` in CI; `PublishRepositoryUrl` |
| Symbols | `.snupkg` symbol packages |
| NuGet author signing | `dotnet nuget sign` with a timestamp in `v3-release.yml`. Release tags fail without the certificate; alpha packages warn |
| SBOM | One CycloneDX JSON SBOM per package, attached to each GitHub release |
| Provenance | `actions/attest-build-provenance` (Sigstore, keyless) for every package |
| Publishing | nuget.org only from the protected `nuget-release` environment, which needs a release approver |
| CodeQL | `v3-codeql.yml`, `security-extended` queries |
| Dependency review | Every PR; fails on moderate or worse |
| Updates | Renovate (`renovate.json`): weekly grouped PRs into `v3.0.0`, 14-day minimum release age |
| OpenSSF Scorecard | `v3-scorecard.yml`, target score 7 or more |
| Fuzzing | `fuzz/SourceFlow.Fuzz`, ten targets. Every PR runs seeds plus 25,000 mutations per target; `v3-weekly.yml` runs libFuzzer for 2 hours per target |

To check a downloaded package:

```bash
dotnet nuget verify --all SourceFlow.Net.3.0.0.nupkg
gh attestation verify SourceFlow.Net.3.0.0.nupkg --repo CodeShayk/SourceFlow.Net
```

The pipeline is described in [GitHub Actions setup](GitHub-Actions-Setup.md#5-release-pipeline-v3-releaseyml).

## 15. Threat model summary

The assets are the domain events (the system of record), messages in flight, read models, encryption keys, and broker and
database credentials. Status is from the [WP-5.5 security review](../implementation/security-review.md).

| Threat (STRIDE) | Vector | Mitigation in 3.0 | Status |
|-----------------|--------|-------------------|--------|
| Spoofing | A forged message in a service's queue | Broker IAM; topology export with policies | Verified |
| Tampering | Message changed in transit or in the broker | TLS; AES-256-GCM with associated data `id\|type\|source` | Verified |
| Tampering | Events edited in the database | Append-only runtime role (`db script --grants`) | PostgreSQL fixed; SQL Server open (O-3) |
| Repudiation | "Who caused this change?" | Actor, causation, correlation and source on every event; audited dead-letter operations | Fixed |
| Information disclosure | Personal data in the log, logs, traces or broker | Crypto-shredding, redaction, SF0013, payload protection, no payloads in spans | Verified |
| Denial of service | Oversized or poison messages, retry storms | Broker size limits, `MaxDepth = 64`, bounded channels, retry budgets and parking; poison attributes parked (F-01) | Verified; no circuit breakers (F-07) and no per-type receive limits (F-08) |
| Tampering / disclosure | Injection through the query API | Allow-list grammar, catalog names, bound parameters, read-only transaction, row-level security, limits; DAST and fuzzing | Verified |
| Elevation of privilege | Wire messages instantiating arbitrary types | Registry allow-list, banned reflection APIs, unknown types parked unread | Verified |
| Elevation of privilege | Over-privileged runtime credentials | `Validate` provisioning outside Development; DML-only database role | Fixed (PostgreSQL) |

## 16. Compliance mapping

| Obligation | Mechanism |
|------------|-----------|
| GDPR Art. 17 (erasure) | Crypto-shredding plus read-model scrubbing and `PersonalDataErased` (section 4) |
| GDPR Art. 30 and audit trails | Immutable events with actor, causation and correlation metadata (section 12) |
| Data minimisation across services | Integration events as explicit contracts without personal data (SF0013) |
| Encryption at rest and in transit | TLS, broker-side encryption, payload protection, per-subject encryption |
| Least privilege | `Validate` provisioning, DML-only and append-only database roles, query permissions |

## 17. Production checklist

**Identity and secrets**

- [ ] No passwords or keys in configuration files; cloud SDKs use workload identities.
- [ ] Azure Service Bus uses `FullyQualifiedNamespace` and a credential, not a connection string.
- [ ] Database connections use TLS and a secret store or token authentication.

**Database**

- [ ] Migrations run as the owner (`dotnet sourceflow db migrate`); `AutoMigrate` is off outside Development.
- [ ] The service runs as a DML-only role; on PostgreSQL, apply `db script --grants <role>` after every migration.
- [ ] Multi-tenant on PostgreSQL: `TenantIsolation.RowLevelSecurity`, migrated with `--rls`, with a separate
      `BYPASSRLS` system role in `SystemConnectionString`.

**Personal data**

- [ ] Every member holding personal data is `[PersonalData]` with the right `Subject`; subject ids are opaque and unique
      across tenants.
- [ ] `EncryptPersonalData()` is on, with a cloud KMS key that has deletion protection and backups.
- [ ] An erasure path calls `IPersonalDataEraser.EraseSubjectAsync`, and every custom read model handles
      `PersonalDataErased`.
- [ ] Integration events are mapped with `.As(…)` and carry no personal data (no SF0013 warnings).

**Messages**

- [ ] Payload protection covers the destinations that carry sensitive data, and `RequireOnReceive` is on once all senders
      protect.
- [ ] The `sourceflow.keys` health check is part of readiness monitoring.
- [ ] Provisioning is `Validate` in production; resources come from `topology export` and infrastructure as code.

**Logs and queries**

- [ ] `LogPayloads` stays off, or the redactors are configured for production.
- [ ] The query API is mapped with `.RequireAuthorization(...)`; `sourceflow.query.pii`, `.cross-tenant`, `.scan` and
      `.explain` are granted only to the people who need them.
- [ ] `IActorAccessor` returns the authenticated user or workload.

**Supply chain**

- [ ] Packages are verified (`dotnet nuget verify`) and SBOMs are fed to your dependency scanner.

## 18. Known limitations and open items

| Item | Status in 3.0 |
|------|---------------|
| Envelope signatures (`sfsig`) | Not built; 3.x candidate |
| Circuit breaker on the outbox relay | Not built (F-07, O-4); SDK retries, relay back-off, retry budgets and parking instead |
| Per-type body size limits on receive | Not built (F-08, O-5); broker limits apply |
| SQL Server append-only grants script | Open (O-3) |
| SQL Server row-level security | Not in 3.0 |
| Periodic event checksum job | 3.x |
| Crypto-shredding of workflow state | Not in 3.0 |
| SFQL `from events` decrypting personal data | Not in 3.0: fields are returned as `$pd` markers |
| Release readiness | The first weekly 2-hour libFuzzer run (O-1), the signing certificate (O-2) and the review sign-off (O-6) are pending |

## Related documents

- [Architecture: Security and multi-tenancy](Architecture/10-Security-and-Multi-Tenancy.md)
- [Design doc 10: Security and compliance](../Design/10-Security-and-Compliance.md)
- [ADR-022: Conjoined multi-tenancy with row-level security](../ADRs/ADR-022-conjoined-multi-tenancy-with-row-level-security.md)
- [ADR-013: Use platform libraries](../ADRs/ADR-013-use-platform-libraries-delete-bespoke-equivalents.md)
- [ADR-026: Per-service query API](../ADRs/ADR-026-per-service-query-api-with-sql-syntax-and-injection-proof-compilation.md)
- [Security review (WP-5.5)](../implementation/security-review.md)
- [GitHub Actions setup](GitHub-Actions-Setup.md)
- [SourceFlow.Net README](SourceFlow.Net-README.md), [PostgreSQL storage](SourceFlow.Storage.PostgreSQL-README.md),
  [AspNetCore](SourceFlow.AspNetCore-README.md)
