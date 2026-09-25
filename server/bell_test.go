package main

import (
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

type captureNotifier struct {
	mu   sync.Mutex
	sent []Notification
}

func (c *captureNotifier) Name() string { return "capture" }

func (c *captureNotifier) Notify(_ context.Context, n Notification) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.sent = append(c.sent, n)
	return nil
}

func (c *captureNotifier) all() []Notification {
	c.mu.Lock()
	defer c.mu.Unlock()
	return append([]Notification(nil), c.sent...)
}

func testBell(t *testing.T, cfg Config) (*Bell, *captureNotifier, *Store) {
	t.Helper()
	if cfg.Token == "" {
		cfg.Token = "secret"
	}
	cfg.StatePath = filepath.Join(t.TempDir(), "state.json")

	store := NewStore(cfg.StatePath)
	state, err := store.Load()
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	send := &captureNotifier{}
	b := NewBell(cfg, store, state, send, slog.New(slog.DiscardHandler))
	return b, send, store
}

func TestBellNotifiesWhenAVentureComesDue(t *testing.T) {
	b, sent, _ := testBell(t, Config{Coalesce: time.Minute, Stale: 6 * time.Hour})

	// Synced an hour ago, with the venture still pending at the time.
	b.now = func() time.Time { return base.Add(-time.Hour) }
	if err := b.Sync(SyncRequest{
		Character: "Y'shtola@Phoenix",
		Retainers: []Retainer{
			{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(0)},
			{Name: "Bubbles", Venture: "Hunting Exploration", DoneAt: at(30 * time.Second)},
			{Name: "Coco", Venture: "Field Exploration", DoneAt: at(4 * time.Hour)},
		},
	}); err != nil {
		t.Fatalf("Sync: %v", err)
	}

	b.now = func() time.Time { return base }
	b.tick(context.Background())

	got := sent.all()
	if len(got) != 1 {
		t.Fatalf("want one coalesced notification, got %d", len(got))
	}
	if got[0].Title != "2 ventures complete" {
		t.Errorf("title = %q", got[0].Title)
	}
	if strings.Contains(got[0].Message, "Coco") {
		t.Errorf("a venture four hours out was announced with the others: %q", got[0].Message)
	}
	if !got[0].At.Equal(time.Unix(at(0), 0)) {
		t.Errorf("At = %s, want the earliest completion in the batch", got[0].At)
	}

	// Nothing repeats on the next tick.
	b.tick(context.Background())
	if n := len(sent.all()); n != 1 {
		t.Fatalf("the same batch was sent again: %d notifications", n)
	}
}

func TestBellSurvivesARestart(t *testing.T) {
	cfg := Config{Token: "secret", Coalesce: time.Minute, Stale: 6 * time.Hour}
	b, _, store := testBell(t, cfg)
	cfg.StatePath = store.path

	b.now = func() time.Time { return base.Add(-time.Hour) }
	if err := b.Sync(SyncRequest{
		Character: "Y'shtola@Phoenix",
		Retainers: []Retainer{{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(0)}},
	}); err != nil {
		t.Fatalf("Sync: %v", err)
	}

	// A second process reading the same file — the countdown is in the state,
	// not in the running timer.
	reloaded, err := NewStore(cfg.StatePath).Load()
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	sent := &captureNotifier{}
	restarted := NewBell(cfg, NewStore(cfg.StatePath), reloaded, sent, slog.New(slog.DiscardHandler))
	restarted.now = func() time.Time { return base }
	restarted.tick(context.Background())

	if n := len(sent.all()); n != 1 {
		t.Fatalf("the venture was forgotten across the restart: %d notifications", n)
	}
}

func TestBellRunWakesOnSyncAndFires(t *testing.T) {
	b, sent, _ := testBell(t, Config{Coalesce: 0, Stale: time.Hour})

	// Completion times are whole Unix seconds, so a venture cannot be due in
	// 50ms. The clock is shifted instead: the bell believes it is an hour from
	// now, 50ms short of a completion, and the wait stays a real 50ms.
	target := time.Now().Add(time.Hour).Truncate(time.Second).Add(time.Second)
	offset := time.Until(target) - 50*time.Millisecond
	b.now = func() time.Time { return time.Now().Add(offset) }

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan struct{})
	go func() { b.Run(ctx); close(done) }()

	// Arrives while the scheduler is already parked with nothing to do.
	if err := b.Sync(SyncRequest{
		Character: "Y'shtola@Phoenix",
		Retainers: []Retainer{{Name: "Sultana", DoneAt: target.Unix()}},
	}); err != nil {
		t.Fatalf("Sync: %v", err)
	}

	deadline := time.After(3 * time.Second)
	for len(sent.all()) == 0 {
		select {
		case <-deadline:
			t.Fatal("the scheduler never woke for a venture synced after it had parked")
		case <-time.After(10 * time.Millisecond):
		}
	}

	cancel()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("Run did not return when its context was cancelled")
	}
}

func TestStateEndpointReportsWhatTheServerBelieves(t *testing.T) {
	b, _, _ := testBell(t, Config{Token: "secret", Coalesce: time.Minute, Stale: time.Hour})
	srv := httptest.NewServer(NewServer(b, Config{Token: "secret"}, slog.New(slog.DiscardHandler)))
	defer srv.Close()

	body := `{"character":"Y'shtola@Phoenix","retainers":[{"name":"Sultana","venture":"Quick Exploration","done_at":` +
		jsonInt(time.Now().Add(time.Hour).Unix()) + `}]}`
	req, _ := http.NewRequest(http.MethodPost, srv.URL+"/sync", strings.NewReader(body))
	req.Header.Set("Authorization", "Bearer secret")
	resp, err := srv.Client().Do(req)
	if err != nil {
		t.Fatalf("POST /sync: %v", err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusNoContent {
		t.Fatalf("POST /sync = %s", resp.Status)
	}

	req, _ = http.NewRequest(http.MethodGet, srv.URL+"/state", nil)
	req.Header.Set("Authorization", "Bearer secret")
	resp, err = srv.Client().Do(req)
	if err != nil {
		t.Fatalf("GET /state: %v", err)
	}
	defer resp.Body.Close()

	var got State
	raw, _ := io.ReadAll(resp.Body)
	if err := json.Unmarshal(raw, &got); err != nil {
		t.Fatalf("state is not JSON: %v", err)
	}
	if c, ok := got.Characters["Y'shtola@Phoenix"]; !ok || len(c.Retainers) != 1 || c.Retainers[0].Name != "Sultana" {
		t.Fatalf("state does not show the sync: %s", raw)
	}
}

func jsonInt(v int64) string {
	b, _ := json.Marshal(v)
	return string(b)
}

func TestBellStillSchedulesWhenTheStateCannotBeSaved(t *testing.T) {
	b, sent, _ := testBell(t, Config{Coalesce: time.Minute, Stale: 6 * time.Hour})
	// A path that cannot be written: a directory where the file should be.
	dir := t.TempDir()
	b.store = NewStore(dir)

	b.now = func() time.Time { return base.Add(-time.Hour) }
	err := b.Sync(SyncRequest{
		Character: "Y'shtola@Phoenix",
		Retainers: []Retainer{{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(0)}},
	})
	if err == nil {
		t.Fatal("a failed save was reported as success")
	}

	// The timer is still live, which is the part that matters this session.
	b.now = func() time.Time { return base }
	b.tick(context.Background())
	if n := len(sent.all()); n != 1 {
		t.Fatalf("a save failure also swallowed the notification: %d sent", n)
	}
}

func TestBellAnnouncesStartedVenturesWithoutBlockingTheSync(t *testing.T) {
	b, sent, _ := testBell(t, Config{Coalesce: time.Minute, Stale: 6 * time.Hour, NotifyStart: true})
	b.now = func() time.Time { return base }

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go b.Run(ctx)

	// Baseline: the character is unknown, so this one is silent.
	if err := b.Sync(SyncRequest{Character: "Y'shtola@Phoenix", Retainers: []Retainer{{Name: "Sultana"}}}); err != nil {
		t.Fatalf("Sync: %v", err)
	}
	// Now a venture goes out.
	if err := b.Sync(SyncRequest{
		Character: "Y'shtola@Phoenix",
		Retainers: []Retainer{{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(time.Hour)}},
	}); err != nil {
		t.Fatalf("Sync: %v", err)
	}

	deadline := time.After(3 * time.Second)
	for len(sent.all()) == 0 {
		select {
		case <-deadline:
			t.Fatal("no start notification after a venture was assigned")
		case <-time.After(10 * time.Millisecond):
		}
	}

	got := sent.all()
	if len(got) != 1 || got[0].Title != "Venture started" {
		t.Fatalf("want one start notification, got %+v", got)
	}
}

func TestBellStaysQuietAboutStartsUnlessAskedTo(t *testing.T) {
	b, sent, _ := testBell(t, Config{Coalesce: time.Minute, Stale: 6 * time.Hour})
	b.now = func() time.Time { return base }

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go b.Run(ctx)

	for range 2 {
		if err := b.Sync(SyncRequest{
			Character: "Y'shtola@Phoenix",
			Retainers: []Retainer{{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(time.Hour)}},
		}); err != nil {
			t.Fatalf("Sync: %v", err)
		}
	}

	// Long enough for a notification to have gone out if it were going to.
	time.Sleep(200 * time.Millisecond)
	if n := len(sent.all()); n != 0 {
		t.Fatalf("sent %d start notifications with BELL_NOTIFY_START off", n)
	}
}
