# Woodpecker Linux CI operator runbook

**All installation, OAuth registration, secret creation, pulls, starts, stops, backup, restore, activation and webhook actions below are operator-only and were not executed by this task.** Local configuration validation is not evidence of an installed or working CI service.

## Prerequisites and security

Use a dedicated Linux x86_64 host with Docker Engine 24 or newer, the Compose plugin 2.20 or newer, Go 1.22 or later, OpenSSL, tar and curl. Use the [official Docker Ubuntu installation guide](https://docs.docker.com/engine/install/ubuntu/) if Docker is absent; do not alter an existing daemon as part of this repository change. Host clang and .NET are not required for container CI. GitHub, registries, `builds.dotnet.microsoft.com`, Ubuntu mirrors and `api.nuget.org` need outbound HTTPS; DNS, time, proxies and trusted CAs are operator responsibilities.

Docker-group membership and the agent's Docker socket access are root-equivalent. The server runs as UID 1000; the agent explicitly runs as root for the socket backend. No privileged container is needed. Workflow code is not safe against a malicious contributor merely because it runs in a container. Keep registration closed, restrict owners, review workflow changes, require deliberate approval of untrusted PRs and prefer a dedicated worker. Cohosting with Home Assistant or other workloads risks security and resource contention; consider another host before activation. Never enable trusted repository privileges to make a failure disappear.

The private control bridge carries plaintext gRPC only between server and agent; do not expose port 9000. Server egress is needed for GitHub. The agent starts sibling build containers via the socket. Its own service limit does not limit those siblings: each step is capped at 6 GiB memory and swap combined and approximately two CPUs; one workflow runs at a time. These are limits, not capacity guarantees; monitor the host. A native link can still exhaust the limit. The 30-minute default and maximum server timeouts should also be set as the repository timeout. Restart policy restarts failed processes, not unhealthy containers or failed jobs.

## OAuth, proxy and configuration

Create a GitHub **OAuth App**, not a GitHub App. Set its homepage to your real public HTTPS origin (no trailing slash) and callback to that origin plus `/authorize`. Use your actual GitHub login as `WOODPECKER_ADMIN`. An existing HTTPS reverse proxy must preserve Host, set trusted `X-Forwarded-Proto: https` and `X-Forwarded-For`, forward to `127.0.0.1:8000`, and handle streaming without buffering. Expose only HTTPS 443 externally. The operator owns DNS, certificates, firewall and proxy configuration.

From the repository root, copy the example and set the HTTPS origin, GitHub login and OAuth client ID. Set `WOODPECKER_AGENT_SECRET_GID` to your primary numeric GID (`id -g`) if it isn't `1000`.

```sh
cp infra/woodpecker/.env.example infra/woodpecker/.env
${EDITOR:-vi} infra/woodpecker/.env
```

With shell tracing disabled (`set +x`), create the secrets directory and token. Enter the OAuth client secret at the silent prompt; do not paste it into a command or shell history. The owner UID must be 1000 for the server. The agent drops all capabilities, so `agent-secret` must have mode `0640` and its group must match the agent's supplemental group `WOODPECKER_AGENT_SECRET_GID`. The operator's primary group lets the operator read both files for preflight. For a non-root operator whose UID is not 1000, transferring ownership removes their authority to change the file modes; group membership grants read access, not chmod authority. Both the ownership transfer and the subsequent chmod require operator-authorized sudo. If sudo is unavailable, obtain authorized administration before proceeding; preflight remains an ordinary operator command.

```sh
set +x
umask 077
mkdir -p infra/woodpecker/secrets
chmod 0700 infra/woodpecker/secrets
openssl rand -hex 32 > infra/woodpecker/secrets/agent-secret
read -r -s -p 'GitHub OAuth client secret: ' WOODPECKER_OAUTH_INPUT
printf '\n'
printf '%s' "$WOODPECKER_OAUTH_INPUT" > infra/woodpecker/secrets/github-secret
unset WOODPECKER_OAUTH_INPUT
sudo chown 1000:"$(id -g)" infra/woodpecker/secrets/github-secret infra/woodpecker/secrets/agent-secret
sudo chmod 0640 infra/woodpecker/secrets/github-secret infra/woodpecker/secrets/agent-secret
go run ./infra/woodpecker --env-file infra/woodpecker/.env
```

These are Compose file secrets, host bind files rather than encrypted Swarm secrets; Compose uid/gid declarations cannot fix host bind ownership. The operator-owned 0700 parent directory is not traversed by the server container because Compose binds the individual files. Do not rotate secrets during preflight or startup; rerun preflight after rotation. Never commit `.env`, secrets or backups. Preflight checks configuration and files only; it does not establish DNS, TLS, daemon/socket access, registry reachability, OAuth/forge membership, pre-existing volume ownership or daemon resource enforcement.

## Start and activate

Only after successful preflight, the operator can pull and start the stack:

```sh
docker compose --env-file infra/woodpecker/.env -f infra/woodpecker/compose.yaml pull
docker compose --env-file infra/woodpecker/.env -f infra/woodpecker/compose.yaml up -d --wait --wait-timeout 120
docker compose --env-file infra/woodpecker/.env -f infra/woodpecker/compose.yaml ps
docker compose --env-file infra/woodpecker/.env -f infra/woodpecker/compose.yaml logs --tail=100 woodpecker-server woodpecker-agent
```

Replace `REAL_ORIGIN` below with the configured real origin, for example `https://ci.your-domain.tld` without a trailing slash:

```sh
curl --fail --silent --show-error -o /dev/null -w '%{http_code}\n' REAL_ORIGIN/api/healthz
```

Expect HTTP 204: Woodpecker returns it after successfully pinging its database. The upstream v3.20.1 native server ping detects connection and timeout failures but has a defective bad-status predicate; check the HTTPS response directly. Also confirm the agent is online in the UI and execute a real pipeline.

Sign in as the configured admin. With GitHub repository-admin permission activate `konstitech/NETDeck`, confirm the configuration path `.woodpecker/`, set repository timeout to 30 minutes, and leave Trusted Network, Trusted Volumes and Trusted Security disabled. Set the require-approval-for-pull-requests policy before accepting untrusted PRs; check the exact UI wording for v3.20.1. Never automatically approve fork PRs. No workflow secrets are needed. Use the commit-correlated acceptance checklist below; a delivered webhook or successful unrelated build alone is insufficient.

Pushes, including merges and direct updates to `main`, and PRs to any target branch are eligible; branch filters would match PR target branches, and this workflow intentionally has none. Manual and tag events are excluded. Upstream case-insensitive `[CI SKIP]` and `[SKIP CI]` commit directives can skip execution. The server and agent must already be running, the repository activated, the workflow present in the pushed revision, and the webhook delivered. Queueing is not GitHub cancel-in-progress; native cancellation settings can cancel superseded runs. The workflow uses a pinned base and checksum-verified SDK, but Ubuntu packages are not pinned bit-for-bit. No persistent dependency cache is used; workspace and NuGet state are reused inside the single step.

## GitHub main merge delivery

A GitHub `push` to `refs/heads/main` goes through the registered native Woodpecker GitHub webhook, a public HTTPS reverse proxy, the loopback-bound Woodpecker server at `127.0.0.1:8000`, and an online local Docker agent, which runs the `.woodpecker/` configuration from the pushed commit. Merge-commit, squash-merge and rebase-merge each update main and produce a `push`; direct pushes qualify too. Closing a PR alone produces `pull_request_closed`, not a main build trigger. Other-branch pushes and pull requests to any target branch remain eligible; tag, manual and cron events are excluded.

The independent GitHub-hosted cross-platform workflow `.github/workflows/native-aot.yml` may also run for the same commit. GitHub Actions does not trigger Woodpecker. A PR run and a subsequent main push run are distinct.

## Administrator activation checklist

These are external administrator actions, not work performed by this repository change:

1. Provision a stable publicly reachable HTTPS origin, public DNS, a valid certificate and firewall/proxy routing on port 443. GitHub cannot deliver to `localhost` or a private LAN-only address. Supply the outbound HTTPS dependencies listed above; provision secure public ingress separately if needed.
2. Create a GitHub OAuth App, not a GitHub App. Set the homepage to `WOODPECKER_HOST` and the callback to that exact origin plus `/authorize`. Configure the actual GitHub login in `WOODPECKER_ADMIN`, keep registration closed and retain the `konstitech` owner restriction. Obtain organization OAuth restrictions/SSO authorization where applicable. The pinned OAuth integration requests `user:email`, `read:org` and `repo`; review and authorize those permissions.
3. Configure the ignored `.env` and file secrets as above, start the existing services as an authorized operator, and sign in with an account having GitHub repository-admin permission. Activate `konstitech/NETDeck` in Woodpecker. Activation ordinarily registers the native hook: inspect the existing hook, rather than adding a second hook. Confirm its redacted endpoint identity, `push` subscription, `form` content type and enabled TLS verification. Activation or repair can replace a matching same-host hook; review other hooks on that host before reactivation.
4. Confirm `.woodpecker/` as configuration path, repository timeout of **30 minutes**, and an online local agent. Set approval mode `pull_requests` before accepting untrusted PRs, and leave Trusted Network, Trusted Volumes and Trusted Security disabled. Approval, capacity, cancellation settings and skip directives can prevent or delay an eligible run.

The proxy must forward `/api/hook` POST unchanged, including the `access_token` query parameter, form payload and forge headers. Exempt only this native endpoint from interactive SSO/login challenges; do not redirect it, inject an Authorization header or disable security globally. Preserve native token validation, Host and trusted forwarded headers, and HTTPS certificate verification. Keep port 9000 private and server HTTP bound to `127.0.0.1:8000`.

## Pinned webhook authentication and secrets

The following describes Woodpecker **v3.20.1**, upstream commit `3f1bb0bd0db28e410ad797034d75780116122c91`, not every release. Woodpecker v3.20.1 does not configure a GitHub webhook HMAC secret or validate X-Hub-Signature-256. Its native GitHub hook uses a `form` payload and a URL such as `/api/hook?access_token=REDACTED` (this example is nonfunctional).

On activation, Woodpecker generates a repository secret from 32 random bytes, base32-encodes it, and uses it to sign an HS256 JWT containing forge and repository identity. The URL JWT is a reusable **bearer** credential with **no expiry** configured. The server verifies the token and matches the payload's repository identity, but the JWT does not sign the request body or cryptographically prove that a payload came from GitHub. Anyone who learns the URL token could replay it or forge a matching-repository payload. Verify the native hook and correlate accepted deliveries with known GitHub events and commit SHA; that is operational evidence, not payload authentication.

Use HTTPS, restrict GitHub repository-admin access, protect Woodpecker data and backups, and redact tokens at every proxy, access-log, monitoring and export boundary. Configure logs to record the path only, never the query string. Redact query credentials from delivery exports, screenshots and support reports; never log Authorization headers, cookies, full sensitive payloads or signature material. These mitigations do not replace a body signature.

| Item | Owner, storage and purpose |
| --- | --- |
| Public configuration | The ignored `infra/woodpecker/.env` holds `WOODPECKER_HOST`, `WOODPECKER_ADMIN`, `WOODPECKER_GITHUB_CLIENT` and the optional `WOODPECKER_AGENT_SECRET_GID` (default `1000`). These values are not credentials. |
| OAuth client secret | The administrator provisions host file `infra/woodpecker/secrets/github-secret`, mounted server-only at `/run/secrets/github-secret`, for OAuth exchange. |
| Agent shared secret | The operator generates `infra/woodpecker/secrets/agent-secret`, mounted on server and agent for agent authentication; do not reuse OAuth or hook secrets. |
| OAuth authorization and access-token state | Woodpecker persists authorization/access and refresh token state associated with the authorizing GitHub user in the `server-data` database. Treat that volume and backups as credential-bearing; revocation or organization authorization changes can break API, clone, status and hook administration. Do not assume database encryption. |
| Repository webhook secret and derived URL token | Woodpecker stores the repository secret as `repos.hash` in the server database and registers the derived URL token at GitHub. Protect server data and GitHub hook settings/delivery evidence. This is neither a workflow secret nor a manually entered GitHub HMAC secret. |

Reactivation can reuse a nonempty repository secret; it does not guarantee credential rotation and can replace a matching same-host hook. If exposure is suspected, stop rollout, restrict ingress if necessary, and obtain authorized version-specific credential invalidation and re-registration guidance. Never edit SQLite or regenerate unrelated secrets as a shortcut. No new GitHub Actions secret, Woodpecker pipeline secret, Woodpecker API token or PAT is needed for this native route; runtime clone authorization still depends on the managed OAuth state. Keep `set +x`, UID 1000, the existing secret-file permissions and ignored paths.

Pinned source evidence: [repository activation](https://github.com/woodpecker-ci/woodpecker/blob/3f1bb0bd0db28e410ad797034d75780116122c91/server/api/repo.go), [GitHub hook registration and parsing](https://github.com/woodpecker-ci/woodpecker/blob/3f1bb0bd0db28e410ad797034d75780116122c91/server/forge/github/github.go), [token handling](https://github.com/woodpecker-ci/woodpecker/blob/3f1bb0bd0db28e410ad797034d75780116122c91/shared/token/token.go), [hook endpoint](https://github.com/woodpecker-ci/woodpecker/blob/3f1bb0bd0db28e410ad797034d75780116122c91/server/api/hook.go), [GitHub event parsing](https://github.com/woodpecker-ci/woodpecker/blob/3f1bb0bd0db28e410ad797034d75780116122c91/server/forge/github/parse.go), [repository model](https://github.com/woodpecker-ci/woodpecker/blob/3f1bb0bd0db28e410ad797034d75780116122c91/server/model/repo.go), [user model](https://github.com/woodpecker-ci/woodpecker/blob/3f1bb0bd0db28e410ad797034d75780116122c91/server/model/user.go), [pipeline creation](https://github.com/woodpecker-ci/woodpecker/blob/3f1bb0bd0db28e410ad797034d75780116122c91/server/api/helper.go), and [server defaults](https://github.com/woodpecker-ci/woodpecker/blob/3f1bb0bd0db28e410ad797034d75780116122c91/cmd/server/flags.go).

## Main merge acceptance evidence

This is an **operator-only** live gate and was not executed by this repository work. Eligibility, delivery, scheduling and successful execution are separate facts.

1. After the external setup and activation above, export the actual configured origin as `WOODPECKER_HOST` in the operator shell and require HTTP 204:

   ```sh
   curl --fail --silent --show-error -o /dev/null -w '%{http_code}\n' "$WOODPECKER_HOST/api/healthz"
   ```

   Health or native ping alone does not establish webhook delivery or execution.
2. Inspect the existing GitHub hook without copying its bearer URL: confirm the configured origin, `/api/hook`, form content, `push` subscription and TLS verification. With administrator authorization, perform a normal merge into main and record its resulting main commit SHA, not merely the earlier PR head SHA.
3. In GitHub **Recent Deliveries**, record a sanitized delivery identifier and time, `push`, `refs/heads/main`, the resulting commit SHA and response status. Native accepted creation can return 200 or 202; 202 requires later pipeline correlation. A 204 can indicate ignored, inactive or filtered work, not completed CI. Delivery success alone is not execution evidence.
4. Correlate the Woodpecker run to the exact resulting commit SHA, event `push`, branch `main` and actual local agent. Require successful completion of the existing clone, build, full test, NativeAOT publish and ELF checks. A green GitHub Actions run, earlier PR run or queued job is not evidence of this result. Record `[CI SKIP]`/`[SKIP CI]`, approval waits or cancellation as exceptions, not success.
5. Keep only sanitized evidence. Authorized redelivery after a fix can create another run; neither exactly-once execution nor automatic replay after downtime is guaranteed.

## Webhook delivery troubleshooting

Inspect sanitized delivery status and server logs without exposing tokens, authorization headers, cookies, payloads or signatures. Do not reset the database or bypass TLS.

| Symptom | Cause and operator action |
| --- | --- |
| No registered hook | Check activation, repository-admin permission, OAuth and organization/SSO authorization, and native registration. Inspect both GitHub and Woodpecker after partial activation; do not add a duplicate hook or blindly reactivate a same-host hook. |
| Delivery connection or TLS failure | Check public DNS, certificate, firewall, reachability and proxy routing; retain TLS verification. A localhost or LAN-only endpoint cannot receive GitHub-hosted delivery. |
| Redirect, login challenge or proxy authorization failure | Correct the interactive-auth exception for the native hook route only; retain native token validation and do not disable security globally. |
| HTTP 400 / native rejection | Check the native endpoint, redacted query token configuration, payload format and repository identity. Do not diagnose a missing GitHub HMAC: this pin does not validate one. |
| 200, 202 or 204 but no run | Check ignored events such as deleted branches, inactive/ownerless repository, `Pipeline-Filtered: true`, workflow configuration at the resulting SHA, skip directives and sanitized server logs. A 202 can still fail during background creation; 204 is not build success. Native creation normally waits five seconds synchronously before 202, with a two-minute background creation bound. Other synchronous errors can return 400, 404 or 500. |
| Queued, blocked or canceled | Check whether the local agent is online or busy, the single workflow slot, required PR approval and native cancellation settings. |
| Failed run | Inspect the failing existing command, SDK checksum, restore, test or NativeAOT errors, timeout and OOM diagnostics; preserve strict checks. |

After correcting the cause, an authorized operator may redeliver; redelivery can create a duplicate run. There is no exactly-once or automatic replay guarantee.

## Consistent backup and restore

Treat both volumes, the `.env` file and secrets as sensitive. Stop accepting work, wait for or cancel active jobs in the UI, then stop both services before copying SQLite. The following operator-only commands use the pinned helper image and the exact named volumes; they may pull that image. They preserve numeric owners. `BACKUP_DIR` must be an absolute path to the ignored backup directory.

Use a real operator-owned `infra/woodpecker/backups` directory (not a symlink) beneath the operator-controlled checkout, on a filesystem enforcing Unix permission bits. Do not use a root-owned, shared or untrusted destination, or concurrently change or replace it, rotate source secrets, or run another backup or restore. If the directory is existing, the explicit `chmod 0700` repairs its traversal permissions before writing. On ownership or permission failure, stop and obtain authorized operator assistance; do not bypass the guard. The 0700 directory is necessary even with the host and each helper using umask 077: existing archive modes are not changed by umask, and helpers may create root-owned archives. New archives are at most 0600. A failed archive may leave a partial file and must not be regarded as a valid backup. This protection cannot revoke previously disclosed copies.

```sh
(
set -eu
umask 077
mkdir -p infra/woodpecker/backups
chmod 0700 infra/woodpecker/backups
BACKUP_DIR="$(cd infra/woodpecker/backups && pwd -P)"
docker compose --env-file infra/woodpecker/.env -f infra/woodpecker/compose.yaml stop woodpecker-agent woodpecker-server
docker run --rm --mount type=volume,src=netdeck-woodpecker_server-data,dst=/data,readonly --mount "type=bind,src=$BACKUP_DIR,dst=/backup" ubuntu:24.04@sha256:2198e4c65076e9cb0b3d695219df667c0e3b0072da64ca3c2648aca17dcecaf5 sh -eu -c 'umask 077; exec tar --numeric-owner -C /data -cpf /backup/server-data.tar .'
docker run --rm --mount type=volume,src=netdeck-woodpecker_agent-config,dst=/data,readonly --mount "type=bind,src=$BACKUP_DIR,dst=/backup" ubuntu:24.04@sha256:2198e4c65076e9cb0b3d695219df667c0e3b0072da64ca3c2648aca17dcecaf5 sh -eu -c 'umask 077; exec tar --numeric-owner -C /data -cpf /backup/agent-config.tar .'
tar --numeric-owner -C infra/woodpecker -cpf "$BACKUP_DIR/config-secrets.tar" .env secrets
)
```

Protect and encrypt backups, retain copies off-host and rehearse restore on an isolated host. Restore matching archives to the same named volumes while services remain stopped. The volume archives are trusted operator backups; extraction writes into existing volumes and does not delete files absent from the archive. Ensure the destination is the intended clean recovery volume, not an unrelated live volume. As during setup, a non-root operator with UID other than 1000 loses chmod authority after ownership transfer; group membership permits reading, not changing modes. Both chown and the following chmod require operator-authorized sudo; obtain authorized administration if sudo is unavailable, then run preflight as the ordinary operator.

Before restoring on a host with a different operator group, update the restored `.env` to match `id -g` before running preflight.

```sh
BACKUP_DIR="$(cd infra/woodpecker/backups && pwd -P)"
docker compose --env-file infra/woodpecker/.env -f infra/woodpecker/compose.yaml stop woodpecker-agent woodpecker-server
docker run --rm --mount type=volume,src=netdeck-woodpecker_server-data,dst=/data --mount "type=bind,src=$BACKUP_DIR,dst=/backup,readonly" ubuntu:24.04@sha256:2198e4c65076e9cb0b3d695219df667c0e3b0072da64ca3c2648aca17dcecaf5 tar --numeric-owner -C /data -xpf /backup/server-data.tar
docker run --rm --mount type=volume,src=netdeck-woodpecker_agent-config,dst=/data --mount "type=bind,src=$BACKUP_DIR,dst=/backup,readonly" ubuntu:24.04@sha256:2198e4c65076e9cb0b3d695219df667c0e3b0072da64ca3c2648aca17dcecaf5 tar --numeric-owner -C /data -xpf /backup/agent-config.tar
tar -C infra/woodpecker -xpf "$BACKUP_DIR/config-secrets.tar"
chmod 0700 infra/woodpecker/secrets
sudo chown 1000:"$(id -g)" infra/woodpecker/secrets/github-secret infra/woodpecker/secrets/agent-secret
sudo chmod 0640 infra/woodpecker/secrets/github-secret infra/woodpecker/secrets/agent-secret
go run ./infra/woodpecker --env-file infra/woodpecker/.env
docker compose --env-file infra/woodpecker/.env -f infra/woodpecker/compose.yaml up -d --wait --wait-timeout 120
```

Validate HTTP 204, login, agent online and a real job after restoring. Never archive a running SQLite database, use `down -v`, prune volumes/system, or reset the database as troubleshooting. Do not remove or reconfigure unrelated services.

## Upgrade and rollback

Review release notes and take a consistent stopped backup first. Update the server, agent and CLI pins together, review corresponding test changes, and perform full verification before operator rollout. Before a database migration, rollback means stopping the stack without deleting volumes and restarting the previous pinned configuration. After a migration, binary-only downgrade may be unsafe: stop the stack and restore the matching pre-upgrade volumes and configuration before starting old pins, then validate readiness, login and a job.

## Troubleshooting

- Missing inputs or files: populate the required `.env` values and create both secret files; rerun preflight. Check owner UID 1000, mode `0640` and matching `WOODPECKER_AGENT_SECRET_GID` for `agent-secret`, operator read access and directory traversal without displaying secrets. `github-secret` also permits mode `0600` or `0400` when the operator can read it.
- OAuth callback errors: compare origin and `/authorize` exactly in the OAuth App and proxy; forbidden login: check the configured actual admin login, closed registration and allowed owner.
- Unreachable webhook or health: check public DNS, certificate, proxy headers/streaming and GitHub delivery status; expect HTTP 204 from `/api/healthz`, not just a successful native ping. Do not bypass TLS.
- Offline agent: check the service logs, private network, secret-file permissions, socket access and health. If the server reports `please provide a token`, compare the group of `agent-secret` with `WOODPECKER_AGENT_SECRET_GID` and check for mode `0640`; do not print or rotate the secret to diagnose access. Queued jobs may be waiting for the single workflow slot or PR approval; review and approve only trusted changes.
- Registry, SDK download, apt or NuGet failures: check outbound DNS/proxy/CA and upstream availability. An SDK checksum failure must stop the job; investigate the pinned release, not skip the hash. Missing Git metadata requires a real Git checkout and clone plugin, not an archive.
- Restore/build/test failures, compiler warnings or NativeAOT failures: inspect the failing command and logs; preserve strict checks and the full test run. For a 30-minute timeout or OOM, inspect job duration and host/child resource usage, reduce competing work or use a suitable dedicated host; do not hide failures or delete data.
