package main

import "time"

// Retainer is one row of what the plugin saw at the summoning bell. DoneAt is
// absolute rather than a duration, so a restart or a slow request cannot skew a
// countdown that was never relative.
type Retainer struct {
	Name    string `json:"name"`
	Venture string `json:"venture,omitempty"`
	DoneAt  int64  `json:"done_at"` // 0 = no venture running
}

// PushoverTarget is where one character's notifications go. Both halves come
// from the plugin, which is what lets one server serve several people.
type PushoverTarget struct {
	User  string `json:"user"`
	Token string `json:"token,omitempty"`
}

func (t PushoverTarget) IsZero() bool { return t.User == "" }

// SyncRequest is the plugin's whole view of one character. A state
// replacement, not an event: a missing retainer is gone, a changed DoneAt is a
// reassigned venture, and nothing depends on the previous message arriving.
type SyncRequest struct {
	Character string     `json:"character"`
	Retainers []Retainer `json:"retainers"`
	// Replaced on every sync, so clearing it in the plugin clears it here.
	Pushover *PushoverTarget `json:"pushover,omitempty"`
}

// entry is a Retainer plus what the server knows about it.
type entry struct {
	Name    string `json:"name"`
	Venture string `json:"venture,omitempty"`
	DoneAt  int64  `json:"done_at"`
	// Doubles as the de-duplication set: it is dropped when the retainer is, so
	// nothing accumulates and nothing needs collecting.
	Notified bool `json:"notified,omitempty"`
}

type character struct {
	Retainers []entry `json:"retainers"`
	SyncedAt  int64   `json:"synced_at"`
	// Replaced wholesale on every sync, like the retainer list.
	Pushover *PushoverTarget `json:"pushover,omitempty"`
}

type State struct {
	Characters map[string]*character `json:"characters"`
}

func NewState() *State {
	return &State{Characters: map[string]*character{}}
}

// Target is where a character's notifications go, if it registered any.
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

// merge replaces what is known about one character and reports the ventures
// newly under way. The first sync for a character reports none: installing the
// plugin with eight running would otherwise announce all eight as new.
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
			// Already finished when first seen. The plugin syncs at the bell,
			// so it is on screen — no need to put it on a phone.
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
// Coalescing looks forward: when a venture comes due, anything finishing within
// the window rides along slightly early. Holding the first notification open
// instead would delay every one by the window. It only ever widens a batch that
// already has something due, or a lone venture would always run early.
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
			// Server down, or machine asleep. A venture that finished this
			// morning is noise.
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

// anyDueAt reports whether a completion has arrived, ignoring the coalescing
// window. Stale ones count: they still need a pass through due to be retired.
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

// group splits a batch by destination. Characters sharing one stay in a single
// notification.
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
