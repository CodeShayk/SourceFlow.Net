# Security Policy

## Supported versions

| Version | Supported with security fixes |
|---------|-------------------------------|
| 3.x | Yes: fixes go to the latest 3.x minor release (3.0 pre-releases: the latest alpha or release candidate) |
| 2.0.x | Yes, until 12 months after 3.0 is generally available |
| 1.x and earlier | No: upgrade to 2.0.x or 3.x |

## Reporting a vulnerability

Please do not report security vulnerabilities in public issues, discussions or pull requests.

Report them privately through GitHub: open the repository's **Security** tab and choose
**[Report a vulnerability](https://github.com/CodeShayk/SourceFlow.Net/security/advisories/new)**. Include:

- the affected package(s) and version(s);
- a description of the vulnerability and its impact;
- steps or a minimal project to reproduce it;
- any known workaround.

## What happens next

1. We acknowledge the report and may ask for more detail in the private advisory.
2. We confirm whether it is a vulnerability and assess its severity. If we decline it, we explain why.
3. We prepare a fix privately, release it for every supported version, and publish a GitHub Security Advisory (with a
   CVE when warranted) that credits you, unless you prefer otherwise.

Please keep the report confidential until the advisory is published.

## How releases are protected

- Packages are built deterministically in CI with SourceLink and author-signed for releases; each release carries a
  CycloneDX SBOM per package and a build provenance attestation.
- Pull requests run CodeQL, dependency review and the full test gate; dependencies are kept current by Renovate.

See the [security and compliance guide](docs/v3.0.0/Documentation/Security-and-Compliance-Guide.md) for how to run
SourceFlow.Net securely.
