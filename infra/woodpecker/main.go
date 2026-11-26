// Command woodpecker runs the configuration-only Woodpecker preflight.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"time"
	"unicode"
	"unicode/utf8"
)

const originError = "invalid WOODPECKER_HOST: use a real HTTPS origin without a path"

var (
	labelPattern = regexp.MustCompile(`^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$`)
	adminPattern = regexp.MustCompile(`^[A-Za-z0-9]([A-Za-z0-9-]{0,37}[A-Za-z0-9])?$`)
	agentPattern = regexp.MustCompile(`^[0-9A-Fa-f]{64}(\r?\n)?$`)
)

type configMap = map[string]interface{}

func renderCompose(envFile string) (configMap, error) {
	_, source, _, ok := runtime.Caller(0)
	if !ok {
		return nil, errors.New("cannot read configuration or secret files")
	}
	directory, err := filepath.Abs(filepath.Dir(source))
	if err != nil {
		return nil, errors.New("cannot read configuration or secret files")
	}
	envFile, err = filepath.Abs(envFile)
	if err != nil {
		return nil, errors.New("cannot read configuration or secret files")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, "docker", "compose", "--project-directory", directory, "--env-file", envFile, "-f", filepath.Join(directory, "compose.yaml"), "config", "--format", "json")
	for _, entry := range os.Environ() {
		key, _, _ := strings.Cut(entry, "=")
		if !strings.HasPrefix(strings.ToUpper(key), "WOODPECKER_") && key != "COMPOSE_DISABLE_ENV_FILE" {
			cmd.Env = append(cmd.Env, entry)
		}
	}
	cmd.Env = append(cmd.Env, "COMPOSE_DISABLE_ENV_FILE=1")
	output, err := cmd.Output()
	if ctx.Err() == context.DeadlineExceeded {
		return nil, errors.New("Compose rendering timed out after 30 seconds")
	}
	if err != nil {
		return nil, errors.New("Compose rendering failed; check Docker Compose, public options and secret files")
	}
	var value interface{}
	if json.Unmarshal(output, &value) != nil {
		return nil, errors.New("Compose returned invalid JSON")
	}
	config, ok := value.(map[string]interface{})
	if !ok || config == nil {
		return nil, errors.New("invalid rendered configuration")
	}
	return config, nil
}

func validateOrigin(value interface{}) error {
	invalid := errors.New(originError)
	s, ok := value.(string)
	if !ok || !strings.HasPrefix(s, "https://") || strings.ContainsAny(s, "?#") {
		return invalid
	}
	for _, c := range s {
		if c <= 32 || c == 127 {
			return invalid
		}
	}
	u, err := url.Parse(s)
	if err != nil || u.User != nil || u.Path != "" || strings.HasSuffix(u.Host, ":") || u.Hostname() == "" {
		return invalid
	}
	if port := u.Port(); port != "" {
		n, err := strconv.Atoi(port)
		if err != nil || n < 1 || n > 65535 {
			return invalid
		}
	}
	host := strings.ToLower(u.Hostname())
	if net.ParseIP(host) != nil || len(host) > 253 || host == "localhost" || host == "example.com" || strings.HasSuffix(host, ".localhost") || strings.HasSuffix(host, ".example.com") || strings.HasSuffix(host, ".example") {
		return invalid
	}
	for _, label := range strings.Split(host, ".") {
		if !labelPattern.MatchString(label) {
			return invalid
		}
	}
	return nil
}

func validateConfiguration(config configMap) error {
	services, ok := config["services"].(map[string]interface{})
	if !ok { return errors.New("invalid rendered configuration") }
	server, ok := services["woodpecker-server"].(map[string]interface{})
	if !ok { return errors.New("invalid rendered configuration") }
	env, ok := server["environment"].(map[string]interface{})
	if !ok { return errors.New("invalid rendered configuration") }
	if err := validateOrigin(env["WOODPECKER_HOST"]); err != nil { return err }
	admin, ok := env["WOODPECKER_ADMIN"].(string)
	if !ok || !adminPattern.MatchString(admin) { return errors.New("invalid WOODPECKER_ADMIN: use one GitHub login") }
	client, ok := env["WOODPECKER_GITHUB_CLIENT"].(string)
	lower := strings.ToLower(client)
	if !ok || client == "" || strings.IndexFunc(client, unicode.IsSpace) >= 0 || strings.Contains(lower, "your_") || strings.Contains(lower, "change_me") || strings.Contains(lower, "example") {
		return errors.New("invalid WOODPECKER_GITHUB_CLIENT: replace the sample client ID")
	}
	return nil
}

func readSecret(path string, agentGID uint32) ([]byte, error) {
	invalid := errors.New("invalid secret file: check type, owner 1000, mode and size")
	fd, err := syscall.Open(path, syscall.O_RDONLY|syscall.O_NOFOLLOW|syscall.O_NONBLOCK|syscall.O_CLOEXEC, 0)
	if err != nil { return nil, invalid }
	file := os.NewFile(uintptr(fd), "secret")
	defer file.Close()
	var info syscall.Stat_t
	if syscall.Fstat(fd, &info) != nil || info.Mode & syscall.S_IFMT != syscall.S_IFREG || info.Uid != 1000 || (info.Mode & 07777 != 0400 && info.Mode & 07777 != 0600 && info.Mode & 07777 != 0640) || info.Size < 1 || info.Size > 4096 {
		return nil, invalid
	}
	if agentGID != 0 && (info.Gid != agentGID || info.Mode&0040 == 0) {
		return nil, errors.New("invalid agent-secret permissions: use mode 0640 and match WOODPECKER_AGENT_SECRET_GID")
	}
	content, err := io.ReadAll(io.LimitReader(file, 4097))
	if err != nil || len(content) < 1 || len(content) > 4096 { return nil, invalid }
	return content, nil
}

func validateSecretFiles(config configMap) error {
	secrets, ok := config["secrets"].(map[string]interface{})
	if !ok { return errors.New("invalid rendered configuration") }
	services, _ := config["services"].(map[string]interface{})
	agent, _ := services["woodpecker-agent"].(map[string]interface{})
	groups, _ := agent["group_add"].([]interface{})
	if len(groups) != 1 {
		return errors.New("invalid rendered configuration: expected one supplemental agent group")
	}
	group, _ := groups[0].(string)
	gid, err := strconv.ParseUint(group, 10, 32)
	if err != nil || gid == 0 || gid == 1<<32-1 {
		return errors.New("invalid WOODPECKER_AGENT_SECRET_GID: use a nonzero numeric group ID")
	}
	for _, name := range []string{"github-secret", "agent-secret"} {
		entry, ok := secrets[name].(map[string]interface{})
		if !ok { return errors.New("invalid rendered configuration") }
		path, ok := entry["file"].(string)
		if !ok || path == "" || !filepath.IsAbs(path) { return errors.New("invalid rendered configuration") }
		var requiredGID uint32
		if name == "agent-secret" {
			requiredGID = uint32(gid)
		}
		content, err := readSecret(path, requiredGID)
		if err != nil { return err }
		value := string(content)
		if !utf8.Valid(content) || (name == "github-secret" && strings.TrimSpace(value) == "") || (name == "agent-secret" && !agentPattern.MatchString(value)) {
			return fmt.Errorf("invalid %s content", name)
		}
	}
	return nil
}

func main() {
	flags := flag.NewFlagSet("woodpecker-preflight", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	envFile := flags.String("env-file", "", "path to the Compose environment file")
	flags.Usage = func() { fmt.Fprintln(os.Stderr, "usage: woodpecker-preflight --env-file ENV_FILE") }
	if err := flags.Parse(os.Args[1:]); err != nil {
		if errors.Is(err, flag.ErrHelp) { return }
		os.Exit(2)
	}
	if *envFile == "" || flags.NArg() != 0 {
		flags.Usage()
		fmt.Fprintln(os.Stderr, "woodpecker-preflight: error: --env-file is required and positional arguments are not accepted")
		os.Exit(2)
	}
	config, err := renderCompose(*envFile)
	if err == nil { err = validateConfiguration(config) }
	if err == nil { err = validateSecretFiles(config) }
	if err != nil {
		fmt.Fprintln(os.Stderr, "woodpecker-preflight:", err)
		os.Exit(1)
	}
	fmt.Println("woodpecker-preflight: OK (configuration only; no services started)")
}
