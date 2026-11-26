package infra_test

import (
    "encoding/json"
    "fmt"
    "os"
    "os/exec"
    "path/filepath"
    "runtime"
    "strings"
    "testing"
)

func root(t *testing.T) string {
    t.Helper()
    _, file, _, ok := runtime.Caller(0)
    if !ok { t.Fatal("cannot locate test source") }
    return filepath.Clean(filepath.Join(filepath.Dir(file), "../.."))
}

func readProject(t *testing.T, name string) string {
    t.Helper()
    b, err := os.ReadFile(filepath.Join(root(t), name))
    if err != nil { t.Fatal(err) }
    return string(b)
}

func containsAll(t *testing.T, text string, terms ...string) {
    t.Helper()
    for _, term := range terms { if !strings.Contains(text, term) { t.Errorf("missing %q", term) } }
}

func section(t *testing.T, text, start, end string) string {
    t.Helper()
    _, tail, found := strings.Cut(text, start)
    if !found { t.Fatalf("missing section %q", start) }
    body, _, found := strings.Cut(tail, end)
    if !found { t.Fatalf("missing section end %q", end) }
    return body
}

func TestWorkflowContracts(t *testing.T) {
    workflow := readProject(t, ".woodpecker/linux.yaml")
    containsAll(t, workflow, "push", "pull_request", "linux/amd64", "backend: docker", "limit: 1", "woodpeckerci/plugin-git:2.12.1@sha256:b8d2401997be68ec25d62bd753fe5d30236e029fe007a5e823177e0e485ae205", "ubuntu:24.04@sha256:2198e4c65076e9cb0b3d695219df667c0e3b0072da64ca3c2648aca17dcecaf5", "10.0.115", "sha512sum --check --status", "dotnet restore", "dotnet build", "dotnet publish", "artifacts/linux-x64/dotnet-deck", "go test ./tests/infra/...")
    if strings.Contains(workflow, "python3") { t.Error("workflow still depends on Python") }
}

func TestComposeSecurityContracts(t *testing.T) {
    compose := readProject(t, "infra/woodpecker/compose.yaml")
    containsAll(t, compose,
        "netdeck-woodpecker", "woodpecker-server", "woodpecker-agent", "internal: true", "127.0.0.1:8000:8000",
        "woodpeckerci/woodpecker-server:v3.20.1@sha256:0f31560e29d67ec4a06d6eeee4bac38d2a2697a6a741b8a0db3998180f3a3517",
        "woodpeckerci/woodpecker-agent:v3.20.1@sha256:a7d7299a13add0b3936fa0ac9ed23507fe283a7fc7d9596c4289d7e8206541eb",
        "read_only: true", "no-new-privileges:true", "ALL", "server-data:/var/lib/woodpecker", "/var/run/docker.sock:/var/run/docker.sock",
        "WOODPECKER_OPEN: \"false\"", "WOODPECKER_GITHUB_SECRET_FILE", "WOODPECKER_AGENT_SECRET_FILE", "/run/secrets/github-secret", "/run/secrets/agent-secret", "service_healthy", "6442450944", "200000")
    for _, bad := range []string{"privileged: true", "network_mode: host", "WOODPECKER_GITHUB_SECRET:", "WOODPECKER_AGENT_SECRET:"} {
        if strings.Contains(compose, bad) { t.Errorf("unsafe compose setting %q", bad) }
    }
}

func TestRunbookSecurityContracts(t *testing.T) {
    doc := readProject(t, "docs/woodpecker-linux.md")
    containsAll(t, strings.ToLower(doc), "operator-only", "root-equivalent", "non-root operator", "group membership", "chmod authority", "backup", "restore", "rollback", "numeric-owner", "checksum", "webhook", "approval", "queued")
    webhook := section(t, doc, "## Pinned webhook authentication and secrets\n", "## Main merge acceptance evidence\n")
    containsAll(t, webhook, "v3.20.1", "3f1bb0bd0db28e410ad797034d75780116122c91", "/api/hook?access_token=REDACTED", "HS256", "Woodpecker v3.20.1 does not configure a GitHub webhook HMAC secret or validate X-Hub-Signature-256.")
    containsAll(t, strings.ToLower(webhook), "bearer", "no expiry", "replay", "query string", "oauth client secret", "agent shared secret", "backups", "does not guarantee credential rotation")
    backup := section(t, doc, "## Consistent backup and restore\n", "## Upgrade and rollback\n")
    containsAll(t, backup, "not a symlink", "chmod 0700 infra/woodpecker/backups", "umask 077", "--numeric-owner", "stop woodpecker-agent woodpecker-server")
    ignore := readProject(t, ".gitignore")
    containsAll(t, ignore, "/infra/woodpecker/.env", "/infra/woodpecker/secrets/", "/infra/woodpecker/backups/")
    example := readProject(t, "infra/woodpecker/.env.example")
    containsAll(t, example, "WOODPECKER_HOST=", "WOODPECKER_ADMIN=", "WOODPECKER_GITHUB_CLIENT=", "WOODPECKER_AGENT_SECRET_GID=1000")
    containsAll(t, readProject(t, "README.md"), "docs/woodpecker-linux.md")
}

type fixture struct {
    dir string
    envFile string
    config map[string]interface{}
}

func newFixture(t *testing.T) *fixture {
    t.Helper()
    dir := t.TempDir()
    f := &fixture{dir: dir, envFile: filepath.Join(dir, "public env"), config: map[string]interface{}{
        "services": map[string]interface{}{"woodpecker-server": map[string]interface{}{"environment": map[string]interface{}{
            "WOODPECKER_HOST": "https://ci.invalid", "WOODPECKER_ADMIN": "verification-user", "WOODPECKER_GITHUB_CLIENT": "verification-client",
        }}, "woodpecker-agent": map[string]interface{}{"group_add": []string{fmt.Sprint(fixtureSecretGID())}}},
        "secrets": map[string]interface{}{"github-secret": map[string]interface{}{"file": filepath.Join(dir, "github-secret")}, "agent-secret": map[string]interface{}{"file": filepath.Join(dir, "agent-secret")}},
    }}
    script := "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$FIXTURE_ARGS\"\nenv > \"$FIXTURE_ENV\"\nif [ -n \"$FIXTURE_FAIL\" ]; then echo DO_NOT_ECHO_SYNTHETIC_SECRET >&2; exit 1; fi\n/bin/cat \"$FIXTURE_JSON\"\n"
    if err := os.WriteFile(filepath.Join(dir, "docker"), []byte(script), 0700); err != nil { t.Fatal(err) }
    if err := os.WriteFile(f.envFile, nil, 0600); err != nil { t.Fatal(err) }
    return f
}

func (f *fixture) environment() map[string]interface{} {
    return f.config["services"].(map[string]interface{})["woodpecker-server"].(map[string]interface{})["environment"].(map[string]interface{})
}

func (f *fixture) run(t *testing.T, raw string, extraEnv ...string) (string, error) {
    t.Helper()
    if raw == "" { b, err := json.Marshal(f.config); if err != nil { t.Fatal(err) }; raw = string(b) }
    jsonPath := filepath.Join(f.dir, "config.json")
    if err := os.WriteFile(jsonPath, []byte(raw), 0600); err != nil { t.Fatal(err) }
    cmd := exec.Command("go", "run", "./infra/woodpecker", "--env-file", f.envFile)
    cmd.Dir = root(t)
    cmd.Env = append(os.Environ(), "PATH="+f.dir+string(os.PathListSeparator)+os.Getenv("PATH"), "FIXTURE_JSON="+jsonPath, "FIXTURE_ARGS="+filepath.Join(f.dir, "args"), "FIXTURE_ENV="+filepath.Join(f.dir, "env"), "WOODPECKER_HOST=DO_NOT_ECHO_SYNTHETIC_SECRET", "woodpecker_admin=DO_NOT_ECHO_SYNTHETIC_SECRET")
    cmd.Env = append(cmd.Env, extraEnv...)
    out, err := cmd.CombinedOutput()
    return string(out), err
}

func requireFailure(t *testing.T, output string, err error, message string) {
    t.Helper()
    if err == nil || !strings.Contains(output, message) { t.Fatalf("expected failure containing %q; err=%v output=%q", message, err, output) }
    if strings.Contains(output, "DO_NOT_ECHO_SYNTHETIC_SECRET") || strings.Contains(output, "Traceback") { t.Fatalf("sensitive diagnostic: %q", output) }
}

func TestPreflightPublicInputValidation(t *testing.T) {
    tests := []struct { field string; values []interface{} }{
        {"WOODPECKER_HOST", []interface{}{nil, "", "http://ci.invalid", "https://user:DO_NOT_ECHO_SYNTHETIC_SECRET@ci.invalid", "https://ci.invalid/", "https://ci.invalid?", "https://ci.invalid#", "https://ci.invalid:0", "https://ci.invalid:65536", "https://127.0.0.1", "https://localhost", "https://ci.example.com", "https://bad_label.invalid", "https://-bad.invalid", "https://ci.invalid.", "https://" + strings.Repeat("a", 64) + ".invalid"}},
        {"WOODPECKER_ADMIN", []interface{}{nil, "", "-a", "a-", "a,b", "a b", strings.Repeat("a", 40)}},
        {"WOODPECKER_GITHUB_CLIENT", []interface{}{nil, "", " ", "ab cd", "YOUR_ID", "change_me", "example"}},
    }
    for _, tc := range tests { for i, value := range tc.values { t.Run(fmt.Sprintf("%s/%d", tc.field, i), func(t *testing.T) {
        f := newFixture(t); f.environment()[tc.field] = value
        out, err := f.run(t, "")
        requireFailure(t, out, err, "invalid "+tc.field)
    }) } }
}

func TestPreflightRenderingAndRedaction(t *testing.T) {
    t.Run("dependency failure", func(t *testing.T) { f := newFixture(t); out, err := f.run(t, "", "FIXTURE_FAIL=1"); requireFailure(t, out, err, "woodpecker-preflight:") })
    t.Run("invalid json", func(t *testing.T) { f := newFixture(t); out, err := f.run(t, "DO_NOT_ECHO_SYNTHETIC_SECRET"); requireFailure(t, out, err, "invalid JSON") })
    t.Run("invalid structure", func(t *testing.T) { f := newFixture(t); out, err := f.run(t, `{}`); requireFailure(t, out, err, "invalid rendered configuration") })
    t.Run("invocation and environment", func(t *testing.T) {
        f := newFixture(t); f.environment()["WOODPECKER_HOST"] = "http://invalid"
        out, err := f.run(t, ""); requireFailure(t, out, err, "invalid WOODPECKER_HOST")
        args, err := os.ReadFile(filepath.Join(f.dir, "args")); if err != nil { t.Fatal(err) }
        expected := []string{"compose", "--project-directory", filepath.Join(root(t), "infra/woodpecker"), "--env-file", f.envFile, "-f", filepath.Join(root(t), "infra/woodpecker/compose.yaml"), "config", "--format", "json"}
        if strings.TrimSuffix(string(args), "\n") != strings.Join(expected, "\n") { t.Errorf("unexpected compose arguments: %q", args) }
        env, err := os.ReadFile(filepath.Join(f.dir, "env")); if err != nil { t.Fatal(err) }
        containsAll(t, string(env), "COMPOSE_DISABLE_ENV_FILE=1")
        for _, line := range strings.Split(string(env), "\n") { if strings.HasPrefix(strings.ToUpper(line), "WOODPECKER_") { t.Errorf("public or secret environment leaked to compose: %q", line) } }
    })
}

func fixtureSecretGID() int {
    if os.Geteuid() == 0 {
        return 1000
    }
    return os.Getegid()
}

func (f *fixture) secret(t *testing.T, name string, content []byte, mode os.FileMode) {
    t.Helper()
    path := filepath.Join(f.dir, name)
    if err := os.WriteFile(path, content, mode); err != nil { t.Fatal(err) }
    if err := os.Chmod(path, mode); err != nil { t.Fatal(err) }
    if os.Geteuid() == 0 { if err := os.Chown(path, 1000, fixtureSecretGID()); err != nil { t.Fatal(err) } }
}

func TestPreflightSecretPolicy(t *testing.T) {
    if os.Geteuid() != 0 && os.Geteuid() != 1000 { t.Skip("secret ownership contract requires UID 1000 or root") }
    cases := []struct { name string; github, agent []byte; mode os.FileMode; message string }{
        {"valid", []byte("synthetic-oauth-value"), []byte(strings.Repeat("a", 64)), 0600, ""},
        {"valid-group-read", []byte("synthetic-oauth-value"), []byte(strings.Repeat("A", 64)+"\r\n"), 0640, ""},
        {"world-readable", []byte("synthetic-oauth-value"), []byte(strings.Repeat("a", 64)), 0644, "invalid secret file"},
        {"empty", nil, []byte(strings.Repeat("a", 64)), 0600, "invalid secret file"},
        {"oversized", []byte(strings.Repeat("a", 4097)), []byte(strings.Repeat("a", 64)), 0600, "invalid secret file"},
        {"blank-oauth", []byte(" \n"), []byte(strings.Repeat("a", 64)), 0600, "invalid github-secret content"},
        {"invalid-utf8", []byte{0xff}, []byte(strings.Repeat("a", 64)), 0600, "invalid github-secret content"},
        {"short-agent", []byte("synthetic-oauth-value"), []byte(strings.Repeat("a", 63)), 0600, "invalid agent-secret content"},
        {"nonhex-agent", []byte("synthetic-oauth-value"), []byte(strings.Repeat("g", 64)), 0600, "invalid agent-secret content"},
        {"extra-newline", []byte("synthetic-oauth-value"), []byte(strings.Repeat("a", 64)+"\n\n"), 0600, "invalid agent-secret content"},
    }
    for _, tc := range cases { t.Run(tc.name, func(t *testing.T) {
        f := newFixture(t); f.secret(t, "github-secret", tc.github, tc.mode); f.secret(t, "agent-secret", tc.agent, 0640)
        out, err := f.run(t, "")
        if tc.message != "" { requireFailure(t, out, err, tc.message) } else if err != nil || out != "woodpecker-preflight: OK (configuration only; no services started)\n" { t.Fatalf("unexpected result: %v %q", err, out) }
    }) }
    t.Run("symlink rejected", func(t *testing.T) {
        f := newFixture(t); f.secret(t, "target", []byte("synthetic-oauth-value"), 0600)
        if err := os.Symlink(filepath.Join(f.dir, "target"), filepath.Join(f.dir, "github-secret")); err != nil { t.Fatal(err) }
        out, err := f.run(t, ""); requireFailure(t, out, err, "invalid secret file")
    })
    t.Run("agent must be able to read the token", func(t *testing.T) {
        f := newFixture(t)
        f.secret(t, "github-secret", []byte("synthetic-oauth-value"), 0600)
        f.secret(t, "agent-secret", []byte(strings.Repeat("a", 64)), 0600)
        out, err := f.run(t, "")
        requireFailure(t, out, err, "invalid agent-secret permissions")

        f.secret(t, "agent-secret", []byte(strings.Repeat("a", 64)), 0640)
        agent := f.config["services"].(map[string]interface{})["woodpecker-agent"].(map[string]interface{})
        agent["group_add"] = []string{fmt.Sprint(fixtureSecretGID() + 1)}
        out, err = f.run(t, "")
        requireFailure(t, out, err, "invalid agent-secret permissions")
    })
}
