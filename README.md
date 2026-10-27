# Minuto Útil

Prototype of a faceless Brazilian Portuguese history channel for YouTube Shorts and TikTok: one real event, under a
minute, calm narration over archive photos and footage. This repository renders a video locally. Nothing is uploaded.
Everything is free: Wikipedia, free model tiers, Smallest.ai's free plan, public domain and Creative Commons media,
and CPU-only local tools (no GPU anywhere, so it can run on a GitHub Actions free runner).

Built like Flopômetro (`D:\projects\canal-dark`): a .NET 10 console app that calls small Python tools in `tools/`
through `uv run`. The Gemini and OpenRouter clients, the quota tracker, the Whisper alignment (`tools/align.py`) and
the process runner were copied from there and adapted.

## Pipeline

```
Wikipedia (pt + en article text) -> script (Gemini free tier, OpenRouter free models as spare)
  -> voice (Smallest.ai "thiago", one take per section; Kokoro pm_santa as fallback)
  -> word timings (faster-whisper small, CPU, int8, matched to the script)
  -> shots (one per section, two when a section is long) -> media (Commons, Library of Congress, Internet Archive)
  -> motion (Ken Burns, 2.5D parallax from Depth Anything V2 Small, real footage clip)
  -> captions (ASS, 2-4 words, spoken word highlighted) -> FFmpeg -> video.mp4 + review files
```

1. **Sources** (`Sources/Wikipedia.cs`): the English article from `story.englishArticle` and its Portuguese twin
   (by language link), as plain text from the MediaWiki Action API, with a descriptive User-Agent. Reference sections
   are cut off. Saved as `sources.json`.
2. **Script** (`Scripts/ScriptWriter.cs`, `prompts/writer.md`): one request. The writer may only use facts from the
   source text, and must copy, for each section, one sentence of the source that backs it (`sourceQuote`); the app
   checks every quote is really in the text and logs the ones that are not. Dashes are replaced with commas, digits
   left in the narration are flagged and spelled out for the voice (`Scripts/SpokenText.cs`, pt-BR numbers). Only a
   script far outside the word range costs a second request.
3. **Model requests**: the Gemini key's free quota is shared with the live Flopômetro channel. Each Gemini model
   (`gemini.models`, the same ids as Flopômetro) is asked once at most, never retried; a busy model hands over to the
   next, a daily quota is remembered in `data/quota-state.json`, and Flopômetro's own quota file is read (read only,
   `sharedQuotaFiles`) so a model the live channel used up is skipped without a request. OpenRouter's free writers
   come after Gemini. `writer.maxRequests` (3) caps the whole run.
4. **Voice** (`Voice/SmallestVoice.cs`, `Voice/Narrator.cs`, `tools/voice.py`): Smallest.ai Waves
   (`lightning_v3.1_pro`, voice `thiago`, 24 kHz WAV), one request per section, each take cached next to its text in
   `voice/smallest/`. If Smallest fails or runs out of credits, Kokoro `pm_santa` speaks every section instead (never
   a mix of voices). `voice.py assemble` trims each take, caps long pauses, joins them with a fixed pause and times
   every word with faster-whisper (a 16 kHz float32 array, not a file path), matched to the script by `align.py`.
   Captions keep the script's spelling; a section Whisper heard poorly is logged.
5. **Shots** (`Video/ShotPlanner.cs`): cuts fall in the pause before each section. A section longer than
   `video.maxShotSeconds` gets two shots, split after a comma or period near its middle. The first shot is real
   footage when a clip is found. Still shots alternate parallax and Ken Burns, with alternating directions.
6. **Media** (`Media/*`): each section's English (then Portuguese) queries go to Wikimedia Commons and the Library of
   Congress; footage comes from the Internet Archive's FedFlix collection (US National Archives films, public domain
   as US government works), then Commons videos. Only public domain, CC0, CC BY and CC BY-SA pass
   (`LicensePolicy`); titles with words pointing at graphic content are refused; pictures from the event's year rank
   higher, memorial and museum photos lower. Downloads are untrusted: they go into the run's `media/` folder, must
   have JPEG, PNG or WebP magic bytes, pass ffprobe at a usable size, and near duplicates (difference hash) are
   dropped. FFmpeg reads footage only over https from archive.org or upload.wikimedia.org, cuts a few seconds at
   three spots and keeps the brightest, most contrasty stretch with no hard cut. British Pathé and Critical Past
   are never used. Every item keeps its author, license and source page for the credits.
7. **Motion** (`tools/motion.py`): stills are cropped to fill 9:16 (no bars).
   - Ken Burns: a slow zoom with a pan, each frame an exact sub-pixel warp rendered at 2x and scaled down
     (`video.motionSupersample`), so it neither jitters nor shimmers.
   - Parallax: Depth Anything V2 Small (transformers, CPU) estimates depth; the camera slides sideways while the
     mid-depth plane stays put, so the foreground and background drift apart. Every output pixel samples backwards
     through a depth map that is widened around near objects and blurred, which leaves no holes and puts the stretch
     on the background beside an edge instead of warping the subject. The depth map is cached next to the picture.
   - Footage at 1080p fills the frame; smaller footage sits full width over a blurred copy of itself.
8. **Render** (`Video/Renderer.cs`, `Video/Captions.cs`): each shot is its own file (cached by its inputs), joined
   with 0.35 s cross-fades centered on the cuts; the narration is brought to -16 LUFS; captions are Montserrat Black,
   2-4 words, the spoken word in yellow, centered in the lower third clear of the TikTok and Shorts buttons and
   caption area; the event's date and place sit on the first shot. H.264 + AAC, 1080x1920, 30 fps.

## Output

`output/<yyyy-MM-dd>/<HHmm>/`:

| File | What |
|---|---|
| `video.mp4` | the video |
| `script.json` | the script as written (after local cleanup); `script-writer.json` says which model wrote it and the warnings |
| `metadata.json` | title, description with "Fonte: Wikipedia" and a "Créditos" list of every media item, hashtags |
| `timings.json` | every word, section and shot in seconds, and Whisper's alignment score per section |
| `contact-sheet.png` | one frame every 3 seconds, for review |
| `run.log` | the run's log, ending with model requests and Smallest characters used |
| `sources.json`, `voice/`, `media/`, `shots/`, `captions.ass` | intermediate files, reused by a re-run |

## Running

Needs the .NET 10 SDK, FFmpeg (with libass) and uv. Keys in the environment (on Windows the user environment is read
directly, so an old shell works too): `GEMINI_API_KEY`, `OPENROUTER_API_KEY` (optional spare), `SMALLEST_API_KEY`.

```powershell
cd tools; uv sync; cd ..                               # once: Python tools (CPU torch, faster-whisper, Kokoro)
dotnet run --project src/MinutoUtil.Cli -- make         # a new video in output/<date>/<time>/
dotnet run --project src/MinutoUtil.Cli -- make --run output/2026-10-27/1114   # re-render a run, no new API calls
dotnet run --project src/MinutoUtil.Cli -- make --article "Attack on Pearl Harbor"  # another event
```

To redo one step of a run, delete its file and re-run the folder: `script.json` (one new model request),
`voice/smallest/section-NN.*` (that section is spoken again), `media/manifest.json` (media searched again),
`shots/` (all shots rendered again). To drop a bad picture after review, remove its entry from `media/manifest.json`
and re-run the folder: the section's spare picture (each section downloads one more than it needs) takes its place.

## Known limits

- loc.gov answers API clients from this PC with a Cloudflare challenge (403); the Library of Congress source then
  turns itself off for the run. Commons and the Internet Archive supply everything today.
- Wikimedia rate-limits downloads of original files from this IPv4 address (429, Retry-After 600); only standard
  thumbnail widths (960, 1280, 1920, 3840) are fetched.
- Picture choice is search rank plus metadata (year, title words); nothing looks at the pixels for relevance. A
  scanned negative or a modern photo without a date can slip through: review the contact sheet.
- The crop aims at the near-pixel centroid of the depth map; sometimes that is a foreground helmet, not a face.

Settings live in `config/minuto-util.json` (defaults in `src/MinutoUtil.Core/Config.cs`), the writer's instructions in
`prompts/writer.md`.

## Tests

```powershell
dotnet test                       # about 30 s; the render test needs FFmpeg and the tools/ environment
cd tools; uv run pytest           # motion and voice tools
```

The render test runs the whole pipeline on real files with real FFmpeg and the real motion tool, replacing only the
network (Wikipedia, the writer, Smallest.ai answers with generated tones) and the models (generated depth maps, no
Whisper). Script tests parse a stored Gemini answer; the motion tests check on a synthetic scene that near pixels move
more than far ones and that Ken Burns glides without jumps.
