package main

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

func TestComposeOneVenture(t *testing.T) {
	title, msg := compose([]Completion{{Character: "Y'shtola@Phoenix", Retainer: "Sultana", Venture: "Quick Exploration"}})
	if title != "Venture complete" {
		t.Errorf("title = %q", title)
	}
	if msg != "Sultana — Quick Exploration" {
		t.Errorf("message = %q", msg)
	}
}

func TestComposeNamesTheCharacterOnlyWhenThereIsMoreThanOne(t *testing.T) {
	same := []Completion{
		{Character: "Y'shtola@Phoenix", Retainer: "Sultana", Venture: "Quick Exploration"},
		{Character: "Y'shtola@Phoenix", Retainer: "Bubbles"},
	}
	title, msg := compose(same)
	if title != "2 ventures complete" {
		t.Errorf("title = %q", title)
	}
	if strings.Contains(msg, "Y'shtola") {
		t.Errorf("the only character in play was named on every line: %q", msg)
	}
	if want := "Bubbles\nSultana — Quick Exploration"; msg != want {
		t.Errorf("message = %q, want %q", msg, want)
	}

	mixed := append(append([]Completion{}, same...), Completion{Character: "Alphinaud@Phoenix", Retainer: "Tataru"})
	_, msg = compose(mixed)
	if !strings.Contains(msg, "Alphinaud@Phoenix: Tataru") {
		t.Errorf("two characters in one batch but the lines do not say which is which: %q", msg)
	}
}

func TestPushoverSendsTheClientsCredentials(t *testing.T) {
	var got url.Values
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		r.ParseForm()
		got = r.Form
		w.WriteHeader(http.StatusOK)
	}))
	defer srv.Close()

	done := time.Date(2026, 9, 24, 12, 0, 0, 0, time.UTC)
	p := newPushover(PushoverConfig{Priority: -1}, srv.Client())
	p.api = srv.URL

	err := p.NotifyTo(context.Background(),
		Notification{Title: "Venture complete", Message: "Sultana", At: done},
		PushoverTarget{User: "client-user", Token: "client-app"})
	if err != nil {
		t.Fatalf("NotifyTo: %v", err)
	}

	// The server contributes no identity of its own: both halves are the
	// client's, so a shared server never sends on the operator's account.
	for k, want := range map[string]string{
		"token": "client-app",
		"user":  "client-user",
		// The completion time, not the send time.
		"timestamp": "1790251200",
		"priority":  "-1",
	} {
		if got.Get(k) != want {
			t.Errorf("%s = %q, want %q", k, got.Get(k), want)
		}
	}
}

func TestPushoverRefusesASendWithNoCredentials(t *testing.T) {
	p := newPushover(PushoverConfig{}, http.DefaultClient)
	err := p.NotifyTo(context.Background(), Notification{At: time.Now()}, PushoverTarget{})
	if err == nil {
		t.Fatal("a send with no credentials from the plugin reported success")
	}
	// Permanent: no number of retries will conjure a user key.
	var perm permanent
	if !errors.As(err, &perm) {
		t.Errorf("error = %v, want a permanent one", err)
	}
}

func TestPushoverDoesNotRetryARejectedMessage(t *testing.T) {
	var attempts int
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		attempts++
		w.WriteHeader(http.StatusBadRequest)
		w.Write([]byte(`{"errors":["application token is invalid"]}`))
	}))
	defer srv.Close()

	p := newPushover(PushoverConfig{}, srv.Client())
	p.api = srv.URL

	err := p.NotifyTo(context.Background(), Notification{At: time.Now()}, PushoverTarget{User: "u", Token: "bad"})
	if err == nil {
		t.Fatal("a rejected message reported success")
	}
	if attempts != 1 {
		t.Errorf("retried a 400 %d times; the token will not become valid on the second try", attempts-1)
	}
	if !strings.Contains(err.Error(), "application token is invalid") {
		t.Errorf("the error does not carry Pushover's explanation: %v", err)
	}
}

func TestPushoverRetriesAServerError(t *testing.T) {
	defer shortRetries(t)()

	var attempts int
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		attempts++
		if attempts < 3 {
			w.WriteHeader(http.StatusBadGateway)
			return
		}
		w.WriteHeader(http.StatusOK)
	}))
	defer srv.Close()

	p := newPushover(PushoverConfig{}, srv.Client())
	p.api = srv.URL

	if err := p.NotifyTo(context.Background(), Notification{At: time.Now()}, PushoverTarget{User: "u", Token: "t"}); err != nil {
		t.Fatalf("gave up on a transient failure: %v", err)
	}
	if attempts != 3 {
		t.Errorf("attempts = %d, want 3", attempts)
	}
}

func TestWebhookPostsTheNotificationAsJSON(t *testing.T) {
	var body string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		b := make([]byte, 512)
		n, _ := r.Body.Read(b)
		body = string(b[:n])
		w.WriteHeader(http.StatusNoContent)
	}))
	defer srv.Close()

	wh := &webhookNotifier{url: srv.URL, client: srv.Client()}
	err := wh.Notify(context.Background(), Notification{
		Title:   "Venture complete",
		Message: "Sultana",
		At:      time.Now(),
		Items:   []Completion{{Character: "Y'shtola@Phoenix", Retainer: "Sultana", DoneAt: 1758715200}},
	})
	if err != nil {
		t.Fatalf("Notify: %v", err)
	}
	for _, want := range []string{`"title":"Venture complete"`, `"completed":[`, `"done_at":1758715200`} {
		if !strings.Contains(body, want) {
			t.Errorf("body %s is missing %s", body, want)
		}
	}
}

func TestMultiNotifierSkipsPushoverButStillRelaysTheWebhook(t *testing.T) {
	var pushoverCalls, webhookCalls int
	push := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		pushoverCalls++
		w.WriteHeader(http.StatusOK)
	}))
	defer push.Close()
	hook := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		webhookCalls++
		w.WriteHeader(http.StatusOK)
	}))
	defer hook.Close()

	p := newPushover(PushoverConfig{}, push.Client())
	p.api = push.URL
	m := multiNotifier{p, &webhookNotifier{url: hook.URL, client: hook.Client()}}

	// A plugin with no credentials yet: nothing to send to, but the operator's
	// relay still sees it.
	if err := m.NotifyTo(context.Background(), Notification{At: time.Now()}, PushoverTarget{}); err != nil {
		t.Fatalf("NotifyTo: %v", err)
	}
	if pushoverCalls != 0 {
		t.Errorf("called Pushover %d times with nothing to send to", pushoverCalls)
	}
	if webhookCalls != 1 {
		t.Errorf("webhook calls = %d, want 1", webhookCalls)
	}

	if m.Name() != "pushover+webhook" {
		t.Errorf("Name() = %q", m.Name())
	}
}

func TestMultiNotifierReportsEveryFailure(t *testing.T) {
	defer shortRetries(t)()

	bad := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusBadRequest)
	}))
	defer bad.Close()
	good := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
	}))
	defer good.Close()

	p := newPushover(PushoverConfig{}, bad.Client())
	p.api = bad.URL
	m := multiNotifier{p, &webhookNotifier{url: good.URL, client: good.Client()}}

	err := m.NotifyTo(context.Background(), Notification{At: time.Now()}, PushoverTarget{User: "u", Token: "t"})
	if err == nil || !strings.Contains(err.Error(), "pushover") {
		t.Fatalf("a failing target was not reported: %v", err)
	}
}

// shortRetries keeps the retry tests from taking eight seconds each.
func shortRetries(t *testing.T) func() {
	t.Helper()
	original := retryDelays
	retryDelays = []time.Duration{time.Millisecond, time.Millisecond}
	return func() { retryDelays = original }
}
