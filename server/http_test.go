package main

import (
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestSyncRequiresTheToken(t *testing.T) {
	b, _, _ := testBell(t, Config{Coalesce: time.Minute, Stale: time.Hour})
	srv := httptest.NewServer(NewServer(b, Config{Token: "secret"}, slog.New(slog.DiscardHandler)))
	defer srv.Close()

	body := `{"character":"Y'shtola@Phoenix","retainers":[]}`
	for _, tc := range []struct {
		name, header string
		want         int
	}{
		{"no header", "", http.StatusUnauthorized},
		{"wrong token", "Bearer nope", http.StatusUnauthorized},
		{"bare token without the scheme", "secret", http.StatusUnauthorized},
		{"correct token", "Bearer secret", http.StatusNoContent},
	} {
		t.Run(tc.name, func(t *testing.T) {
			req, _ := http.NewRequest(http.MethodPost, srv.URL+"/sync", strings.NewReader(body))
			if tc.header != "" {
				req.Header.Set("Authorization", tc.header)
			}
			resp, err := srv.Client().Do(req)
			if err != nil {
				t.Fatalf("POST /sync: %v", err)
			}
			defer resp.Body.Close()
			if resp.StatusCode != tc.want {
				t.Errorf("status = %s, want %d", resp.Status, tc.want)
			}
		})
	}
}

func TestHealthzNeedsNoToken(t *testing.T) {
	b, _, _ := testBell(t, Config{})
	srv := httptest.NewServer(NewServer(b, Config{Token: "secret"}, slog.New(slog.DiscardHandler)))
	defer srv.Close()

	resp, err := srv.Client().Get(srv.URL + "/healthz")
	if err != nil {
		t.Fatalf("GET /healthz: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Errorf("status = %s", resp.Status)
	}
}

func TestValidate(t *testing.T) {
	now := time.Now()
	long := strings.Repeat("a", maxName+1)

	for _, tc := range []struct {
		name    string
		req     SyncRequest
		wantErr string
	}{
		{
			name: "a full sync",
			req: SyncRequest{Character: "Y'shtola@Phoenix", Retainers: []Retainer{
				{Name: "Sultana", Venture: "Quick Exploration", DoneAt: now.Add(time.Hour).Unix()},
				{Name: "Idle"},
			}},
		},
		{name: "no character", req: SyncRequest{Character: "  "}, wantErr: "character is required"},
		{name: "overlong character", req: SyncRequest{Character: long}, wantErr: "longer than"},
		{
			name:    "nameless retainer",
			req:     SyncRequest{Character: "Y'shtola@Phoenix", Retainers: []Retainer{{DoneAt: now.Unix()}}},
			wantErr: "has no name",
		},
		{
			name: "the same retainer twice",
			req: SyncRequest{Character: "Y'shtola@Phoenix", Retainers: []Retainer{
				{Name: "Sultana", DoneAt: now.Add(time.Hour).Unix()},
				{Name: "Sultana", DoneAt: now.Add(2 * time.Hour).Unix()},
			}},
			wantErr: "appears twice",
		},
		{
			name:    "a completion time from next year",
			req:     SyncRequest{Character: "Y'shtola@Phoenix", Retainers: []Retainer{{Name: "Sultana", DoneAt: now.AddDate(1, 0, 0).Unix()}}},
			wantErr: "implausible",
		},
	} {
		t.Run(tc.name, func(t *testing.T) {
			req := tc.req
			err := validate(&req, now)
			switch {
			case tc.wantErr == "" && err != nil:
				t.Fatalf("rejected a valid sync: %v", err)
			case tc.wantErr != "" && err == nil:
				t.Fatalf("accepted %s", tc.name)
			case tc.wantErr != "" && !strings.Contains(err.Error(), tc.wantErr):
				t.Fatalf("error %q does not mention %q", err, tc.wantErr)
			}
		})
	}
}

func TestSyncRejectsUnknownFields(t *testing.T) {
	b, _, _ := testBell(t, Config{Coalesce: time.Minute, Stale: time.Hour})
	srv := httptest.NewServer(NewServer(b, Config{Token: "secret"}, slog.New(slog.DiscardHandler)))
	defer srv.Close()

	// A plugin sending a field this server does not know about is a version
	// mismatch, and silence would turn it into a notification that never comes.
	req, _ := http.NewRequest(http.MethodPost, srv.URL+"/sync",
		strings.NewReader(`{"character":"Y'shtola@Phoenix","retainers":[],"gil":1200}`))
	req.Header.Set("Authorization", "Bearer secret")
	resp, err := srv.Client().Do(req)
	if err != nil {
		t.Fatalf("POST /sync: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		t.Errorf("status = %s, want 400", resp.Status)
	}
}
