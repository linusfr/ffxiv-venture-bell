package main

import (
	"strings"
	"testing"
	"time"
)

var base = time.Date(2026, 9, 24, 12, 0, 0, 0, time.UTC)

func at(d time.Duration) int64 { return base.Add(d).Unix() }

func TestMergeKeepsNotifiedForTheSameVenture(t *testing.T) {
	s := NewState()
	s.merge("Y'shtola@Phoenix", []Retainer{{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(time.Hour)}}, nil, base)
	s.Characters["Y'shtola@Phoenix"].Retainers[0].Notified = true

	// Same completion time: the plugin is just re-syncing what we already sent.
	s.merge("Y'shtola@Phoenix", []Retainer{{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(time.Hour)}}, nil, base)
	if !s.Characters["Y'shtola@Phoenix"].Retainers[0].Notified {
		t.Fatal("a re-sync of the same venture re-armed a notification that was already sent")
	}

	// New completion time: the venture was reassigned and is due again.
	s.merge("Y'shtola@Phoenix", []Retainer{{Name: "Sultana", Venture: "Hunting Exploration", DoneAt: at(3 * time.Hour)}}, nil, base)
	if s.Characters["Y'shtola@Phoenix"].Retainers[0].Notified {
		t.Fatal("a reassigned venture stayed marked as notified")
	}
}

func TestMergeSuppressesVenturesAlreadyCompleteOnFirstSight(t *testing.T) {
	s := NewState()
	s.merge("Y'shtola@Phoenix", []Retainer{
		{Name: "Sultana", DoneAt: at(-time.Minute)},
		{Name: "Bubbles", DoneAt: at(time.Hour)},
	}, nil, base)

	got := s.Characters["Y'shtola@Phoenix"].Retainers
	if !got[0].Notified {
		t.Error("a venture that had already finished at sync time would have been pushed; it is on screen at the bell")
	}
	if got[1].Notified {
		t.Error("a pending venture was suppressed")
	}
}

func TestMergeDropsRetainersAbsentFromTheSync(t *testing.T) {
	s := NewState()
	s.merge("Y'shtola@Phoenix", []Retainer{
		{Name: "Sultana", DoneAt: at(time.Hour)},
		{Name: "Bubbles", DoneAt: at(time.Hour)},
	}, nil, base)
	s.merge("Y'shtola@Phoenix", []Retainer{{Name: "Sultana", DoneAt: at(time.Hour)}}, nil, base)

	if n := len(s.Characters["Y'shtola@Phoenix"].Retainers); n != 1 {
		t.Fatalf("a dismissed retainer survived the sync: %d retainers left", n)
	}
}

func TestDueCoalescesForward(t *testing.T) {
	s := NewState()
	s.merge("Y'shtola@Phoenix", []Retainer{
		{Name: "Sultana", DoneAt: at(-time.Second)},     // due
		{Name: "Bubbles", DoneAt: at(30 * time.Second)}, // inside the window
		{Name: "Coco", DoneAt: at(10 * time.Minute)},    // well outside it
	}, nil, base.Add(-time.Hour))

	got := s.due(base, 0, time.Minute, 6*time.Hour)
	if len(got) != 2 {
		t.Fatalf("want the two completions inside the coalescing window, got %d: %+v", len(got), got)
	}

	// Everything sent is marked, so a second pass in the same window is silent.
	if again := s.due(base, 0, time.Minute, 6*time.Hour); len(again) != 0 {
		t.Fatalf("the same completions came due twice: %+v", again)
	}
}

func TestDueHonoursLeadTime(t *testing.T) {
	s := NewState()
	s.merge("Y'shtola@Phoenix", []Retainer{{Name: "Sultana", DoneAt: at(4 * time.Minute)}}, nil, base.Add(-time.Hour))

	if got := s.due(base, 0, 0, 6*time.Hour); len(got) != 0 {
		t.Fatalf("notified four minutes early with no lead time configured: %+v", got)
	}
	if got := s.due(base, 5*time.Minute, 0, 6*time.Hour); len(got) != 1 {
		t.Fatalf("a five minute lead did not reach a venture due in four: %+v", got)
	}
}

func TestDueDropsCompletionsMissedByMoreThanStale(t *testing.T) {
	s := NewState()
	// Known while pending, so merge does not suppress it; the server was then
	// down for a day.
	s.merge("Y'shtola@Phoenix", []Retainer{{Name: "Sultana", DoneAt: at(-24 * time.Hour)}}, nil, base.Add(-48*time.Hour))

	if got := s.due(base, 0, 0, 6*time.Hour); len(got) != 0 {
		t.Fatalf("sent a notification for a venture that finished a day ago: %+v", got)
	}
	if !s.Characters["Y'shtola@Phoenix"].Retainers[0].Notified {
		t.Error("the stale completion was not put to rest and will be reconsidered on every tick")
	}
}

func TestNextAtIsTheEarliestPendingCompletion(t *testing.T) {
	s := NewState()
	s.merge("Y'shtola@Phoenix", []Retainer{
		{Name: "Coco", DoneAt: at(3 * time.Hour)},
		{Name: "Sultana", DoneAt: at(time.Hour)},
		{Name: "Idle"}, // no venture running
	}, nil, base)

	got, ok := s.nextAt(10 * time.Minute)
	if !ok {
		t.Fatal("no wake-up scheduled although ventures are pending")
	}
	if want := time.Unix(at(50*time.Minute), 0); !got.Equal(want) {
		t.Fatalf("wake-up at %s, want %s", got, want)
	}

	s.Characters["Y'shtola@Phoenix"].Retainers[0].Notified = true
	s.Characters["Y'shtola@Phoenix"].Retainers[1].Notified = true
	if _, ok := s.nextAt(0); ok {
		t.Error("scheduled a wake-up with nothing left to say")
	}
}

func TestDueSendsNothingEarlyOnItsOwn(t *testing.T) {
	s := NewState()
	s.merge("Y'shtola@Phoenix", []Retainer{{Name: "Sultana", DoneAt: at(30 * time.Second)}}, nil, base.Add(-time.Hour))

	// Inside the coalescing window, but there is nothing due for it to ride
	// along with — announcing it now would just be thirty seconds early.
	if got := s.due(base, 0, time.Minute, 6*time.Hour); len(got) != 0 {
		t.Fatalf("a lone venture was announced %v early: %+v", 30*time.Second, got)
	}
	if s.Characters["Y'shtola@Phoenix"].Retainers[0].Notified {
		t.Fatal("the venture was marked notified without a notification")
	}

	// Once it is actually due, it goes out.
	if got := s.due(base.Add(30*time.Second), 0, time.Minute, 6*time.Hour); len(got) != 1 {
		t.Fatalf("want one completion once it is due, got %d", len(got))
	}
}

func TestMergeReportsNewlyStartedVentures(t *testing.T) {
	s := NewState()

	// First sight of a character says nothing: installing the plugin with
	// ventures already running is not the same as assigning them.
	started := s.merge("Y'shtola@Phoenix", []Retainer{
		{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(time.Hour)},
		{Name: "Bubbles"},
	}, nil, base)
	if len(started) != 0 {
		t.Fatalf("the first sync announced %d ventures as newly started: %+v", len(started), started)
	}

	// An idle retainer picking up a venture, and one that was already running
	// left alone.
	started = s.merge("Y'shtola@Phoenix", []Retainer{
		{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(time.Hour)},
		{Name: "Bubbles", Venture: "Field Exploration", DoneAt: at(2 * time.Hour)},
	}, nil, base)
	if len(started) != 1 || started[0].Retainer != "Bubbles" {
		t.Fatalf("want only Bubbles reported as started, got %+v", started)
	}

	// A re-sync of the same picture is not a new venture.
	if again := s.merge("Y'shtola@Phoenix", []Retainer{
		{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(time.Hour)},
		{Name: "Bubbles", Venture: "Field Exploration", DoneAt: at(2 * time.Hour)},
	}, nil, base); len(again) != 0 {
		t.Fatalf("an unchanged re-sync reported %+v", again)
	}

	// A completion that has already passed is not a start either.
	if done := s.merge("Y'shtola@Phoenix", []Retainer{
		{Name: "Sultana", Venture: "Quick Exploration", DoneAt: at(-time.Minute)},
	}, nil, base); len(done) != 0 {
		t.Fatalf("a venture that had already finished was reported as started: %+v", done)
	}
}

func TestComposeStartedNamesWhenTheyAreBack(t *testing.T) {
	title, message := composeStarted([]Completion{
		{Character: "Y'shtola@Phoenix", Retainer: "Bubbles", Venture: "Field Exploration", DoneAt: at(2 * time.Hour)},
		{Character: "Y'shtola@Phoenix", Retainer: "Sultana", Venture: "Quick Exploration", DoneAt: at(time.Hour)},
	})
	if title != "2 ventures started" {
		t.Errorf("title = %q", title)
	}
	// Soonest first, and each line says when it is back — the one thing the
	// bell in front of you does not put on your phone.
	want := time.Unix(at(time.Hour), 0).Format("15:04")
	if !strings.HasPrefix(message, "Sultana — Quick Exploration · back "+want) {
		t.Errorf("message = %q, want it to lead with Sultana back at %s", message, want)
	}
}
