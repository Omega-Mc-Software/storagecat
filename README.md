# StorageCat

Free, portable self-storage management for small facilities. One Windows exe, no install, no account, no cloud — your data stays in a SQLite file you own.

Built by [Neko Omega](https://neko.omegamc.uk/) (Omega-Mc-Software). Sibling tools: [LedgerCat](https://github.com/Omega-Mc-Software/ledgercat) (landlord bookkeeping) and [LogCat](https://github.com/Omega-Mc-Software/logcat) (plain-text daily log).

## What v0.1 does

- **Units outlive tenants** — generate units by size + quantity, occupy/vacate freely
- **Anniversary billing** — each unit bills on its own move-in day, not the 1st
- **Due now** — exact amount owed per unit today, late fee included, blank when clean
- **Delinquency aging** — 1–30 / 31–60 / 61–90 / 90+ day buckets
- **Waitlist per unit size** — when a unit frees up, the next name is there
- **Occupancy % + rent-roll CSV** — CSV/JSON import and export, always
- Notes collapsed to a 📝 icon; light/dark mode; About tab

Not in v0.1 (on purpose): gate integration, online payments, tenant portal, lien letters.

## Download

Grab the latest exe at https://neko.omegamc.uk/products/ (SHA-256 checksum alongside each download). Windows may show a SmartScreen warning for unsigned exes — the checksum is published so you can verify what you downloaded.

## Building from source

Requires the .NET 8 SDK (Windows targeting pack):

```
dotnet publish src/StorageCat.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Tag a release (`v*`) and GitHub Actions builds and uploads the exe automatically.

## License

MIT — see [LICENSE](LICENSE).
