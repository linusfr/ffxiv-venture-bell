package main

import (
	_ "embed"
	"encoding/json"
	"log/slog"
	"net/http"
)

//go:embed ui.html
var page []byte

// UIConfig is the read-only web page: off unless asked for, and never able to
// change anything — the plugin is where things are done, this is where they are
// looked at.
type UIConfig struct {
	Enabled bool
	// Token is the page's own, deliberately not the plugin's: whoever reads the
	// timers should not thereby be able to sync or send notifications.
	Token string
	// Trusted means something in front authenticates the viewer — Authelia,
	// OIDC, a VPN — and the page then needs no token of its own. On a server
	// with nothing in front, it would serve the state to anyone who can reach
	// the port, so it is opt-in and never the default.
	Trusted bool
}

func mountUI(mux *http.ServeMux, b *Bell, cfg Config, log *slog.Logger) {
	if !cfg.UI.Enabled {
		return
	}

	mux.HandleFunc("GET /", func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.Write(page)
	})

	// The page's own data. Separate from /api/state on purpose: this is the one
	// a proxy is expected to guard, and /api stays bearer-only for the plugin.
	state := func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if err := json.NewEncoder(w).Encode(b.Snapshot()); err != nil {
			log.Error("could not write the state", "err", err)
		}
	}

	if cfg.UI.Trusted {
		mux.HandleFunc("GET /ui/state", state)
		log.Info("web page trusts whatever is in front of it for authentication")
	} else {
		mux.Handle("GET /ui/state", authed(cfg.UI.Token, state))
	}
}
