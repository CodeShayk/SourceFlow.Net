# Fuzzing

`SourceFlow.Fuzz` fuzzes every parser that reads untrusted bytes (doc 10 §7, WP-5.5). Each target lists the
exceptions its parser's contract allows for malformed input. Any other exception, a hang or a crash is a finding.

| Target | Parser | Allowed |
|--------|--------|---------|
| `cloudevents-binary` | `CloudEvents.FromAttributes` (binary mode) | `FormatException` |
| `cloudevents-structured` | `CloudEvents.FromStructured` | `FormatException` |
| `aws-wire` | `AwsWire.Decode`, then the v2 bridge (`AwsV2Bridge.TryRead`, `V2MessageMap.Translate`) | `FormatException` |
| `gcp-wire` | `GcpWire.Decode` | `FormatException` |
| `azure-wire` | `AzureWire.Decode` | `FormatException` |
| `registry` | Registry allow-list lookup, then deserialization of the registered type | `JsonException` |
| `protected-payload` | `PayloadProtector.UnprotectAsync` | `DecryptionFailedException` |
| `personal-data` | `PersonalDataProtector.UnprotectAsync` (`$pd` markers) | `DecryptionFailedException`, `JsonException` |
| `sfql`, `sql` | `SfqlParser.Parse`: request guard, lexer and parser, both syntaxes | none (errors are diagnostics) |

## Input formats

- **Attribute-based targets.** The input is `name=value` lines, an empty line, then the body.
- **`registry`.** The first line is `{type}.v{version}`; the rest is the JSON body.
- **`protected-payload`.** The first line is the `sfenc` header; the rest is the protected body.
- **The other targets.** The input is the raw document or query.

The seeds are real messages, built with the real encoders.

## Running

From the repository root:

```shell
dotnet run -c Release --project fuzz/SourceFlow.Fuzz -- list
dotnet run -c Release --project fuzz/SourceFlow.Fuzz -- mutate aws-wire 300      # in-process, 5 minutes
dotnet run -c Release --project fuzz/SourceFlow.Fuzz -- corpus corpus           # seeds and libFuzzer dictionaries
```

The modes:

- **`mutate`.** A coverage-blind mutation fuzzer that needs no tooling. A finding is saved under `findings/`, and the
  input is printed as base64.
- **On every PR.** `tests/Fuzz/SourceFlow.Fuzz.Tests` runs each target's seeds and 25,000 seeded mutations.
- **Coverage-guided.** This is libFuzzer through SharpFuzz. `.github/workflows/v3-weekly.yml` runs it for 2 hours per
  target. It builds `libfuzzer-dotnet` from source at a pinned release, instruments the `SourceFlow.*` and `Bank.*`
  assemblies with `sharpfuzz`, and runs each target with the target name in `SOURCEFLOW_FUZZ_TARGET`. Findings are
  uploaded as artifacts.

To reproduce a finding, pass it to the libFuzzer driver as the only input (libFuzzer runs it once), or turn it into a
regression test in `tests/Core/SourceFlow.Core.Tests/Security/FuzzRegressionTests.cs`.

## Findings so far

The first runs (WP-5.5) found five ways malformed input escaped a parser's contract. Each is fixed, with a regression
test:

| Finding | Effect | Fix | Test |
|---------|--------|-----|------|
| Malformed JSON in `ce_sfheaders` / `ce_sfext` threw `JsonException` from `CloudEvents.FromAttributes` | The receivers only catch `FormatException`, so one poison message made every receive that returned it throw | `FromAttributes` and `FromStructured` report all malformed input as `FormatException` | UT-FUZZ-01, UT-FUZZ-02, UT-AWS-34 |
| An overflowing `sfstreamversion` threw `OverflowException` | As above | As above | UT-FUZZ-01 |
| A malformed id in a message body threw `FormatException` from the generated converter | Parked as `PermanentFailure` instead of `DeserializationFailed` | Generated converters throw `JsonException` | UT-FUZZ-03 |
| Invalid UTF-8 in a body with personal data threw `InvalidOperationException` | Unknown exceptions are classified as transient, so the read was retried and the message then parked as `Exhausted`, instead of failing at once | UTF-8 is validated first (`JsonException`) | UT-FUZZ-04 |
| A lone-surrogate escape (`\ud800`) in such a body threw `InvalidOperationException` | As above | Mapped to `JsonException` | UT-FUZZ-04 |
