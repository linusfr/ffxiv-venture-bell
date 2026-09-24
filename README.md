# Venture Bell

[![ci](https://img.shields.io/github/actions/workflow/status/linusfr/ffxiv-venture-bell/ci.yml?branch=main&label=ci&cacheSeconds=300)](https://github.com/linusfr/ffxiv-venture-bell/actions/workflows/ci.yml)
[![licence](https://img.shields.io/github/license/linusfr/ffxiv-venture-bell?color=blue)](LICENSE)

> Your retainers come back whether the game is open or not.

A venture timer is only useful while you are logged in, which is the one time you
do not need it. The plugin sends the completion times to a small server; the
server holds the countdown and pushes a notification — game closed, PC asleep,
you at work.

```text
┌─────────────────┐   "Sultana is back at 19:42,    ┌──────────────┐
│  Venture Bell   │    Bubbles at 19:43"            │  venturebell │
│  (Dalamud)      │   ────────────────────────────> │  (server)    │
└─────────────────┘   at the bell, then only        └──────┬───────┘
                      when something changes               │ 19:42
                                                           v
                                                       2 ventures complete
                                                       Sultana — Quick Exploration
                                                       Bubbles — Hunting Exploration
```

Every tag ships the plugin zip, the container image and the Helm chart at the
same version, so there is no compatibility matrix.

## Install

Add to Dalamud's custom plugin repositories (`/xlsettings` → Experimental):

```text
https://raw.githubusercontent.com/linusfr/ffxiv-venture-bell/main/pluginmaster.json
```

Run the server (below), then put its address and token into `/venturebell`.
Until both are set the plugin sends nothing anywhere.

For Pushover you need your own application token — the quota is per
application, so a shipped one would be everyone's quota.
[Register one](https://pushover.net/apps/build), then set `PUSHOVER_TOKEN` and
`PUSHOVER_USER`. Priority defaults to `-1`: delivered, silent, and Pushover's
own quiet hours already work.

## Running the server

```sh
BELL_TOKEN=$(openssl rand -hex 24) PUSHOVER_TOKEN=… PUSHOVER_USER=… ./venturebell
```

Docker: `ghcr.io/linusfr/ffxiv-venture-bell`, state on `/data`. The image is
distroless and runs as uid 65532, so a bind mount needs `chown 65532:65532`; a
named volume sorts itself out.

Kubernetes:

```sh
kubectl create secret generic venturebell --from-literal=BELL_TOKEN=…
helm install venturebell oci://ghcr.io/linusfr/charts/venturebell \
  --version 1.4.0 --set existingSecret=venturebell
```

Chart `1.4.0` pulls image `1.4.0`. Rendering fails outright if neither
`existingSecret` nor `secret.bellToken` is set; the rest is in
[`chart/values.yaml`](chart/values.yaml). GHCR publishes new packages private,
so the first pull may need the visibility flipped.

## Configuration

| Variable | Default | |
|---|---|---|
| `BELL_TOKEN` | *required* | Shared secret the plugin sends as `Authorization: Bearer …` |
| `BELL_ADDR` | `127.0.0.1:8770` | Listen address; the image overrides it to `0.0.0.0:8770` |
| `BELL_STATE` | `state.json` | Where the timers live; `/data/state.json` in the image |
| `BELL_LEAD` | `0` | Notify this long *before* completion, e.g. `5m` |
| `BELL_COALESCE` | `1m` | Completions this close together share one notification |
| `BELL_STALE` | `6h` | Missed by more than this and it is dropped, not sent late |
| `PUSHOVER_TOKEN`, `PUSHOVER_USER` | — | Your application token and user key |
| `PUSHOVER_PRIORITY`, `_DEVICE`, `_SOUND` | `-1` | Priority `-2`…`1`; 2 needs an acknowledgement flow |
| `BELL_WEBHOOK_URL` | — | Also POST the notification as JSON here: ntfy, gotify, Home Assistant |
| `BELL_DEBUG` | unset | Debug logging |

Pushover, the webhook, both, or neither — neither means log-only, which is the
quickest way to test the plumbing.

## How it behaves

- **Absolute times, not durations.** A slow request or a restart cannot skew a
  countdown that was never relative
- **Each sync is the whole retainer list**, not an event. Reassign a venture,
  dismiss a retainer, play a second character — the next sync is the truth
- **One message per batch.** When a venture comes due, anything finishing within
  `BELL_COALESCE` rides along. Nothing is ever sent early on its own
- **Nothing you can already see**, and nothing from last Tuesday: a venture that
  had finished before the plugin synced is never pushed, and one missed by more
  than `BELL_STALE` is dropped
- **The plugin syncs at the summoning bell** — the only place the game hands the
  client real timers — and then every minute if something changed. A venture
  assigned with the plugin off is one the server never hears about
- **A failed notification is not retried forever.** Pushover gets three attempts,
  then it is logged as an error rather than re-sent all day

`/venturebell` opens the settings, `/venturebell sync` sends now,
`/venturebell status` reports the last attempt.

## API

`POST /sync` replaces what the server knows about one character. `GET /state`
shows what it believes, `GET /healthz` is unauthenticated for probes.

```jsonc
// Authorization: Bearer <BELL_TOKEN>
{
  "character": "Y'shtola@Phoenix",
  "retainers": [
    { "name": "Sultana", "venture": "Quick Exploration", "done_at": 1790283374 },
    { "name": "Coco" }
  ]
}
```

`done_at` is Unix seconds; omitting it means no venture is running. A
`BELL_WEBHOOK_URL` receives `{title, message, completed[]}`.

## Development

```sh
just run          # the server, log-only, on 127.0.0.1:8770
just sync 10      # pretend a bell: a retainer finishing in ten seconds
just install      # the plugin into ~/.xlcore/devPlugins for /xlplugins dev mode
just check        # everything CI runs
```

MIT. Not affiliated with Square Enix.
