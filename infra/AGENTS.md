# Repository Guidelines

## Project Overview

Infrastructure for a self-hosted Woodpecker CI server, Docker-backed agent, and local OCI registry. The repository also contains a configuration-only preflight command.

## Architecture & Data Flow

- `woodpecker/compose.yaml` defines the server, agent, and registry. The server uses GitHub OAuth; the agent connects to the server over the internal control network and runs workflows through the host Docker socket on the registry network.
- The registry stores images in a named volume and is reachable from the host through `127.0.0.1:5000`; the server binds to `127.0.0.1:8000`. An external HTTPS endpoint is required for `WOODPECKER_HOST`.
- `woodpecker/main.go` invokes `docker compose config --format json`, validates the rendered server settings and secret files, and exits without starting services. Keep validation aligned with Compose configuration.
- Docker socket access is root-equivalent: run the agent only on a dedicated trusted worker. Preserve network isolation, pinned image digests, resource limits, health checks, and container hardening when changing the stack.

## Key Directories

- `woodpecker/`: Compose stack, registry configuration, Go preflight, and example environment.
- `woodpecker/secrets/`: local secret files; do not commit, print, or copy their contents into documentation.

## Development Commands

Run from the repository root; use a configured environment file and Docker Compose:

```sh
go run ./woodpecker/main.go --env-file woodpecker/.env
docker compose --project-directory woodpecker --env-file woodpecker/.env -f woodpecker/compose.yaml config
```

The first command checks configuration and secrets only; it does not start services. For deployment, `docker compose --project-directory woodpecker --env-file woodpecker/.env -f woodpecker/compose.yaml up -d` changes the local runtime; run it only when authorized. No repository build or lint scripts are present; format Go changes with `gofmt`.

## Code Conventions & Common Patterns

- Go preflight functions return errors to `main`, which prints a sanitized diagnostic and exits nonzero; avoid exposing secret values or raw Compose errors.
- Render Compose before validating its JSON rather than independently interpreting YAML or inherited `WOODPECKER_` variables. Preserve the 30-second subprocess timeout and environment filtering.
- Validate secret files without following symlinks, enforce ownership, modes and size, and verify the agent secret's group access and hexadecimal format. Public settings belong in the environment file, not secret files.
- Compose uses named volumes and separate control, egress, registry, and registry-ingress networks. Preserve these boundaries and the registry's localhost-only host binding.

## Important Files

- `woodpecker/main.go`: preflight entry point and validation logic.
- `woodpecker/compose.yaml`: services, networks, volumes, configs, and secrets.
- `woodpecker/registry.yaml`: registry storage, HTTP, and health settings.
- `woodpecker/.env.example`: public configuration template; supply a real HTTPS origin, GitHub administrator and OAuth client ID.

## Runtime/Tooling Preferences

- Go and Docker with the Compose plugin are required for preflight; there is no `go.mod` or package-manager configuration in this repository. The Compose images target `linux/amd64` and are pinned by version and digest.
- The preflight requires `--env-file`; it expects secret files owned by UID 1000. The agent secret must also be group-readable by `WOODPECKER_AGENT_SECRET_GID` (default 1000).

## Testing & QA

No checked-in test suite, test framework, coverage target, or CI lint configuration is present. For configuration changes, run the preflight with a valid local environment and secrets; use `docker compose ... config` to inspect the rendered configuration without starting the stack. Never include secret contents in test output or reports.
