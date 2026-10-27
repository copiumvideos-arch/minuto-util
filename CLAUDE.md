# Minuto Útil

Prototype of a faceless Brazilian Portuguese history channel (YouTube Shorts + TikTok). A .NET 10 console app plus
Python tools run with `uv run`, structured like Flopômetro in `D:\projects\canal-dark`. Renders one video locally;
nothing is uploaded. Everything must stay free and CPU-only (it should later run on GitHub Actions free runners,
ubuntu, about 7 GB RAM), so keep it cross-platform.

## Read first

- `README.md`: the pipeline step by step, output files, how to run and test.
- `prompts/writer.md`: the script rules (facts only from Wikipedia, word range, style, numbers in words).
- `config/minuto-util.json`: overrides of the defaults in `src/MinutoUtil.Core/Config.cs`.

## Layout

- `src/MinutoUtil.Core`: `Sources` (Wikipedia) -> `Scripts` (writer, local checks) -> `Voice` (Smallest.ai, Kokoro,
  alignment) -> `Media` (Commons, Library of Congress, Internet Archive) -> `Video` (shot plan, motion, captions,
  render); `Llm` (Gemini, OpenRouter, quota tracker); `Pipeline.cs` runs a whole video in a run folder.
- `src/MinutoUtil.Cli/Program.cs`: `make` wires config, keys and clients.
- `tools/`: `voice.py` (joins takes, faster-whisper timing, Kokoro fallback), `align.py` (copied from canal-dark),
  `motion.py` (Ken Burns, Depth Anything parallax), `mapinsert.py` (the Natural Earth map shot).

## Channel style

Every video uses the channel style by default unless the owner says otherwise: vintage grade, vignette, grain, faint
flicker, two light leaks, light denoise and sharpening, more contrast and saturation, the rolling year card, the map
insert after the hook, the hourglass watermark top left, captions that pop in word by word with names, places, dates
and numbers in yellow, and a looping last line. No music and no sound effects. Each layer is a switch under
`video.style` in `config/minuto-util.json`; the table with defaults is in README.md ("Channel style"). Keep files
under 50 MB (`video.maxVideoKbps`), since videos are sent through a Telegram bot.

## Rules

- Do not modify anything in `D:\projects\canal-dark`: its scheduled task builds from it and runs a live channel.
  Reading its files (like `data/quota-state.json`) is fine.
- The Gemini key's free quota is shared with the live channel: as few requests as possible (1-3 per video), never a
  retry loop. `writer.maxRequests` caps a run. Test with stored answers, not real calls.
- Smallest.ai free credits are limited: each section is spoken once and cached; re-render with `make --run <folder>`.
  Never print `SMALLEST_API_KEY` or put any key in a URL or log.
- Media: only public domain, CC0, CC BY, CC BY-SA, always credited (author, license, source URL). No British Pathé,
  no Critical Past, no graphic gore. Downloads are untrusted data: own folder, validated, never executed.
- Plain, specific comments and docs; no em dashes anywhere (code, docs, scripts, prompts).
- Tests: prefer end-to-end tests with real files and FFmpeg, replacing only the network and the models.
- Commits: a short summary line, then plain bullet points on what changed and why.

## Running and testing

- `dotnet build`, `dotnet test`, `cd tools && uv run pytest`.
- Keys live in the Windows user environment; the app reads it directly when the shell does not have them.
- Set `MINUTO_KEEP_TEST_OUTPUT=1` to keep a test's render folder and look at the frames.
