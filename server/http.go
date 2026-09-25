package main

import (
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"strings"
	"time"
	"unicode/utf8"
)

const (
	maxBody      = 64 << 10
	maxRetainers = 32             // the game allows ten; the rest is headroom
	maxName      = 64             // a character name plus world fits comfortably
	maxSkew      = 30 * 24 * 3600 // seconds either side of now a timestamp may land
	maxKey       = 64             // Pushover keys are 30; the rest is headroom
)

func NewServer(b *Bell, cfg Config, log *slog.Logger) http.Handler {
	mux := http.NewServeMux()

	// Unauthenticated on purpose: it says nothing but that the process is up,
	// and container health checks should not need the secret.
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		fmt.Fprintln(w, "ok")
	})

	mux.Handle("POST /sync", authed(cfg.Token, func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxBody)

		var req SyncRequest
		dec := json.NewDecoder(r.Body)
		dec.DisallowUnknownFields()
		if err := dec.Decode(&req); err != nil {
			http.Error(w, "malformed body: "+err.Error(), http.StatusBadRequest)
			return
		}
		if err := validate(&req, time.Now()); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}

		if err := b.Sync(req); err != nil {
			log.Error("sync failed", "err", err, "character", req.Character)
			http.Error(w, "could not record the sync", http.StatusInternalServerError)
			return
		}

		log.Info("synced", "character", req.Character, "retainers", len(req.Retainers))
		w.WriteHeader(http.StatusNoContent)
	}))

	// Everything the server believes, for looking at with curl when a
	// notification does not turn up.
	mux.Handle("GET /state", authed(cfg.Token, func(w http.ResponseWriter, r *http.Request) {
		state := b.Snapshot()
		w.Header().Set("Content-Type", "application/json")
		enc := json.NewEncoder(w)
		enc.SetIndent("", "  ")
		if err := enc.Encode(state); err != nil {
			log.Error("could not write state", "err", err)
		}
	}))

	return mux
}

func authed(token string, next http.HandlerFunc) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// The scheme is required rather than tolerated: accepting a bare token
		// too would make the header two formats, and the plugin only sends one.
		presented, ok := strings.CutPrefix(r.Header.Get("Authorization"), "Bearer ")
		if !ok || subtle.ConstantTimeCompare([]byte(presented), []byte(token)) != 1 {
			w.Header().Set("WWW-Authenticate", `Bearer realm="venture-bell"`)
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		next(w, r)
	})
}

// validate keeps a malformed or hostile payload out of the state file, which
// is read back and trusted on every restart.
func validate(req *SyncRequest, now time.Time) error {
	req.Character = strings.TrimSpace(req.Character)
	if req.Character == "" {
		return errors.New("character is required")
	}
	if utf8.RuneCountInString(req.Character) > maxName {
		return fmt.Errorf("character name is longer than %d characters", maxName)
	}
	if len(req.Retainers) > maxRetainers {
		return fmt.Errorf("at most %d retainers, got %d", maxRetainers, len(req.Retainers))
	}

	if req.Pushover != nil {
		req.Pushover.User = strings.TrimSpace(req.Pushover.User)
		req.Pushover.Token = strings.TrimSpace(req.Pushover.Token)
		// An object with no user key is the plugin saying "use the server's
		// own settings", which is what omitting it means. Normalise so the
		// state file does not grow empty targets.
		switch {
		case req.Pushover.User == "" && req.Pushover.Token == "":
			req.Pushover = nil
		case req.Pushover.User == "" || req.Pushover.Token == "":
			// This server has no Pushover identity of its own, so half a pair
			// is nothing it can complete.
			return errors.New("pushover needs both an application token and a user key, or neither")
		case utf8.RuneCountInString(req.Pushover.User) > maxKey ||
			utf8.RuneCountInString(req.Pushover.Token) > maxKey:
			return fmt.Errorf("pushover keys are longer than %d characters", maxKey)
		}
	}

	seen := make(map[string]struct{}, len(req.Retainers))
	for i := range req.Retainers {
		r := &req.Retainers[i]
		r.Name = strings.TrimSpace(r.Name)
		r.Venture = strings.TrimSpace(r.Venture)

		if r.Name == "" {
			return fmt.Errorf("retainer %d has no name", i)
		}
		if utf8.RuneCountInString(r.Name) > maxName {
			return fmt.Errorf("retainer %q has a name longer than %d characters", r.Name, maxName)
		}
		if utf8.RuneCountInString(r.Venture) > maxName {
			return fmt.Errorf("retainer %q has a venture longer than %d characters", r.Name, maxName)
		}
		// Retainers are keyed by name when merging, so duplicates would make
		// one of them silently unreachable.
		if _, dup := seen[r.Name]; dup {
			return fmt.Errorf("retainer %q appears twice", r.Name)
		}
		seen[r.Name] = struct{}{}

		if r.DoneAt != 0 {
			if delta := r.DoneAt - now.Unix(); delta > maxSkew || delta < -maxSkew {
				return fmt.Errorf("retainer %q has an implausible done_at (%d)", r.Name, r.DoneAt)
			}
		}
	}
	return nil
}
