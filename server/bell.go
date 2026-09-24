package main

import (
	"context"
	"log/slog"
	"sync"
	"time"
)

// Bell owns the countdown. The plugin only ever tells it what the game showed;
// everything about when to speak up happens here, so closing the game — the
// entire point of the exercise — changes nothing.
type Bell struct {
	cfg   Config
	store *Store
	send  Notifier
	log   *slog.Logger
	now   func() time.Time

	mu    sync.Mutex
	state *State

	wake chan struct{}
}

func NewBell(cfg Config, store *Store, state *State, send Notifier, log *slog.Logger) *Bell {
	return &Bell{
		cfg:   cfg,
		store: store,
		state: state,
		send:  send,
		log:   log,
		now:   time.Now,
		// Buffered: a sync that lands while the scheduler is mid-tick should
		// leave a note, not block on it.
		wake: make(chan struct{}, 1),
	}
}

// Sync records one character's retainers and re-arms the timer.
func (b *Bell) Sync(req SyncRequest) error {
	b.mu.Lock()
	b.state.merge(req.Character, req.Retainers, b.now())
	err := b.store.Save(b.state)
	b.mu.Unlock()

	// Re-arm even if the save failed: the merge already happened in memory, and
	// a disk problem should cost persistence across a restart, not the
	// notification this session.
	b.nudge()
	return err
}

// Snapshot is what /state serves: a copy, so a slow reader cannot hold the
// scheduler's lock.
func (b *Bell) Snapshot() State {
	b.mu.Lock()
	defer b.mu.Unlock()

	out := State{Characters: make(map[string]*character, len(b.state.Characters))}
	for name, c := range b.state.Characters {
		copied := *c
		copied.Retainers = append([]entry(nil), c.Retainers...)
		out.Characters[name] = &copied
	}
	return out
}

func (b *Bell) nudge() {
	select {
	case b.wake <- struct{}{}:
	default:
	}
}

// Run blocks until ctx is cancelled, waking for the next completion or for a
// sync that moves it.
func (b *Bell) Run(ctx context.Context) {
	for {
		b.tick(ctx)

		var timer *time.Timer
		var fire <-chan time.Time
		if at, ok := b.nextAt(); ok {
			delay := max(at.Sub(b.now()), 0)
			timer = time.NewTimer(delay)
			fire = timer.C
			b.log.Debug("armed", "at", at.Format(time.RFC3339), "in", delay.Round(time.Second).String())
		}

		select {
		case <-ctx.Done():
			if timer != nil {
				timer.Stop()
			}
			return
		case <-b.wake:
		case <-fire:
		}
		if timer != nil {
			timer.Stop()
		}
	}
}

func (b *Bell) nextAt() (time.Time, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.state.nextAt(b.cfg.Lead)
}

// tick sends whatever is due. Completions are marked and persisted before the
// send, not after: a crash mid-send loses one notification, whereas marking
// afterwards would re-send the whole batch on every restart until it succeeds.
func (b *Bell) tick(ctx context.Context) {
	b.mu.Lock()
	items := b.state.due(b.now(), b.cfg.Lead, b.cfg.Coalesce, b.cfg.Stale)
	var saveErr error
	if len(items) > 0 {
		saveErr = b.store.Save(b.state)
	}
	b.mu.Unlock()

	if saveErr != nil {
		b.log.Error("could not persist state", "err", saveErr)
	}
	if len(items) == 0 {
		return
	}

	title, message := compose(items)
	at := time.Unix(items[0].DoneAt, 0)
	for _, it := range items {
		if t := time.Unix(it.DoneAt, 0); t.Before(at) {
			at = t
		}
		b.log.Info("venture complete", "character", it.Character, "retainer", it.Retainer, "venture", it.Venture)
	}

	n := Notification{Title: title, Message: message, At: at, Items: items}
	if err := b.send.Notify(ctx, n); err != nil {
		// Deliberately loud: the notification is gone, and the only place that
		// fact can still surface is the log.
		b.log.Error("notification not delivered", "err", err, "title", title, "count", len(items))
		return
	}
	b.log.Info("notified", "via", b.send.Name(), "count", len(items))
}
