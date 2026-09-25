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

Run the server (below), then put its address and token into `/venturebell`,
which shows a green dot once the server answers. Then add your Pushover
credentials, and you are done.

**The server holds no Pushover account.** Both halves — the application token
and your user key — live in the plugin and travel with each sync. That is what
makes a shared server safe in both directions: your friends' ventures can never
arrive on the operator's phone, and nobody's notifications come out of anybody
else's quota. Point a friend at your server and all they need is the URL, the
`BELL_TOKEN`, and their own Pushover setup.

[Register an application](https://pushover.net/apps/build) — any name, and its
icon is what shows on your lock screen — then paste its API token and your user
key (top right of the dashboard) into the plugin. Priority defaults to `-1`:
delivered, silent, and Pushover's own quiet hours already work.

## Running the server

```sh
BELL_TOKEN=$(openssl rand -hex 24) ./venturebell
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
| `BELL_TOKEN` | *required* | Shared secret the plugin sends as `Authorization: Bearer …`. Everyone syncing to one server presents the same one, so hand it only to people you would also hand your Pushover quota |
| `BELL_ADDR` | `127.0.0.1:8770` | Listen address; the image overrides it to `0.0.0.0:8770` |
| `BELL_STATE` | `state.json` | Where the timers live; `/data/state.json` in the image |
| `BELL_LEAD` | `0` | Notify this long *before* completion, e.g. `5m` |
| `BELL_COALESCE` | `1m` | Completions this close together share one notification |
| `BELL_STALE` | `6h` | Missed by more than this and it is dropped, not sent late |
| `PUSHOVER_PRIORITY` | `-1` | Priority `-2`…`1` for every message this server sends; 2 needs an acknowledgement flow |
| `BELL_NOTIFY_START` | unset | Any value also announces ventures as they are assigned, with the time they are back |
| `TZ` | UTC | Zone for those times. The image carries its own tz database, so any IANA name works; the chart defaults to `Europe/Berlin` |
| `BELL_WEBHOOK_URL` | — | Also POST the notification as JSON here: ntfy, gotify, Home Assistant |
| `BELL_DEBUG` | unset | Debug logging |

`BELL_WEBHOOK_URL` is the operator's own relay and has no notion of a
recipient, so on a shared server it sees everyone's notifications. Leave it
unset unless that is what you want.

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
- **Start notifications are opt-in and quiet by default.** You are at the bell
  when you assign a venture, so the news is the time it is back, not the event.
  The first sync for a character never announces starts — installing the plugin
  with eight ventures running should not push eight "started"
- **One notification per destination.** Completions are grouped by the
  credentials their plugin registered, so two people never share a message.
  `/state` shows only *that* a character has credentials, never what they are —
  one shared `BELL_TOKEN` must not be a way to read other people's
- **A failed notification is not retried forever.** Pushover gets three attempts,
  then it is logged as an error rather than re-sent all day

`/venturebell` opens the settings, `/venturebell sync` sends now,
`/venturebell status` reports the last attempt. The settings window checks the
connection whenever it opens, and every button reports under "Last action".

## API

`POST /sync` replaces what the server knows about one character. `POST /test`
sends one notification immediately with the credentials in the body and stores
nothing — it is what the plugin's "Send a test notification" button calls, and it
answers with Pushover's own error when a key is wrong. `GET /state` shows what
the server believes, `GET /healthz` is unauthenticated for probes.

```jsonc
// Authorization: Bearer <BELL_TOKEN>
{
  "character": "Y'shtola@Phoenix",
  "retainers": [
    { "name": "Sultana", "venture": "Quick Exploration", "done_at": 1790283374 },
    { "name": "Coco" }
  ],
  "pushover": { "token": "axxxxxxxx", "user": "uxxxxxxxx" }
}
```

`done_at` is Unix seconds; omitting it means no venture is running. `pushover`
carries both halves or neither — the server cannot complete a pair it does not
have. It is replaced on every sync, so clearing it in the plugin clears it here,
and a character without it gets a warning in the log rather than a notification. A
`BELL_WEBHOOK_URL` receives `{title, message, completed[]}`.

## Development

```sh
just run          # the server, log-only, on 127.0.0.1:8770
just sync 10      # pretend a bell: a retainer finishing in ten seconds
just install      # the plugin into ~/.xlcore/devPlugins for /xlplugins dev mode
just check        # everything CI runs
```

MIT. Not affiliated with Square Enix.
