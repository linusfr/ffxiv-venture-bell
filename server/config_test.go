package main

import (
	"strings"
	"testing"
)

func TestConfigRefusesAPageWithNothingGuardingIt(t *testing.T) {
	t.Setenv("BELL_TOKEN", "plugin")
	t.Setenv("BELL_UI", "1")

	// Read-only still means every character's retainers.
	if _, err := LoadConfig(); err == nil || !strings.Contains(err.Error(), "BELL_UI_TOKEN") {
		t.Fatalf("a page with no token and nothing in front was accepted: %v", err)
	}

	// The plugin's token is not the page's: reading should not also let someone
	// sync or send notifications.
	t.Setenv("BELL_UI_TOKEN", "plugin")
	if _, err := LoadConfig(); err == nil || !strings.Contains(err.Error(), "must differ") {
		t.Fatalf("the page was allowed to share the plugin's token: %v", err)
	}

	t.Setenv("BELL_UI_TOKEN", "read-only")
	if _, err := LoadConfig(); err != nil {
		t.Fatalf("a page with its own token was refused: %v", err)
	}

	// Or nothing of its own, when something in front does the authenticating.
	t.Setenv("BELL_UI_TOKEN", "")
	t.Setenv("BELL_UI_TRUSTED", "1")
	if _, err := LoadConfig(); err != nil {
		t.Fatalf("a page behind a proxy was refused: %v", err)
	}
}
