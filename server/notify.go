package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"sort"
	"strconv"
	"strings"
	"time"
)

type Notification struct {
	Title   string       `json:"title"`
	Message string       `json:"message"`
	At      time.Time    `json:"-"`
	Items   []Completion `json:"completed"`
}

type Notifier interface {
	Name() string
	Notify(ctx context.Context, n Notification) error
}

// TargetedNotifier delivers to an account the client supplied rather than the
// one in the server's environment. Notifiers that have no notion of a
// recipient — a webhook relay — simply do not implement it and keep receiving
// everything.
type TargetedNotifier interface {
	NotifyTo(ctx context.Context, n Notification, target PushoverTarget) error
}

// compose turns a batch of completions into something worth reading on a lock
// screen: what came back, and what it was doing.
func compose(items []Completion) (title, message string) {
	sort.Slice(items, func(i, j int) bool {
		if items[i].Character != items[j].Character {
			return items[i].Character < items[j].Character
		}
		return items[i].Retainer < items[j].Retainer
	})

	characters := map[string]struct{}{}
	for _, it := range items {
		characters[it.Character] = struct{}{}
	}
	// One character is the normal case, and prefixing every line with the only
	// name in play is noise. Two or more and the name is the point.
	withCharacter := len(characters) > 1

	lines := make([]string, 0, len(items))
	for _, it := range items {
		line := it.Retainer
		if it.Venture != "" {
			line += " — " + it.Venture
		}
		if withCharacter {
			line = it.Character + ": " + line
		}
		lines = append(lines, line)
	}

	switch len(items) {
	case 0:
		return "", ""
	case 1:
		return "Venture complete", lines[0]
	default:
		return fmt.Sprintf("%d ventures complete", len(items)), strings.Join(lines, "\n")
	}
}

// composeStarted announces ventures as they are sent out. It names the time
// they are back, which is the one thing the summoning bell in front of you does
// not put on your phone.
func composeStarted(items []Completion) (title, message string) {
	sort.Slice(items, func(i, j int) bool { return items[i].DoneAt < items[j].DoneAt })

	characters := map[string]struct{}{}
	for _, it := range items {
		characters[it.Character] = struct{}{}
	}
	withCharacter := len(characters) > 1

	lines := make([]string, 0, len(items))
	for _, it := range items {
		line := it.Retainer
		if it.Venture != "" {
			line += " — " + it.Venture
		}
		// Local time, so the container needs a TZ; UTC would be an hour or two
		// off and look like a bug in the timers.
		line += " · back " + time.Unix(it.DoneAt, 0).Format("15:04")
		if withCharacter {
			line = it.Character + ": " + line
		}
		lines = append(lines, line)
	}

	switch len(items) {
	case 0:
		return "", ""
	case 1:
		return "Venture started", lines[0]
	default:
		return fmt.Sprintf("%d ventures started", len(items)), strings.Join(lines, "\n")
	}
}

// ── fan-out ─────────────────────────────────────────────────────────────────

type multiNotifier []Notifier

func (m multiNotifier) Name() string {
	names := make([]string, 0, len(m))
	for _, n := range m {
		names = append(names, n.Name())
	}
	if len(names) == 0 {
		return "none"
	}
	return strings.Join(names, "+")
}

func (m multiNotifier) Notify(ctx context.Context, n Notification) error {
	return m.NotifyTo(ctx, n, PushoverTarget{})
}

func (m multiNotifier) NotifyTo(ctx context.Context, n Notification, target PushoverTarget) error {
	var errs []error
	for _, to := range m {
		var err error
		if t, ok := to.(TargetedNotifier); ok {
			if target.IsZero() {
				// Nowhere to send: the plugin has not been given credentials.
				// The webhook, which has no notion of a recipient, still runs.
				continue
			}
			err = t.NotifyTo(ctx, n, target)
		} else {
			err = to.Notify(ctx, n)
		}
		if err != nil {
			errs = append(errs, fmt.Errorf("%s: %w", to.Name(), err))
		}
	}
	return errors.Join(errs...)
}

// ── Pushover ────────────────────────────────────────────────────────────────

const pushoverAPI = "https://api.pushover.net/1/messages.json"

type pushoverNotifier struct {
	priority int
	client   *http.Client
	api      string
}

func newPushover(cfg PushoverConfig, client *http.Client) *pushoverNotifier {
	return &pushoverNotifier{priority: cfg.Priority, client: client, api: pushoverAPI}
}

func (p *pushoverNotifier) Name() string { return "pushover" }

func (p *pushoverNotifier) Notify(ctx context.Context, n Notification) error {
	return p.NotifyTo(ctx, n, PushoverTarget{})
}

// NotifyTo sends to the account the client supplied. The application token
// stays the server's unless the client brought its own, which is the normal
// Pushover arrangement: one application, many users.
func (p *pushoverNotifier) NotifyTo(ctx context.Context, n Notification, target PushoverTarget) error {
	if target.IsZero() {
		return permanent{errors.New("no Pushover credentials — set them in the plugin's settings")}
	}

	form := url.Values{
		"token":   {target.Token},
		"user":    {target.User},
		"title":   {n.Title},
		"message": {n.Message},
		// The completion time, not the send time. With a lead time set, those
		// differ, and the phone should show when the venture is actually up.
		"timestamp": {strconv.FormatInt(n.At.Unix(), 10)},
		"priority":  {strconv.Itoa(p.priority)},
	}

	return retry(ctx, func(ctx context.Context) error {
		req, err := http.NewRequestWithContext(ctx, http.MethodPost, p.api, strings.NewReader(form.Encode()))
		if err != nil {
			return permanent{err}
		}
		req.Header.Set("Content-Type", "application/x-www-form-urlencoded")

		resp, err := p.client.Do(req)
		if err != nil {
			return err // network trouble: worth another go
		}
		defer resp.Body.Close()
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 512))

		switch {
		case resp.StatusCode/100 == 2:
			return nil
		case resp.StatusCode/100 == 4:
			// Bad token, bad user key, malformed message. Retrying changes
			// nothing and the answer is in the body.
			return permanent{fmt.Errorf("pushover rejected the message: %s: %s", resp.Status, strings.TrimSpace(string(body)))}
		default:
			return fmt.Errorf("pushover: %s: %s", resp.Status, strings.TrimSpace(string(body)))
		}
	})
}

// ── generic webhook ─────────────────────────────────────────────────────────

// webhookNotifier posts the same notification as JSON anywhere: ntfy, gotify,
// a Discord relay, or Home Assistant, for people who want the delivery policy
// to live somewhere other than here.
type webhookNotifier struct {
	url    string
	client *http.Client
}

func (w *webhookNotifier) Name() string { return "webhook" }

func (w *webhookNotifier) Notify(ctx context.Context, n Notification) error {
	body, err := json.Marshal(n)
	if err != nil {
		return permanent{err}
	}

	return retry(ctx, func(ctx context.Context) error {
		req, err := http.NewRequestWithContext(ctx, http.MethodPost, w.url, bytes.NewReader(body))
		if err != nil {
			return permanent{err}
		}
		req.Header.Set("Content-Type", "application/json")

		resp, err := w.client.Do(req)
		if err != nil {
			return err
		}
		defer resp.Body.Close()
		io.Copy(io.Discard, io.LimitReader(resp.Body, 512))

		switch {
		case resp.StatusCode/100 == 2:
			return nil
		case resp.StatusCode/100 == 4:
			return permanent{fmt.Errorf("webhook rejected the message: %s", resp.Status)}
		default:
			return fmt.Errorf("webhook: %s", resp.Status)
		}
	})
}

// ── retries ─────────────────────────────────────────────────────────────────

// permanent marks an error that another attempt cannot fix.
type permanent struct{ err error }

func (p permanent) Error() string { return p.err.Error() }
func (p permanent) Unwrap() error { return p.err }

var retryDelays = []time.Duration{2 * time.Second, 6 * time.Second}

func retry(ctx context.Context, fn func(context.Context) error) error {
	var err error
	for attempt := 0; ; attempt++ {
		if err = fn(ctx); err == nil {
			return nil
		}
		var p permanent
		if errors.As(err, &p) || attempt >= len(retryDelays) {
			return err
		}
		select {
		case <-ctx.Done():
			return errors.Join(err, ctx.Err())
		case <-time.After(retryDelays[attempt]):
		}
	}
}
