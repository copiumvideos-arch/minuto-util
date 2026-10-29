# Minuto Útil

Prototype of a faceless Brazilian Portuguese history channel (YouTube Shorts + TikTok). A .NET 10 console app plus
Python tools run with `uv run`, structured like Flopômetro in `D:\projects\canal-dark`. Renders one video locally;
`publish` uploads a finished run (YouTube public Short, TikTok inbox draft). Everything must stay free and CPU-only
(it runs on GitHub Actions free runners, ubuntu, about 7 GB RAM), so keep it cross-platform.

## Read first

- `README.md`: the pipeline step by step, output files, how to run and test.
- `channels/minuto-util/prompts/writer.md`: the script rules (facts only from Wikipedia, word range, style, numbers in words) and the
  storytelling rules under "How the explanation moves" (one question opened by the hook and answered at the end, the
  common version turned, steps that cause or complicate each other in spoken links, sources named, doubt admitted,
  the end on the fact that answers the hook, no "Ou seja" wrap-up or irony), modeled on the Estranha História channel (`docs/scriptwriting.md` section 18).
  Anything new that tells a story (other formats, titles) follows them too.
- `docs/scriptwriting.md`: the why behind `channels/minuto-util/prompts/writer.md` (the Brazilian viewer, what the platforms reward,
  hooks, structure, writing for the voice, how to measure and test). Read it before changing a prompt or the word
  range; a lesson becomes a rule in the prompt and its reason goes in the guide.
- `channels/minuto-util/config.json`: overrides of the defaults in `src/MinutoUtil.Core/Config.cs`.
- README "Channels": one engine, several channels. Each channel's files are in `channels/<id>/` (config.json,
  topics.json, prompts/), every command takes `--channel <id>` (default `minuto-util`), and the `channel` block of its
  config names its secrets (`envPrefix`), sign-in folder, state folder, releases and schedule.

## Layout

- `channels/<id>/`: one folder per channel; `src/MinutoUtil.Core/Channel.cs` loads it and fills the names derived
  from the channel (environment variables, token files, watermark text).
- `src/MinutoUtil.Core`: `Sources` (Wikipedia) -> `Scripts` (writer, local checks) -> `Voice` (Smallest.ai, Kokoro,
  alignment) -> `Media` (Commons, Library of Congress, Internet Archive) -> `Video` (shot plan, motion, captions,
  render); `Llm` (Gemini, OpenRouter, quota tracker); `Pipeline.cs` runs a whole video in a run folder.
- `src/MinutoUtil.Core/Publishing`: Google OAuth and the resumable YouTube upload (ported from canal-dark), TikTok
  Login Kit and inbox upload, token stores (DPAPI file, or environment variable on a runner), `Publisher` (published.json).
- `src/MinutoUtil.Cli/Program.cs`: `make` wires config, keys and clients; `PublishingCommands.cs`: `youtube-login`,
  `tiktok-login`, `publish`.
- `src/MinutoUtil.Core/Cloud` and `src/MinutoUtil.Cli/CloudCommands.cs`: the cloud runs (topic queue from
  `channels/minuto-util/topics.json`, run records on the `state` branch, Telegram review with buttons, publish/reject/retry).
  `.github/workflows/render.yml`, `publish.yml` and `ideas.yml` run them for one channel per job
  (`tools/channels.py` picks it, `.github/scripts/cli.sh` passes its secrets); `relay/` is the Cloudflare Worker that
  turns each channel bot's taps into workflow runs for that channel. README "Cloud runs" has the flow and the setup.
- `site/`: privacy policy and terms for the TikTok app, published by the owner under
  https://copiumvideos-arch.github.io/minuto-util/ (repo `copiumvideos-arch/copiumvideos-arch.github.io`).
- `tools/`: `voice.py` (joins takes, faster-whisper timing, Kokoro fallback), `align.py` (copied from canal-dark),
  `motion.py` (Ken Burns, Depth Anything parallax), `mapinsert.py` (the Natural Earth map shot), `watermark.py`
  (the round watermark cut from the emblem), `channels.py` (which channels a workflow run is for), `trailer.py` (cuts
  game trailers on the narration for a channel with `media.footage` "trailers").
- Channels: `minuto-util` (history, archive pictures, the style below) and `profissao-spoiler` (games, official trailers
  from Steam or else YouTube through `Media/GameTrailers.cs`, facts-only scripts, Montserrat captions; README "Trailer channels").

## Channel style

Minuto Útil's look (Profissão Spoiler has its own switches in its config). Every video uses the channel style by default unless the owner says otherwise: vintage grade, vignette, grain, faint
flicker, two light leaks, light denoise and sharpening, more contrast and saturation, the rolling year card, the map
insert after the hook, the emblem watermark top left, captions that pop in word by word with names, places, dates
and numbers in red, a looping last line, and a thumbnail in the same look (`thumbnail.jpg`: the year in red and the
script's `thumbnailText` with one red word, over an AI painting of the script's `thumbnailScene` from Cloudflare Workers
AI, or the archive picture when that fails; set on YouTube, and also the video's first frame so TikTok's cover picker
finds it at the start). The brand is the owner's logo (`assets/brand/`): black, cream `#F7E2C7`,
red `#DC010A`, heavy serif type (Roboto Serif Black and ExtraBold in `assets/fonts`); no yellow anywhere. If the
emblem changes, rebuild `assets/brand/watermark.png` with `tools/watermark.py`. No music and no sound effects. Each
layer is a switch under `video.style` in `channels/minuto-util/config.json`; the table with defaults is in README.md ("Channel
style"). Keep files under 50 MB (`video.maxVideoKbps`), since videos are sent through a Telegram bot.

## Rules

- Do not modify anything in `D:\projects\canal-dark`: its scheduled task builds from it and runs a live channel.
  Reading its files (like `data/quota-state.json`) is fine.
- The Gemini key's free quota is shared with the live channel: as few requests as possible (1-3 per video), never a
  retry loop. `writer.maxRequests` caps a run's Gemini requests (the spares have `writer.maxSpareRequests`). Test
  with stored answers, not real calls.
- Smallest.ai free credits are limited: each section is spoken once and cached; re-render with `make --run <folder>`.
  Never print `SMALLEST_API_KEY` or put any key in a URL or log.
- Media: only public domain, CC0, CC BY, CC BY-SA, always credited (author, license, source URL). No British Pathé,
  no Critical Past, no graphic gore. The one exception is a trailer channel (Profissão Spoiler): the game's official
  trailers (Steam, or official YouTube channels), credited, by the owner's choice. Downloads are untrusted data: own folder, validated, never executed.
- Channels never mix: each has its own secrets (`envPrefix`), sign-ins (`tokenFolder`), state folder and releases,
  and nothing channel-specific falls back to Minuto Útil's values. Minuto Útil's names, sealed tokens and state layout
  must stay exactly as they are (`ChannelTests.cs` guards them); workflows reach channel secrets only through
  `secrets[format('{0}_...', prefix)]`.
- Publishing: each channel only to its own `youtube.channelId` (Minuto Útil: `UCNath7BzjeX3ehJCA7cNYAA`; the app
  checks it). Never use Flopômetro's token file; Minuto's tokens live in `%LOCALAPPDATA%\MinutoUtil`. Never print tokens or client
  secrets, never put them in URLs or logs. Never call the real upload endpoints from tests; use `publish --dry-run` to
  check copy. TikTok gets drafts only (scope `video.upload`), never Direct Post.
- Plain, specific comments and docs; no em dashes anywhere (code, docs, scripts, prompts).
- Tests: prefer end-to-end tests with real files and FFmpeg, replacing only the network and the models.
- Commits: a short summary line, then plain bullet points on what changed and why.

## Running and testing

- `dotnet build`, `dotnet test`, `cd tools && uv run pytest`, `cd relay && npm test`.
- Keys live in the Windows user environment; the app reads it directly when the shell does not have them.
- Set `MINUTO_KEEP_TEST_OUTPUT=1` to keep a test's render folder and look at the frames.
