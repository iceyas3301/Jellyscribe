# Jellyscribe — Iven's fork

A fork of [builtbyproxy/Jellyscribe](https://github.com/builtbyproxy/Jellyscribe) that adds one
feature: **reviews and ratings written in the [Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced)
plugin are posted to the matching Letterboxd (films) or Serializd (TV) diary.**

Everything else is upstream Jellyscribe, unmodified.

- Fork: `iceyas3301/Jellyscribe`
- Upstream: `builtbyproxy/Jellyscribe` (tracked as the `upstream` remote)

## Why this exists

Jellyfin Enhanced has a user-review feature: you can rate and write about a film or show from the
item page. Those reviews live in JE's own store
(`<config>/plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced/reviews.json`, keyed
`{userIdN}:{mediaType}:{tmdbId}[:s{n}[:e{n}]]`) and **JE never writes them into Jellyfin's
UserData rating field**.

Jellyscribe's existing sync paths read Jellyfin UserData (real-time playback handler, daily
catch-up, rating mirror). So a film rated only in the JE UI never reached Letterboxd or Serializd —
it existed on the Jellyfin side and nowhere else. On this server that was 78 reviews.

## What the fork adds

`LetterboxdSync/Enhanced/` plus `Api/EnhancedReviewController.cs`:

| File | Role |
|---|---|
| `EnhancedReviewKey.cs` | Parses JE's composite key into user + media type + TMDb id + season/episode. |
| `EnhancedReviewEntry.cs` | The review payload plus the rating/date mapping to both services. |
| `EnhancedReviewStore.cs` | Read-only, tolerant reader for JE's `reviews.json`. |
| `EnhancedReviewSyncState.cs` | Fingerprint-based dedupe + outcome memory (never stores review text). |
| `EnhancedReviewSyncRunner.cs` | The sync pass: read store → resolve the author's own accounts → post. |
| `EnhancedReviewSyncTask.cs` | Jellyfin scheduled task, every 15 minutes by default. |
| `Api/EnhancedReviewController.cs` | Admin `Status` + `SyncNow` endpoints. |

Plus: four settings on `PluginConfiguration`, one DI registration, and a **Jellyfin Enhanced
reviews** panel on the plugin's Integrations tab.

### Behaviour

- **Films → Letterboxd.** Posted through the same service Jellyscribe already uses, so the official
  API path is preferred and the scraping fallback applies. Dated to when the review was written.
- **TV shows and episodes → Serializd.** A show-level entry posts a show review; `…:s1:e2` posts an
  episode review. Ratings map JE's 1–5 stars to Serializd's 1–10.
- **Season-only entries are skipped** (`…:s1` with no episode). Serializd's API has no season-level
  review endpoint, and silently promoting a season rating to a show rating would overwrite a real
  show rating. Skipped entries are recorded once and never retried.
- **Only the author's own accounts receive a review.** An entry is matched to accounts whose
  `UserJellyfinId` is the JE author's, compared in GUID "N" form (a hand-edited config holding the
  dashed form still matches).
- **Editing re-syncs.** Each entry's content+rating+edit-time is hashed; a changed hash re-posts.
  An unchanged one is left alone.
- **No account linked yet ≠ dropped.** Those entries stay pending and post the moment an account is
  linked.
- **Failures retry up to `EnhancedReviewSyncMaxAttempts` (5)** then stop, so a broken provider isn't
  hammered every 15 minutes.

### Settings (Dashboard → Jellyscribe → Integrations → Jellyfin Enhanced reviews)

| Setting | Default | Meaning |
|---|---|---|
| Sync Jellyfin Enhanced reviews | **off** | Master switch. |
| Include reviews written before the sync was enabled | on | Untick for "only new reviews from now on". The cutoff timestamp is captured once and persisted, so toggling it never re-posts history. |
| reviews.json path | empty | Override only if JE's store isn't in its standard location. |
| `EnhancedReviewSyncMaxAttempts` (config file only) | 5 | Retry budget for failures. |

## Deliberate limitations

- **Spoiler flags aren't carried.** JE's review record has no spoiler field, so every post goes to
  Letterboxd with `containsSpoilers: false`.
- **Serializd entries are not backdated.** Serializd's show/episode review endpoint takes no date,
  so a backfilled Serializd entry lands on the run date. Letterboxd entries are dated correctly.
- **Activity rows use `TMDb <id>` as the title.** JE's store carries no title, and resolving one per
  entry would mean querying the library for every synced review. Rows are tagged with source
  `enhanced-review`.
- **Review text is never logged or persisted outside JE.** The sync-state file stores a SHA-256
  fingerprint, not the text; logs record length/booleans only, matching the rest of the plugin.

## Installing the fork

The fork keeps upstream's plugin GUID and name, so it **replaces** the stock Jellyscribe install —
you do not end up with two plugins.

The fork's `AssemblyVersion` is deliberately one minor line ahead of upstream (`2.4.0.0` vs
upstream's `2.3.1.0`) so Jellyfin's plugin manager never sees the stock build as an upgrade and
silently replaces the fork. Retarget the version line when upstream catches up.

```bash
# build
export PATH="$HOME/.dotnet:$PATH"
dotnet build -c Release

# deploy: DLL drop into <config>/plugins/Jellyscribe_<version>/, then restart Jellyfin
# (see deploy.sh in the repo for the container path used on this server)
```

Jellyfin's scheduled tasks list gains **Sync Jellyfin Enhanced reviews** (every 15 minutes, editable
there). It is a no-op until the setting is enabled.

The fork's own GitHub Actions are intentionally left alone — releases/manifests are upstream's job.
This fork is installed manually.

## Staying in sync with upstream

`~/.hermes/scripts/jellyscribe-fork-sync.sh` runs every two weeks via Hermes cron. Gates, in order:

1. working tree clean
2. upstream has new commits (otherwise a cheap no-op)
3. the merge is conflict-free
4. the fork's `AssemblyVersion` is still ahead of upstream's
5. the fork's feature files still exist (a merge can "succeed" while dropping them)
6. `dotnet build -c Release` succeeds
7. the full test suite passes (`Passed!` required)

Only then does it push to `origin/main`. Any failure rolls the merge back with
`git reset --hard <pre-merge sha>`, leaving the fork on the last known-good commit, and reports
what failed. Logs land in `~/.hermes/logs/jellyscribe-fork-sync/`.

A conflict (gate 3) or a version collision (gate 4) needs a human — by design, since both mean
upstream changed something structural.

## Verifying the fork by hand

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build -c Release
dotnet test  -c Release                       # full upstream suite + the Enhanced tests
dotnet test  -c Release --filter "FullyQualifiedName~Enhanced"
```

The feature's own tests live in `LetterboxdSync.Tests/Enhanced/` (68 tests): key parsing, rating
mapping, tolerant store reads, dedupe/retry semantics, routing by scope, the per-user privacy
boundary, and the admin endpoints.

If a review needs re-posting, delete its entry from
`<config>/plugins/configurations/jellyscribe-enhanced-review-state.json` (or the whole file to
re-post everything) and let the next run pick it up.
