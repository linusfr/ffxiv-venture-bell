package main

import "time"

// Retainer is one row of what the plugin saw at the summoning bell.
// DoneAt is an absolute Unix timestamp, not a duration: the countdown belongs
// to the server, and an absolute time survives a restart, a slow request and a
// game that gets closed thirty seconds after the sync.
type Retainer struct {
	Name    string `json:"name"`
	Venture string `json:"venture,omitempty"`
	DoneAt  int64  `json:"done_at"` // 0 = no venture running
}

// PushoverTarget is where one character's notifications go. It lets a single
// server serve several people: each plugin sends its own user key, and the
// application token stays the server's unless a client brings its own.
//
// Absent means "use whatever the server is configured with", which is the
// single-user case and needs no plugin configuration at all.
type PushoverTarget struct {
	User  string `json:"user"`
	Token string `json:"token,omitempty"`
}

func (t PushoverTarget) IsZero() bool { return t.User == "" }

// SyncRequest is the plugin's whole view of one character's retainers. It is a
// state replacement, not an event: a retainer missing from the list is gone,
// and a changed DoneAt is a reassigned venture. Nothing here needs the server
// to have seen the previous message.
type SyncRequest struct {
	Character string     `json:"character"`
	Retainers []Retainer `json:"retainers"`
	// Where this character's notifications should go. Sent on every sync, so
	// clearing it in the plugin clears it here too.
	Pushover *PushoverTarget `json:"pushover,omitempty"`
}

// entry is a Retainer plus what the server knows about it.
type entry struct {
	Name    string `json:"name"`
	Venture string `json:"venture,omitempty"`
	DoneAt  int64  `json:"done_at"`
	// Notified doubles as the de-duplication set. Keeping it on the record
	// rather than in a separate "already sent" set means it is dropped when the
	// retainer is, so nothing accumulates and nothing needs collecting.
	Notified bool `json:"notified,omitempty"`
}

type character struct {
	Retainers []entry `json:"retainers"`
	SyncedAt  int64   `json:"synced_at"`
	// Replaced wholesale on every sync, like the retainer list: the plugin is
	// the source of truth, and a key removed there is removed here.
	Pushover *PushoverTarget `json:"pushover,omitempty"`
}

type State struct {
	Characters map[string]*character `json:"characters"`
}

func NewState() *State {
	return &State{Characters: map[string]*character{}}
}

// Target is where a character's notifications go, or the zero value for the
// server's own configuration.
func (s *State) Target(character string) PushoverTarget {
	if c, ok := s.Characters[character]; ok && c.Pushover != nil {
		return *c.Pushover
	}
	return PushoverTarget{}
}

// Completion is one venture worth notifying about.
type Completion struct {
	Character string `json:"character"`
	Retainer  string `json:"retainer"`
	Venture   string `json:"venture,omitempty"`
	DoneAt    int64  `json:"done_at"`
}

// merge replaces what is known about one character, and reports the ventures
// that are newly under way.
//
// The first sync for a character reports nothing: installing the plugin with
// eight ventures already running would otherwise announce all eight as if they
// had just been assigned. That first sight is a silent baseline.
func (s *State) merge(name string, in []Retainer, target *PushoverTarget, now time.Time) []Completion {
	prev := map[string]entry{}
	_, known := s.Characters[name]
	if c, ok := s.Characters[name]; ok {
		for _, e := range c.Retainers {
			prev[e.Name] = e
		}
	}

	var started []Completion
	out := make([]entry, 0, len(in))
	for _, r := range in {
		e := entry{Name: r.Name, Venture: r.Venture, DoneAt: r.DoneAt}
		switch p, seen := prev[r.Name]; {
		case seen && p.DoneAt == e.DoneAt:
			// Same venture we already knew about: keep whether it was sent.
			e.Notified = p.Notified
		case e.DoneAt > 0 && e.DoneAt <= now.Unix():
			// First sight of a venture that has already finished. The plugin
			// only syncs at the summoning bell, so this is on screen right now
			// — pushing it to a phone would be telling you what you can see.
			e.Notified = true
		case known && e.DoneAt > now.Unix():
			// A venture this retainer was not on a moment ago.
			started = append(started, Completion{
				Character: name,
				Retainer:  e.Name,
				Venture:   e.Venture,
				DoneAt:    e.DoneAt,
			})
		}
		out = append(out, e)
	}

	s.Characters[name] = &character{Retainers: out, SyncedAt: now.Unix(), Pushover: target}
	return started
}

// due returns everything to notify about now, and marks it notified.
//
// Coalescing looks forward rather than back: when a venture comes due, anything
// finishing within the window rides along with it, slightly early. Holding the
// first notification open to see what else arrives would instead delay every
// notification by the window even when nothing else is coming.
//
// The window only ever widens a batch that already has something genuinely due
// in it. Without that, a lone venture finishing in ten seconds would be
// announced now, and every notification would run early by up to the window.
func (s *State) due(now time.Time, lead, coalesce, stale time.Duration) []Completion {
	if !s.anyDueAt(now, lead, stale) {
		return nil
	}

	var out []Completion
	horizon := now.Add(coalesce)
	cutoff := now.Add(-stale)

	for name, c := range s.Characters {
		for i := range c.Retainers {
			e := &c.Retainers[i]
			if e.Notified || e.DoneAt == 0 {
				continue
			}
			done := time.Unix(e.DoneAt, 0)
			if done.Add(-lead).After(horizon) {
				continue
			}
			// Missed by a mile: the server was down, or the machine was
			// asleep. A notification for a venture that finished this morning
			// is noise, so swallow it.
			if done.Before(cutoff) {
				e.Notified = true
				continue
			}
			e.Notified = true
			out = append(out, Completion{
				Character: name,
				Retainer:  e.Name,
				Venture:   e.Venture,
				DoneAt:    e.DoneAt,
			})
		}
	}
	return out
}

// anyDueAt reports whether a completion has actually arrived, ignoring the
// coalescing window. Stale completions count: they are not notified, but they
// still need a pass through due to be retired.
func (s *State) anyDueAt(now time.Time, lead, stale time.Duration) bool {
	for _, c := range s.Characters {
		for _, e := range c.Retainers {
			if e.Notified || e.DoneAt == 0 {
				continue
			}
			if !time.Unix(e.DoneAt, 0).Add(-lead).After(now) {
				return true
			}
		}
	}
	return false
}

// group splits a batch by where it has to go. Characters that share a
// destination — the usual case of one person, or several who left the plugin's
// notification fields empty — stay in one notification.
func (s *State) group(items []Completion) map[PushoverTarget][]Completion {
	out := map[PushoverTarget][]Completion{}
	for _, it := range items {
		target := s.Target(it.Character)
		out[target] = append(out[target], it)
	}
	return out
}

// nextAt is when the scheduler should wake up, if there is anything to wake for.
func (s *State) nextAt(lead time.Duration) (time.Time, bool) {
	var next time.Time
	for _, c := range s.Characters {
		for _, e := range c.Retainers {
			if e.Notified || e.DoneAt == 0 {
				continue
			}
			at := time.Unix(e.DoneAt, 0).Add(-lead)
			if next.IsZero() || at.Before(next) {
				next = at
			}
		}
	}
	return next, !next.IsZero()
}
