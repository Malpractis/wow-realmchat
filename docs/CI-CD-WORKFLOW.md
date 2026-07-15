# CI/CD workflow

How to interact with this repo so the pipelines — and the self-updating
fleet behind them — keep working. Read [ARCHITECTURE.md](ARCHITECTURE.md)
first for what the release assets mean.

## The one thing to internalize

**Merging to main IS deploying.** Any push to main that touches `src/**`
(or `VERSION`, or `release.yml`) publishes a release, and every installed
client picks it up within 24 hours (daily task) or on next app open —
unattended, with no human in the loop after the merge. There is no staging
environment; the PR gate is the staging environment.

The corollary: `updaterVersion` is the git **tree hash of `src/RealmChat`**,
so the fleet updates exactly when something under that directory changes.
Tests (`tools/Tests`), the stub, and docs live outside it on purpose —
changing them never triggers a client update (and `tools/**`/`docs/**`
don't even run the release workflow).

## Normal change workflow

1. Branch from `main`, make the change, keep commits
   [Conventional](https://www.conventionalcommits.org/) (`type(scope):`).
2. Open a PR. `ci.yml` runs on `windows-latest`:
   - `dotnet build src/RealmChat` — same toolchain the release uses;
   - compiles `tools/OllamaStub` with the legacy Framework `csc`;
   - builds and **runs `tools/Tests`** (unit + signature fixtures + the
     full controller E2E against the stub). A red suite blocks the merge.
3. Merge. `release.yml` then, in order:
   - resolves the next tag from `VERSION` (auto-increments patch past any
     existing tag/release — two same-day merges can't collide);
   - builds the exe with `-p:UpdaterVersion=<tree hash>`;
   - **re-runs the whole test suite** (pushes to main can bypass PRs; the
     release must not);
   - writes `manifest.json`, hashes exe + manifest into `SHA256SUMS`, signs
     it with the `SIGNING_KEY` secret, and **self-verifies the signature
     against the committed `src/RealmChat/release-key.pub`** — a secret
     that fielded exes wouldn't trust fails the release here, loudly,
     instead of stranding the fleet;
   - publishes the release (exe, sums, sig, manifest);
   - commits the bumped `VERSION` back with `[skip ci]`.
4. Done. Do not create releases or tags by hand; the workflow owns them.

### Running the tests locally

```
dotnet build src/RealmChat -c Release -o out          # any .NET 8+ SDK
csc /out:out\ollama-stub.exe tools\OllamaStub\ollama-stub.cs
dotnet build tools/Tests -c Release -o out-tests
out-tests\RealmChat.Tests.exe                          # needs Windows (net48)
```

Optional env: `REALMCHAT_TEST_STUB` / `REALMCHAT_TEST_FIXTURES` (defaults
match the paths above, run from the repo root).

## Rules that keep the pipeline honest

- **Don't touch `src/RealmChat/` casually.** Any byte changed there rolls
  the fleet. Comment-only cleanups still ship an update; batch them with
  real changes.
- **Never edit `VERSION` by hand** except to deliberately bump major/minor
  (the workflow only auto-increments patch). The bump-back commit is the
  workflow's; leave it alone.
- **`manifest.json`, `SHA256SUMS`, `SHA256SUMS.sig`, `RealmChat.exe` are a
  compatibility contract.** Additive changes only — fielded exes must always
  be able to parse the current latest release.
- **The stub is the executable spec** of the Ollama API surface. If the app
  starts using a new endpoint or CLI verb, extend
  `tools/OllamaStub/ollama-stub.cs` in the same PR or the E2E fails.
- **The fixtures are signed bytes.** `tools/Tests/fixtures/` is protected by
  `.gitattributes` (`-text`); never let an editor or git setting normalize
  their line endings, and never regenerate them except during key rotation
  (below).
- **This repo is public and environment-blank.** No addresses, hostnames,
  subnets, or site names anywhere — code, docs, tests, commit messages.
  Site specifics belong only in the machine-local `config.json`.
- **Workflow edits**: `release.yml` is in its own `paths:` filter, so merging
  a change to it publishes a release even with no code change. Expected, but
  know it's coming. Keep third-party actions pinned by commit SHA.

## The signing key

| where | what |
|---|---|
| repo secret `SIGNING_KEY` | the RSA-3072 private key PEM; CI signs with it |
| `src/RealmChat/release-key.pub` | committed public key; CI's pre-publish self-verify |
| `src/RealmChat/ReleaseKey.cs` | the same key's modulus, pinned in the exe; clients verify with it |
| password manager | the offline home of the private key. It exists nowhere else — no key, no releases the fleet will accept |

Failure modes: missing/garbage secret → the signing step fails the release
(fleet unaffected, keeps last version). Secret is a *valid but different*
key → the self-verify step fails the release (same safe outcome). The fleet
can only be affected by a release signed with the real key.

### Key rotation (two-phase — order is everything)

Fielded exes verify with their pinned key until they self-update, and that
self-update is itself gated by the old key. Therefore:

1. Generate the new keypair offline. **Don't** touch the `SIGNING_KEY`
   secret yet.
2. Ship an exe that trusts the new key: update `ReleaseKey.cs` +
   `release-key.pub`, **re-sign the test fixtures with the new private
   key**, PR, merge. This release is still signed by the OLD key — every
   fielded exe accepts it.
   ```
   openssl dgst -sha256 -sign new-key.pem \
     -out tools/Tests/fixtures/SHA256SUMS.sig tools/Tests/fixtures/SHA256SUMS
   ```
3. Wait for the fleet to update (check the app footer / log on the host
   PC(s), or just give it >24 h).
4. Only now rotate the `SIGNING_KEY` secret to the new key. The next release
   is signed by it; the (updated) fleet accepts it.

Swapping steps 2 and 4 bricks self-update on every fielded exe: they'd
refuse the new signature forever and need a manual reinstall. If the private
key is ever **compromised**, that manual reinstall IS the recovery path —
rotate the secret immediately (halting the attacker's ability to publish
acceptable releases requires revoking their repo access too), ship a
new-key exe, and hand-install it on each host PC.

### Verifying a release by hand

```
gh release download --repo <this repo> -p 'RealmChat.exe' -p 'SHA256SUMS*' -p 'manifest.json'
sha256sum -c SHA256SUMS
openssl dgst -sha256 -verify src/RealmChat/release-key.pub \
  -signature SHA256SUMS.sig SHA256SUMS
```

## Troubleshooting the pipeline

| symptom | cause / fix |
|---|---|
| release run failed at "Manifest + signed checksums" | `SIGNING_KEY` secret missing or not a valid PEM — restore it from the password manager |
| release run failed at the openssl verify | secret doesn't match the committed pubkey — you're mid-rotation out of order; restore the old key or finish phase 1 first |
| release published but clients don't update | did `src/RealmChat/` actually change? Test/doc/tool-only merges don't bump `updaterVersion` (by design) |
| clients toast "updater needs attention" | 3+ failed checks — usually the release is missing an asset (all four are required) or was created by hand |
| CI green locally, red on the runner | line endings on fixtures (check `.gitattributes` survived), or the stub wasn't rebuilt after an API-surface change |
| two merges raced, second release has a higher patch than `VERSION` | normal — the tag-collision loop did its job; `VERSION` catches up via the bump-back commit |
