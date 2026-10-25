<!-- CAT-COUNTER:START -->
<p align="center">
  <img src="https://count.himiku.com/@chunma-discord-companio-16a7c0?theme=capoo-2&amp;padding=6&amp;scale=0.8&amp;darkmode=1" alt="Cute cat project visits counter" />
</p>


<!-- CAT-COUNTER:END -->

<!-- typing-title:start -->
<p align="center">
  <a href="https://github.com/DenverCoder1/readme-typing-svg">
    <img src="https://readme-typing-svg.demolab.com?font=Fira+Code&amp;weight=600&amp;size=28&amp;duration=3500&amp;pause=1800&amp;color=EF4444&amp;center=true&amp;vCenter=true&amp;width=760&amp;height=65&amp;repeat=true&amp;lines=Great+Love+Immortal+Venerable;Heaven+Refining+Demon+Venerable" alt="Great Love Immortal Venerable / Heaven Refining Demon Venerable" />
  </a>
</p>
<!-- typing-title:end -->

<!-- discord-presence:start -->
<p align="center">
  <a href="https://discord.com/users/891081797318897715">
    <img src="https://lanyard.cnrad.dev/api/891081797318897715?theme=dark&amp;bg=100b0d&amp;borderRadius=16px&amp;showDisplayName=true&amp;animated=true&amp;animatedDecoration=true" alt="Discord profile and live activity" />
  </a>
</p>
<!-- discord-presence:end -->

<!-- project-navigation:start -->
<p align="center"><a href="https://github.com/chunma-san">Profile &amp; projects</a> · <a href="#quick-start">Quick start</a> · <a href="https://github.com/chunma-san/discord-companion/issues">Issues</a></p>
<!-- project-navigation:end -->

# Discord Companion

An anime and manga companion with chapter notifications, GrapheneOS updates, a private link library and optional owner-only host monitoring.

**Runtime:** .NET 10 · **Configuration:** environment variables · **Deployment:** self-hosted

## Quick start

Install the .NET 10 SDK for local development, or Docker with Compose for container deployment.

```sh
cp bot.env.example bot.env
# Edit bot.env and fill the required credentials and IDs locally.
mkdir -p data
# Ensure data/ is writable by the container user (default UID/GID 1654).
docker compose up -d --build
```

For local development, export the variables from your environment and use `dotnet run`.

```sh
dotnet build --configuration Release
dotnet run --configuration Release -- --self-test
# Linux only:
python3 test_host_broker.py
```

The host bridge is optional and Linux-only. `install_host_bridge.py` creates a dedicated service account and a system service; it requires root and must be reviewed before use. Configure bridge variables through a systemd environment file or service override. Grant only the service names and container names you intend to expose. Keep the socket accessible only to the bot service account.

## Usage

Public commands include `/anime`, `/manga`, `/anilist`, `/theme`, `/calendar`, `/ping`, `/choose` and `/help`. Owner-only groups include `/chapters`, `/library`, `/server`, `/docker`, `/service` and `/voice-timer`. Configure OWNER_ID and GUILD_ID before starting.

## Command groups

| Command | Purpose |
| --- | --- |
| `/chapters` | Owner: track new manhua and novel chapters |
| `/anime` | Search anime on AniList |
| `/manga` | Search manga on AniList |
| `/anilist` | Look up an AniList user |
| `/theme` | Find anime openings and endings |
| `/calendar` | Browse the anime airing calendar |
| `/ping` | Check that the bot responds |
| `/choose` | Choose one item from a comma-separated list |
| `/help` | See the available commands |
| `/server` | Owner-only home server information |
| `/docker` | Owner-only container information |
| `/service` | Owner-only service information |
| `/voice-timer` | Control a configured voice announcement timer |
| `/teamspeak` | TeamSpeak bot information |
| `/navidrome` | Music library service information |
| `/immich` | Photo service information |
| `/library` | Owner: your organized saved links and descriptions |

Discord provides subcommands and argument descriptions when you type a command.

## Configuration

`.env.example` contains placeholders only. Real credentials, IDs, storage locations and account settings belong in your private local configuration.

| Variable | Requirement |
| --- | --- |
| `DISCORD_TOKEN` | Discord bot token; keep it secret. |
| `EXPECTED_STORAGE_UUID` | Expected UUID of the configured storage mount. |
| `GLIV_GID` | Container process GID; configure in the Compose .env file. |
| `GLIV_UID` | Container process UID; configure in the Compose .env file. |
| `GRAPHENEOS_CHANNEL_ID` | Destination for GrapheneOS news; omitted disables that destination. |
| `GUILD_ID` | Guild allowed to use owner commands. |
| `HOST_NETWORK_INTERFACE` | Network interface inspected by the host bridge. |
| `HOST_SERVICE_ALLOWLIST` | JSON map from allowed service labels to systemd unit names without .service. |
| `HOST_SOCKET` | Unix socket used by the optional host bridge. |
| `MANHUA_CHANNEL_ID` | Destination for comic updates. |
| `NOVEL_CHANNEL_ID` | Destination for novel updates. |
| `OWNER_ID` | Discord user allowed to run owner commands. |
| `READING_DATA_DIR` | Directory for reading state and the optional private library. |
| `STORAGE_MOUNT` | Extra storage mount inspected by the bridge. |
| `TS_BOT_CONTAINERS` | JSON array of audio-bot container names. |

## How it works and operational notes

Reading subscriptions and deduplication state are stored under READING_DATA_DIR. HTML source layouts can change. The optional Linux host bridge runs privileged fixed operations over a Unix socket; inspect its allowlist and installer before enabling it. Anime and feed features do not require installing the bridge.

## Project layout

- Source files implement the application and integrations.
- `requirements.txt` or the project file declares dependencies.
- `.env.example` documents configuration names without live values.
- `.gitignore` excludes credentials, sessions, generated data and build output.

## Verification

See [VALIDATION.md](VALIDATION.md) for the checks performed on this publication snapshot and the remaining deployment checks. A source check is not an end-to-end test of Discord, Telegram, music sites or your host.

## Privacy and upstream services

This repository is a configurable source distribution. It does not include a production database, personal saved-link collection, message archive, credentials, media library or existing Git history. Review local changes before committing them.

Third-party APIs, libraries and products remain the work of their respective maintainers. Review their terms and licenses before redistribution or deployment.

<!-- project-maintainer-credit -->
## Contributors

- [chunma-san](https://github.com/chunma-san) — Project maintenance and development.

Original contributors retain their authorship and license notices. See the repository history for individual contributions.

## Kol Novel listings

Chapter notifications support Kol Novel’s editorial chapter cards, including cover images and stable link-based deduplication. After migrating an installation with a long-failing Kol Novel subscription, stop its normal worker and run the image with `--reading-refresh-baseline` against the same writable data mount, using the existing data owner. This records current Kol Novel entries silently while retaining prior delivery history. Start the normal worker again afterward.

Publisher verification pages are reported explicitly as source failures. Delivery history is retained, and normal checks retry later; the bot does not treat these pages as an empty successful listing.
