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

func TestTestEndpointSendsWithTheSuppliedCredentials(t *testing.T) {
	b, sent, _ := testBell(t, Config{Coalesce: time.Minute, Stale: time.Hour})
	srv := httptest.NewServer(NewServer(b, Config{Token: "secret"}, slog.New(slog.DiscardHandler)))
	defer srv.Close()

	body := `{"character":"Y'shtola@Phoenix","retainers":[],"pushover":{"token":"app","user":"usr"}}`
	resp := post(t, srv, "/test", "Bearer secret", body)
	if resp != http.StatusNoContent {
		t.Fatalf("POST /test = %d, want 204", resp)
	}

	got := sent.all()
	if len(got) != 1 || got[0].Title != "Venture Bell" {
		t.Fatalf("want one test notification, got %+v", got)
	}
	if target := sent.allTargets()[0]; target.User != "usr" || target.Token != "app" {
		t.Errorf("sent with %+v, want the credentials from the body", target)
	}

	// It must not have touched the stored state.
	if n := len(b.Snapshot().Characters); n != 0 {
		t.Errorf("the test wrote %d characters into the state", n)
	}
}

func TestTestEndpointNeedsBothHalves(t *testing.T) {
	b, sent, _ := testBell(t, Config{Coalesce: time.Minute, Stale: time.Hour})
	srv := httptest.NewServer(NewServer(b, Config{Token: "secret"}, slog.New(slog.DiscardHandler)))
	defer srv.Close()

	for _, body := range []string{
		`{"character":"Y","retainers":[],"pushover":{"user":"usr"}}`,
		`{"character":"Y","retainers":[]}`,
	} {
		if code := post(t, srv, "/test", "Bearer secret", body); code != http.StatusBadRequest {
			t.Errorf("POST /test %s = %d, want 400", body, code)
		}
	}
	if n := len(sent.all()); n != 0 {
		t.Errorf("sent %d notifications with incomplete credentials", n)
	}
}

func post(t *testing.T, srv *httptest.Server, path, auth, body string) int {
	t.Helper()
	req, _ := http.NewRequest(http.MethodPost, srv.URL+path, strings.NewReader(body))
	req.Header.Set("Authorization", auth)
	req.Header.Set("Content-Type", "application/json")
	resp, err := srv.Client().Do(req)
	if err != nil {
		t.Fatalf("POST %s: %v", path, err)
	}
	defer resp.Body.Close()
	return resp.StatusCode
}

func TestApiAliasesAreTheSameEndpoints(t *testing.T) {
	b, _, _ := testBell(t, Config{Coalesce: time.Minute, Stale: time.Hour})
	srv := httptest.NewServer(NewServer(b, Config{Token: "secret"}, slog.New(slog.DiscardHandler)))
	defer srv.Close()

	// The plugin can be pointed at either; a proxy tells them apart by path.
	body := `{"character":"Y'shtola@Phoenix","retainers":[]}`
	for _, path := range []string{"/sync", "/api/sync"} {
		if code := post(t, srv, path, "Bearer secret", body); code != http.StatusNoContent {
			t.Errorf("POST %s = %d, want 204", path, code)
		}
		if code := post(t, srv, path, "Bearer wrong", body); code != http.StatusUnauthorized {
			t.Errorf("POST %s with a bad token = %d, want 401", path, code)
		}
	}
}

func TestUIIsOffUnlessAskedFor(t *testing.T) {
	b, _, _ := testBell(t, Config{Coalesce: time.Minute, Stale: time.Hour})
	srv := httptest.NewServer(NewServer(b, Config{Token: "secret"}, slog.New(slog.DiscardHandler)))
	defer srv.Close()

	for _, path := range []string{"/", "/ui/state"} {
		resp, err := srv.Client().Get(srv.URL + path)
		if err != nil {
			t.Fatalf("GET %s: %v", path, err)
		}
		resp.Body.Close()
		if resp.StatusCode != http.StatusNotFound {
			t.Errorf("GET %s = %s with the page disabled, want 404", path, resp.Status)
		}
	}
}

func TestUIStateNeedsTheTokenUnlessSomethingElseGuardsIt(t *testing.T) {
	b, _, _ := testBell(t, Config{Coalesce: time.Minute, Stale: time.Hour})

	guarded := httptest.NewServer(NewServer(b,
		Config{Token: "secret", UI: UIConfig{Enabled: true, Token: "read-only"}}, slog.New(slog.DiscardHandler)))
	defer guarded.Close()

	resp, err := guarded.Client().Get(guarded.URL + "/ui/state")
	if err != nil {
		t.Fatalf("GET /ui/state: %v", err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("GET /ui/state = %s, want 401 without a token", resp.Status)
	}

	// The page's own token works; the plugin's is not a way in.
	for token, want := range map[string]int{"read-only": http.StatusOK, "secret": http.StatusUnauthorized} {
		req, _ := http.NewRequest(http.MethodGet, guarded.URL+"/ui/state", nil)
		req.Header.Set("Authorization", "Bearer "+token)
		got, err := guarded.Client().Do(req)
		if err != nil {
			t.Fatalf("GET /ui/state: %v", err)
		}
		got.Body.Close()
		if got.StatusCode != want {
			t.Errorf("GET /ui/state with %q = %s, want %d", token, got.Status, want)
		}
	}

	// Trusted: something in front is doing the authenticating, so the page's
	// own data is served without a token of its own.
	trusted := httptest.NewServer(NewServer(b,
		Config{Token: "secret", UI: UIConfig{Enabled: true, Trusted: true}}, slog.New(slog.DiscardHandler)))
	defer trusted.Close()

	resp, err = trusted.Client().Get(trusted.URL + "/ui/state")
	if err != nil {
		t.Fatalf("GET /ui/state: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Errorf("GET /ui/state = %s behind a proxy, want 200", resp.Status)
	}

	// The page itself carries no secrets and is served either way.
	page, err := trusted.Client().Get(trusted.URL + "/")
	if err != nil {
		t.Fatalf("GET /: %v", err)
	}
	defer page.Body.Close()
	if page.StatusCode != http.StatusOK {
		t.Errorf("GET / = %s, want the page", page.Status)
	}
}
