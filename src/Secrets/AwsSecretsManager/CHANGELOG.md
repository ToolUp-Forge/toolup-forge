# Changelog — ToolUp.Secrets.AwsSecretsManager

All notable changes to the `ToolUp.Secrets.AwsSecretsManager` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

### Fixed

- Vendor exceptions arriving wrapped in `AggregateException` are now
  matched through the wrapper (flatten + inner-exception match). The
  direct `:? ResourceNotFoundException` / `:? InvalidRequestException`
  tests never fired (found by the first armed cloud-parity run,
  2026-08-27), so: `SetSecret` could not create a secret that did not
  already exist (the `CreateSecret` fallback was dead), `GetSecret` on a
  missing key threw instead of returning `None`, and `DeleteSecret` on a
  missing key returned `Error` instead of being idempotent. Error
  messages from generic handlers now carry the inner exception's message
  rather than "One or more errors occurred (…)".

## [0.1.2] - 2026-05-11

- Initial release. Phase 2a: AWS Secrets Manager `ISecretStore`
  companion. Introduced after the 0.1.0 public release; first ships at
  the coordinated 0.1.2 SDK line.
