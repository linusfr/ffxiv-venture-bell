// Command venturebell holds the countdown for FFXIV retainer ventures and
// pushes a notification when they finish — including, and especially, when the
// game is closed.
package main

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	// The container image is distroless and carries no /usr/share/zoneinfo, so
	// TZ=Europe/Berlin would silently resolve to UTC and every time printed in
	// a notification would be an hour or two out. Embedding the database costs
	// ~450KB and makes TZ mean what it says wherever this runs.
	_ "time/tzdata"
)

func main() {
	log := slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: logLevel()}))

	if err := run(log); err != nil {
		log.Error("exiting", "err", err)
		os.Exit(1)
	}
}

func run(log *slog.Logger) error {
	cfg, err := LoadConfig()
	if err != nil {
		return err
	}

	store := NewStore(cfg.StatePath)
	state, err := store.Load()
	if err != nil {
		return err
	}

	send := buildNotifier(cfg, log)
	bell := NewBell(cfg, store, state, send, log)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	go bell.Run(ctx)

	srv := &http.Server{
		Addr:              cfg.Addr,
		Handler:           NewServer(bell, cfg, log),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      15 * time.Second,
		IdleTimeout:       60 * time.Second,
	}

	errc := make(chan error, 1)
	go func() {
		log.Info("listening", "addr", cfg.Addr, "notifier", send.Name(),
			"lead", cfg.Lead.String(), "coalesce", cfg.Coalesce.String(), "state", cfg.StatePath)
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			errc <- err
		}
	}()

	select {
	case err := <-errc:
		return err
	case <-ctx.Done():
	}

	log.Info("shutting down")
	shutdown, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	return srv.Shutdown(shutdown)
}

func buildNotifier(cfg Config, log *slog.Logger) Notifier {
	client := &http.Client{Timeout: 10 * time.Second}

	var targets multiNotifier
	if cfg.Pushover.Token != "" {
		targets = append(targets, newPushover(cfg.Pushover, client))
	}
	if cfg.WebhookURL != "" {
		targets = append(targets, &webhookNotifier{url: cfg.WebhookURL, client: client})
	}
	if len(targets) == 0 {
		// Useful on the first run: point the plugin at it, watch the log, and
		// only then go and make a Pushover application.
		log.Warn("no notifier configured — completions will only be logged")
		return logNotifier{log}
	}
	return targets
}

type logNotifier struct{ log *slog.Logger }

func (l logNotifier) Name() string { return "log" }

func (l logNotifier) Notify(_ context.Context, n Notification) error {
	l.log.Info("would notify", "title", n.Title, "message", n.Message)
	return nil
}

func logLevel() slog.Level {
	if os.Getenv("BELL_DEBUG") != "" {
		return slog.LevelDebug
	}
	return slog.LevelInfo
}
