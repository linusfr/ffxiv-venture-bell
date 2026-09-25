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

type PushoverConfig struct {
	Token    string
	User     string
	Device   string
	Sound    string
	Priority int
}

// Defaults chosen for the common case: the plugin and the server on the same
// machine, one player, notifications that should not wake anybody.
func LoadConfig() (Config, error) {
	c := Config{
		Addr:      env("BELL_ADDR", "127.0.0.1:8770"),
		Token:     os.Getenv("BELL_TOKEN"),
		StatePath: env("BELL_STATE", "state.json"),
		Pushover: PushoverConfig{
			Token:  os.Getenv("PUSHOVER_TOKEN"),
			User:   os.Getenv("PUSHOVER_USER"),
			Device: os.Getenv("PUSHOVER_DEVICE"),
			Sound:  os.Getenv("PUSHOVER_SOUND"),
		},
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
	if (c.Pushover.Token == "") != (c.Pushover.User == "") {
		return c, fmt.Errorf("PUSHOVER_TOKEN and PUSHOVER_USER must be set together")
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
