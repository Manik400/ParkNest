# ops

The pieces that watch the system rather than run it.

```bash
docker compose up -d prometheus alertmanager alert-sink grafana
```

Grafana is at <http://localhost:3000>. Anonymous viewing is on, so the dashboard opens without a
login; sign in as `parknest` / `parknest` to edit. Prometheus is at <http://localhost:9090>.

| Path | What it is |
|---|---|
| `prometheus/prometheus.yml` | Scrape config. Targets the API on the **host**, not a container — it runs from the IDE or `dotnet run`. |
| `prometheus/alerts.yml` | Three rules: ledger drift, shortfall rate, dispute rate. |
| `alertmanager/alertmanager.yml` | Where a firing rule goes: grouping, repeat intervals, receiver. |
| `grafana/provisioning/` | Datasource and dashboard providers, so a fresh volume comes up configured. |
| `grafana/dashboards/parknest-overview.json` | The dashboard, in the order PRD §17 asks the questions: is the money right, is the marketplace alive, is the API healthy. |

## The one number that matters

`parknest_reconciliation_drift_credits` should be exactly zero. Wallet balances are a cached
projection of the ledger, and the hourly sweep replays every wallet from its entries to check.
Anything above zero means the projection and the truth disagree about someone's money — that is an
incident, not a slow day, and the alert fires as critical after five minutes.

The panel next to it is the wallet count from the same sweep, and it is there on purpose: zero
drift across zero wallets is not good news.

## Empty graphs

The dashboard reads live counters, which start at zero on every API restart and are not persisted.
A dashboard with nothing on it usually means one of:

- the API is not running, or is not on `:5109` — check `Targets` in Prometheus,
- nothing has happened yet: book a session in the app and the marketplace row fills in,
- Docker on Linux without `host.docker.internal` — the compose file maps it, older Docker does not.

## Alerts

Alertmanager is at <http://localhost:9093>. Everything routes to `alert-sink` by default, a
container that prints what it receives:

```bash
docker compose logs -f alert-sink
```

That is deliberate. A Slack URL nobody has filled in produces a delivery path that fails silently
at the far end, which is worse than no path at all — this way `docker compose up` gives something
that demonstrably works, and you can see whether an alert actually left Alertmanager.

To route somewhere real, uncomment the `slack` receiver in `alertmanager/alertmanager.yml`, supply
the webhook URL, and point the route at it. Critical alerts — ledger drift — go out immediately and
repeat hourly; warnings are grouped and repeat every four hours. A firing critical suppresses the
warnings beneath it, so one incident is one page rather than three.

## What is not here

**Who is on call.** The routing works; the rota is a decision no configuration file can make. Until
somebody owns the pager, a critical alert reaches a container log and stops there.
