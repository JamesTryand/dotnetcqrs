// Command into-dotnetcqrs is dotnetcqrs-multi-node Milestone 5's direction B: a
// pocketcqrs-side process dispatching a command into a *dotnetcqrs* gateway.
//
// It mirrors, by hand, exactly the HTTP request pocketcqrs's own
// internal/gatewayclient.(*Client).Dispatch builds -- same route, same
// Authorization/Content-Type/Idempotency-Key/Causation-Id/Correlation-Id headers, the
// same deterministic sha256 idempotency key. It is a *thin driver*, not a copy of a
// production extcaller: gatewayclient is under internal/ in the pocketcqrs module and
// cannot be imported from this repo, and Milestone 5's point is proving the request
// shape pocketcqrs emits is accepted by dotnetcqrs's CqrsGatewayEndpoints -- not
// re-testing pocketcqrs's extcaller wiring. If you keep this and pocketcqrs's
// gatewayclient.go in sync, this stays a faithful stand-in.
//
// Auth: dotnetcqrs's gateway (unlike pocketcqrs's) validates whatever scheme its host
// app configured. The DotnetCqrsHost sample validates a shared-key HS256 JWT; this
// driver mints one with the same key/iss/aud. That is what "a shared token issuer"
// means in practice for this direction -- and note it is a *different* credential than
// the PocketBase token direction A needs. See docs/interop.md.
//
// Usage:
//
//	into-dotnetcqrs <taskId> <title>                  -- dispatch CreateTask, expect success
//	into-dotnetcqrs <taskId> <title> --expect-reject  -- expect the gateway to answer 400
package main

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"
)

func main() {
	os.Exit(run())
}

func run() int {
	var positional []string
	expectReject := false
	for _, a := range os.Args[1:] {
		if a == "--expect-reject" {
			expectReject = true
			continue
		}
		positional = append(positional, a)
	}
	if len(positional) < 2 {
		fmt.Fprintln(os.Stderr, "usage: into-dotnetcqrs <taskId> <title> [--expect-reject]")
		return 2
	}
	taskID, title := positional[0], positional[1]

	base := env("DOTNETCQRS_URL", "http://127.0.0.1:8891")
	key := os.Getenv("INTEROP_JWT_KEY")
	if key == "" {
		fmt.Fprintln(os.Stderr, "INTEROP_JWT_KEY must be set (shared HS256 signing key)")
		return 2
	}
	issuer := env("INTEROP_JWT_ISSUER", "interop-issuer")
	audience := env("INTEROP_JWT_AUDIENCE", "dotnetcqrs-interop")
	subject := env("INTEROP_JWT_SUB", "pocketcqrs-extcaller")
	causationID := env("CAUSATION_ID", "interop-cause-B1")
	correlationID := env("CORRELATION_ID", "interop-corr-B1")

	token := mintHS256(key, issuer, audience, subject)

	payload, _ := json.Marshal(map[string]string{"title": title})
	url := fmt.Sprintf("%s/api/cqrs/%s/%s/%s", strings.TrimRight(base, "/"), "task", taskID, "CreateTask")

	req, err := http.NewRequest(http.MethodPost, url, bytes.NewReader(payload))
	if err != nil {
		fmt.Fprintln(os.Stderr, "building request:", err)
		return 2
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer "+token)
	// Same deterministic key gatewayclient.idempotencyKey builds: sha256 of the parts,
	// each followed by a NUL byte, hex-encoded, "extcall-" prefix. --expect-reject uses a
	// fresh nonce part so it is a genuine re-decide, not an idempotent replay (dotnetcqrs
	// has no idempotency store today, but pocketcqrs does -- see docs/interop.md).
	idemParts := []string{"extcall:pocketcqrs", taskID, "0"}
	if expectReject {
		idemParts = append(idemParts, fmt.Sprintf("%d", time.Now().UnixNano()))
	}
	req.Header.Set("Idempotency-Key", idempotencyKey(idemParts))
	req.Header.Set("Causation-Id", causationID)
	req.Header.Set("Correlation-Id", correlationID)

	resp, err := (&http.Client{Timeout: 10 * time.Second}).Do(req)
	if err != nil {
		fmt.Fprintln(os.Stderr, "dispatch failed:", err)
		return 1
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)

	ok := resp.StatusCode >= 200 && resp.StatusCode < 300
	switch {
	case ok && !expectReject:
		fmt.Printf("OK: CreateTask(%s) dispatched into %s\n", taskID, base)
		return 0
	case ok && expectReject:
		fmt.Fprintf(os.Stderr, "FAIL: expected rejection, got %d: %s\n", resp.StatusCode, body)
		return 1
	case !ok && expectReject && resp.StatusCode == 400:
		fmt.Printf("OK: gateway rejected as expected: 400 %s\n", body)
		return 0
	default:
		fmt.Fprintf(os.Stderr, "FAIL: gateway returned %d: %s\n", resp.StatusCode, body)
		return 1
	}
}

func env(name, def string) string {
	if v := os.Getenv(name); v != "" {
		return v
	}
	return def
}

func idempotencyKey(parts []string) string {
	h := sha256.New()
	for _, p := range parts {
		h.Write([]byte(p))
		h.Write([]byte{0})
	}
	return "extcall-" + hex.EncodeToString(h.Sum(nil))
}

func mintHS256(key, issuer, audience, subject string) string {
	now := time.Now().Unix()
	header := b64(`{"alg":"HS256","typ":"JWT"}`)
	claims, _ := json.Marshal(map[string]any{
		"iss": issuer,
		"aud": audience,
		"sub": subject,
		"iat": now,
		"exp": now + 300,
	})
	signingInput := header + "." + b64(string(claims))
	mac := hmac.New(sha256.New, []byte(key))
	mac.Write([]byte(signingInput))
	sig := base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
	return signingInput + "." + sig
}

func b64(s string) string {
	return base64.RawURLEncoding.EncodeToString([]byte(s))
}
