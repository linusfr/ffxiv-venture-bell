package main

import (
	"fmt"
	"os"
	"strconv"
	"time"
)

// Config is the whole of the server's configuration. Environment only: the
// deployment shapes are a binary and a container, and both do env well.
type Config struct {
	Addr      string        // listen address
	Token     string        // shared secret the plugin must present
	StatePath string        // where the venture state is persisted
	Lead      time.Duration // notify this long before a venture completes
	Coalesce  time.Duration // ventures completing this close together share one notification
	Stale     time.Duration // a completion missed by more than this is dropped, not sent late
	// Off by default: you are at the bell when it happens, so the news is the
	// time it is back, not the event.
	NotifyStart bool

	Pushover   PushoverConfig
	WebhookURL string
	UI         UIConfig
}

// PushoverConfig holds no credentials: both halves come from the plugin, so
// nobody's ventures land on the operator's phone or quota.
type PushoverConfig struct {
	Priority int
}

// Defaults for the common case: one player, notifications that wake nobody.
func LoadConfig() (Config, error) {
	c := Config{
		Addr:        env("BELL_ADDR", "127.0.0.1:8770"),
		Token:       os.Getenv("BELL_TOKEN"),
		StatePath:   env("BELL_STATE", "state.json"),
		WebhookURL:  os.Getenv("BELL_WEBHOOK_URL"),
		NotifyStart: os.Getenv("BELL_NOTIFY_START") != "",
		UI: UIConfig{
			Enabled: os.Getenv("BELL_UI") != "",
			Token:   os.Getenv("BELL_UI_TOKEN"),
			Trusted: os.Getenv("BELL_UI_TRUSTED") != "",
		},
	}

	var err error
	if c.Lead, err = envDuration("BELL_LEAD", 0); err != nil {
		return c, err
	}
	if c.Coalesce, err = envDuration("BELL_COALESCE", time.Minute); err != nil {
		return c, err
	}
	if c.Stale, err = envDuration("BELL_STALE", 6*time.Hour); err != nil {
		return c, err
	}
	if c.Pushover.Priority, err = envInt("PUSHOVER_PRIORITY", -1); err != nil {
		return c, err
	}

	// The page is read-only, but "read-only" still means every character's
	// retainers, so it needs a guard of one kind or the other.
	if c.UI.Enabled && !c.UI.Trusted && c.UI.Token == "" {
		return c, fmt.Errorf("BELL_UI needs either BELL_UI_TOKEN, or BELL_UI_TRUSTED=1 when something " +
			"in front of it (OIDC, a VPN) does the authenticating")
	}
	if c.UI.Token != "" && c.UI.Token == c.Token {
		return c, fmt.Errorf("BELL_UI_TOKEN must differ from BELL_TOKEN: reading the timers should not " +
			"also let someone sync or send notifications")
	}

	// Not optional even on localhost: what the plugin can reach, so can
	// something else.
	if c.Token == "" {
		return c, fmt.Errorf("BELL_TOKEN is required (any long random string; the plugin sends it back)")
	}
	// Loud rather than ignored, or an upgrade stops notifying in silence.
	for _, dead := range []string{"PUSHOVER_TOKEN", "PUSHOVER_USER", "PUSHOVER_DEVICE", "PUSHOVER_SOUND"} {
		if os.Getenv(dead) != "" {
			return c, fmt.Errorf("%s is no longer used — Pushover credentials now come from the plugin, "+
				"so move them into its settings and drop this variable", dead)
		}
	}
	if c.Pushover.Priority < -2 || c.Pushover.Priority > 1 {
		// Priority 2 needs an acknowledgement flow; a retainer does not.
		return c, fmt.Errorf("PUSHOVER_PRIORITY must be between -2 and 1, got %d", c.Pushover.Priority)
	}

	return c, nil
}

func env(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func envDuration(key string, def time.Duration) (time.Duration, error) {
	v := os.Getenv(key)
	if v == "" {
		return def, nil
	}
	d, err := time.ParseDuration(v)
	if err != nil {
		return 0, fmt.Errorf("%s: %w", key, err)
	}
	if d < 0 {
		return 0, fmt.Errorf("%s must not be negative", key)
	}
	return d, nil
}

func envInt(key string, def int) (int, error) {
	v := os.Getenv(key)
	if v == "" {
		return def, nil
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		return 0, fmt.Errorf("%s: %w", key, err)
	}
	return n, nil
}
