package main

import (
	"fmt"
	"os"
	"strconv"
	"time"
)

// Config is the whole of the server's configuration. Everything comes from the
// environment: there is no config file, because the only deployment shapes are
// "a binary next to the game" and "a container", and both do env well.
type Config struct {
	Addr      string        // listen address
	Token     string        // shared secret the plugin must present
	StatePath string        // where the venture state is persisted
	Lead      time.Duration // notify this long before a venture completes
	Coalesce  time.Duration // ventures completing this close together share one notification
	Stale     time.Duration // a completion missed by more than this is dropped, not sent late
	// NotifyStart also announces ventures as they are assigned. Off by default:
	// you are standing at the summoning bell when it happens, so the useful part
	// is the confirmation that the server has the timers, not the news itself.
	NotifyStart bool

	Pushover   PushoverConfig
	WebhookURL string
}

// PushoverConfig is what little the server has to say about Pushover. It holds
// no credentials at all: the application token and the user key both come from
// the plugin, so this server cannot send to anybody who has not asked it to,
// and nobody's ventures land on the operator's phone or quota.
type PushoverConfig struct {
	Priority int
}

// Defaults chosen for the common case: the plugin and the server on the same
// machine, one player, notifications that should not wake anybody.
func LoadConfig() (Config, error) {
	c := Config{
		Addr:        env("BELL_ADDR", "127.0.0.1:8770"),
		Token:       os.Getenv("BELL_TOKEN"),
		StatePath:   env("BELL_STATE", "state.json"),
		WebhookURL:  os.Getenv("BELL_WEBHOOK_URL"),
		NotifyStart: os.Getenv("BELL_NOTIFY_START") != "",
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

	// A token is not optional even on localhost. Anything reachable enough for
	// the plugin to POST to is reachable enough for something else to.
	if c.Token == "" {
		return c, fmt.Errorf("BELL_TOKEN is required (any long random string; the plugin sends it back)")
	}
	// Loud rather than ignored: someone upgrading would otherwise keep dead
	// variables and quietly wonder why notifications stopped arriving.
	for _, dead := range []string{"PUSHOVER_TOKEN", "PUSHOVER_USER", "PUSHOVER_DEVICE", "PUSHOVER_SOUND"} {
		if os.Getenv(dead) != "" {
			return c, fmt.Errorf("%s is no longer used — Pushover credentials now come from the plugin, "+
				"so move them into its settings and drop this variable", dead)
		}
	}
	if c.Pushover.Priority < -2 || c.Pushover.Priority > 1 {
		// Priority 2 needs retry/expire parameters and an acknowledgement flow.
		// A retainer coming home does not warrant one.
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
