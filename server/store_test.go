package main

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestStoreRoundTrip(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.json")
	s := NewStore(path)

	// A missing file is a new install, not an error.
	state, err := s.Load()
	if err != nil {
		t.Fatalf("Load on a fresh install: %v", err)
	}
	if len(state.Characters) != 0 {
		t.Fatalf("a fresh install already knows %d characters", len(state.Characters))
	}

	state.merge("Y'shtola@Phoenix", []Retainer{{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(time.Hour)}}, nil, base)
	if err := s.Save(state); err != nil {
		t.Fatalf("Save: %v", err)
	}

	back, err := s.Load()
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	got := back.Characters["Y'shtola@Phoenix"].Retainers
	if len(got) != 1 || got[0].Venture != "Quick Exploration" || got[0].DoneAt != at(time.Hour) {
		t.Fatalf("state did not survive the round trip: %+v", got)
	}

	info, err := os.Stat(path)
	if err != nil {
		t.Fatalf("Stat: %v", err)
	}
	if perm := info.Mode().Perm(); perm != 0o600 {
		t.Errorf("state file mode = %o, want 600", perm)
	}
}

func TestStoreLeavesNoTemporaryFilesBehind(t *testing.T) {
	dir := t.TempDir()
	s := NewStore(filepath.Join(dir, "state.json"))
	for range 3 {
		if err := s.Save(NewState()); err != nil {
			t.Fatalf("Save: %v", err)
		}
	}

	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("ReadDir: %v", err)
	}
	if len(entries) != 1 {
		t.Fatalf("want only state.json in the directory, got %d entries", len(entries))
	}
}

func TestStoreRefusesGarbage(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.json")
	if err := os.WriteFile(path, []byte("{not json"), 0o600); err != nil {
		t.Fatal(err)
	}
	// Starting fresh would silently drop every pending venture; better to stop
	// and let someone look at the file.
	if _, err := NewStore(path).Load(); err == nil {
		t.Fatal("an unreadable state file was treated as an empty one")
	}
}

func TestStoreCreatesItsDirectory(t *testing.T) {
	// BELL_STATE can point anywhere, and "the folder is not there yet" should
	// not be the reason a venture goes unannounced.
	path := filepath.Join(t.TempDir(), "nested", "dir", "state.json")
	if err := NewStore(path).Save(NewState()); err != nil {
		t.Fatalf("Save into a directory that does not exist yet: %v", err)
	}
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("Stat: %v", err)
	}
}
