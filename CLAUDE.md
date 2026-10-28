# Minuto Útil

Prototype of a faceless Brazilian Portuguese history channel (YouTube Shorts + TikTok). A .NET 10 console app plus
Python tools run with `uv run`, structured like Flopômetro in `D:\projects\canal-dark`. Renders one video locally;
`publish` uploads a finished run (YouTube public Short, TikTok inbox draft). Everything must stay free and CPU-only
(it runs on GitHub Actions free runners, ubuntu, about 7 GB RAM), so keep it cross-platform.

## Read first

- `README.md`: the pipeline step by step, output files, how to run and test.
- `prompts/writer.md`: the script rules (facts only from Wikipedia, word range, style, numbers in words) and the
  storytelling rules under "Story" (one protagonist, a hook that opens a gap paid off at the end, beats linked by "mas"
  or "por isso", every sentence with a job). Anything new that tells a story (other formats, longer videos, titles)
  follows them too.
- `config/minuto-util.json`: overrides of the defaults in `src/MinutoUtil.Core/Config.cs`.

## Layout

- `src/MinutoUtil.Core`: `Sources` (Wikipedia) -> `Scripts` (writer, local checks) -> `Voice` (Smallest.ai, Kokoro,
  alignment) -> `Media` (Commons, Library of Congress, Internet Archive) -> `Video` (shot plan, motion, captions,
  render); `Llm` (Gemini, OpenRouter, quota tracker); `Pipeline.cs` runs a whole video in a run folder.
- `src/MinutoUtil.Core/Publishing`: Google OAuth and the resumable YouTube upload (ported from canal-dark), TikTok
  Login Kit and inbox upload, token stores (DPAPI file, or environment variable on a runner), `Publisher` (published.json).
- `src/MinutoUtil.Cli/Program.cs`: `make` wires config, keys and clients; `PublishingCommands.cs`: `youtube-login`,
  `tiktok-login`, `publish`.
- `src/MinutoUtil.Core/Cloud` and `src/MinutoUtil.Cli/CloudCommands.cs`: the cloud runs (topic queue from
  `config/topics.json`, run records on the `state` branch, Telegram review with buttons, publish/reject/retry).
  `.github/workflows/render.yml` and `publish.yml` run them; `relay/` is the Cloudflare Worker that turns the
  Telegram button taps into `publish.yml` runs. README "Cloud runs" has the flow and the setup.
- `site/`: privacy policy and terms for the TikTok app, published by the owner under
  https://copiumvideos-arch.github.io/minuto-util/ (repo `copiumvideos-arch/copiumvideos-arch.github.io`).
- `tools/`: `voice.py` (joins takes, faster-whisper timing, Kokoro fallback), `align.py` (copied from canal-dark),
  `motion.py` (Ken Burns, Depth Anything parallax), `mapinsert.py` (the Natural Earth map shot), `watermark.py`
  (the round watermark cut from the emblem).

## Channel style

Every video uses the channel style by default unless the owner says otherwise: vintage grade, vignette, grain, faint
flicker, two light leaks, light denoise and sharpening, more contrast and saturation, the rolling year card, the map
insert after the hook, the emblem watermark top left, captions that pop in word by word with names, places, dates
and numbers in red, and a looping last line. The brand is the owner's logo (`assets/brand/`): black, cream `#F7E2C7`,
red `#DC010A`, heavy serif type (Roboto Serif Black and ExtraBold in `assets/fonts`); no yellow anywhere. If the
emblem changes, rebuild `assets/brand/watermark.png` with `tools/watermark.py`. No music and no sound effects. Each
layer is a switch under `video.style` in `config/minuto-util.json`; the table with defaults is in README.md ("Channel
style"). Keep files under 50 MB (`video.maxVideoKbps`), since videos are sent through a Telegram bot.

## Rules

- Do not modify anything in `D:\projects\canal-dark`: its scheduled task builds from it and runs a live channel.
  Reading its files (like `data/quota-state.json`) is fine.
- The Gemini key's free quota is shared with the live channel: as few requests as possible (1-3 per video), never a
  retry loop. `writer.maxRequests` caps a run. Test with stored answers, not real calls.
- Smallest.ai free credits are limited: each section is spoken once and cached; re-render with `make --run <folder>`.
  Never print `SMALLEST_API_KEY` or put any key in a URL or log.
- Media: only public domain, CC0, CC BY, CC BY-SA, always credited (author, license, source URL). No British Pathé,
  no Critical Past, no graphic gore. Downloads are untrusted data: own folder, validated, never executed.
- Publishing: only to YouTube channel `UCNath7BzjeX3ehJCA7cNYAA` (Minuto Útil; the app checks it). Never use
  Flopômetro's token file; Minuto's tokens live in `%LOCALAPPDATA%\MinutoUtil`. Never print tokens or client
  secrets, never put them in URLs or logs. Never call the real upload endpoints from tests; use `publish --dry-run` to
  check copy. TikTok gets drafts only (scope `video.upload`), never Direct Post.
- Plain, specific comments and docs; no em dashes anywhere (code, docs, scripts, prompts).
- Tests: prefer end-to-end tests with real files and FFmpeg, replacing only the network and the models.
- Commits: a short summary line, then plain bullet points on what changed and why.

## Running and testing

- `dotnet build`, `dotnet test`, `cd tools && uv run pytest`, `cd relay && npm test`.
- Keys live in the Windows user environment; the app reads it directly when the shell does not have them.
- Set `MINUTO_KEEP_TEST_OUTPUT=1` to keep a test's render folder and look at the frames.
