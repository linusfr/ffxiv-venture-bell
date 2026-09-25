package main

import (
	"context"
	"log/slog"
	"sync"
	"time"
)

// Bell owns the countdown. The plugin only reports what the game showed, so
// closing the game — the point of the exercise — changes nothing.
type Bell struct {
	cfg   Config
	store *Store
	send  Notifier
	log   *slog.Logger
	now   func() time.Time

	mu    sync.Mutex
	state *State

	wake chan struct{}
	// Handed to Run so the plugin's request never waits on Pushover.
	started chan startedBatch
}

// startedBatch is one sync's new ventures: one character, one destination.
type startedBatch struct {
	target PushoverTarget
	items  []Completion
}

func NewBell(cfg Config, store *Store, state *State, send Notifier, log *slog.Logger) *Bell {
	return &Bell{
		cfg:   cfg,
		store: store,
		state: state,
		send:  send,
		log:   log,
		now:   time.Now,
		// Buffered: a sync landing mid-tick leaves a note rather than blocking.
		wake:    make(chan struct{}, 1),
		started: make(chan startedBatch, 8),
	}
}

// Sync records one character's retainers and re-arms the timer.
func (b *Bell) Sync(req SyncRequest) error {
	b.mu.Lock()
	started := b.state.merge(req.Character, req.Retainers, req.Pushover, b.now())
	target := b.state.Target(req.Character)
	err := b.store.Save(b.state)
	b.mu.Unlock()

	if b.cfg.NotifyStart && len(started) > 0 {
		select {
		case b.started <- startedBatch{target: target, items: started}:
		default:
			// Eight queued means nothing is going out; drop rather than grow.
			b.log.Warn("dropped a start notification", "count", len(started))
		}
	}

	// Re-arm even if the save failed: a disk problem should cost persistence
	// across a restart, not this session's notification.
	b.nudge()
	return err
}

// Snapshot is what /state serves: a copy, so a slow reader cannot hold the lock.
func (b *Bell) Snapshot() State {
	b.mu.Lock()
	defer b.mu.Unlock()

	out := State{Characters: make(map[string]*character, len(b.state.Characters))}
	for name, c := range b.state.Characters {
		copied := *c
		copied.Retainers = append([]entry(nil), c.Retainers...)
		// Everyone shares one BELL_TOKEN, so /state must not hand out other
		// people's keys — only whether a character has any.
		if c.Pushover != nil {
			copied.Pushover = &PushoverTarget{User: "(set)"}
		}
		out.Characters[name] = &copied
	}
	return out
}

// SendTest delivers one message to the credentials supplied, so a typo is
// discoverable without waiting hours for a venture. It touches no state.
func (b *Bell) SendTest(ctx context.Context, target PushoverTarget) error {
	n := Notification{
		Title:   "Venture Bell",
		Message: "Test notification — your credentials work.",
		At:      b.now(),
	}
	if to, ok := b.send.(TargetedNotifier); ok {
		return to.NotifyTo(ctx, n, target)
	}
	return b.send.Notify(ctx, n)
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
		case batch := <-b.started:
			title, message := composeStarted(batch.items)
			for _, it := range batch.items {
				b.log.Info("venture started", "character", it.Character, "retainer", it.Retainer, "venture", it.Venture)
			}
			b.deliver(ctx, Notification{Title: title, Message: message, At: b.now(), Items: batch.items}, batch.target)
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
// send: a crash then loses one notification, where marking afterwards would
// re-send the batch on every restart until it succeeded.
func (b *Bell) tick(ctx context.Context) {
	b.mu.Lock()
	items := b.state.due(b.now(), b.cfg.Lead, b.cfg.Coalesce, b.cfg.Stale)
	// Grouped under the lock that produced them, so a sync landing mid-tick
	// cannot move a destination out from under a completion.
	groups := b.state.group(items)
	var saveErr error
	if len(items) > 0 {
		saveErr = b.store.Save(b.state)
	}
	b.mu.Unlock()

	if saveErr != nil {
		b.log.Error("could not persist state", "err", saveErr)
	}

	for target, batch := range groups {
		title, message := compose(batch)
		at := time.Unix(batch[0].DoneAt, 0)
		for _, it := range batch {
			if t := time.Unix(it.DoneAt, 0); t.Before(at) {
				at = t
			}
			b.log.Info("venture complete", "character", it.Character, "retainer", it.Retainer, "venture", it.Venture)
		}
		b.deliver(ctx, Notification{Title: title, Message: message, At: at, Items: batch}, target)
	}
}

func (b *Bell) deliver(ctx context.Context, n Notification, target PushoverTarget) {
	if target.IsZero() {
		// Ordinary state, not a fault — but this is where someone finds out.
		b.log.Warn("nothing to notify with — no Pushover credentials from the plugin",
			"character", n.Items[0].Character, "title", n.Title)
	}

	var err error
	if to, ok := b.send.(TargetedNotifier); ok {
		err = to.NotifyTo(ctx, n, target)
	} else {
		err = b.send.Notify(ctx, n)
	}
	if err != nil {
		// Loud: the notification is gone, and the log is the only trace left.
		b.log.Error("notification not delivered", "err", err, "title", n.Title, "count", len(n.Items))
		return
	}
	b.log.Info("notified", "via", b.send.Name(), "title", n.Title, "count", len(n.Items))
}
