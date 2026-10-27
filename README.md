# Minuto Útil

Prototype of a faceless Brazilian Portuguese history channel for YouTube Shorts and TikTok: one real event, under a
minute, calm narration over archive photos and footage. This repository renders a video locally; a separate `publish` command uploads a finished one.
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
   with 0.35 s cross-fades centered on the cuts; the narration is brought to -16 LUFS; captions are Roboto Serif
   Black, 2-4 words, cream with key words in red, centered in the lower third clear of the TikTok and Shorts buttons
   and caption area; the event's date and place sit on the first shot. H.264 + AAC, 1080x1920, 30 fps.

## Channel style (the standard look)

Every Minuto Útil video uses this style by default, unless the owner says otherwise. Each layer is a switch in
`video.style` in `config/minuto-util.json` (defaults in `StyleOptions`, `src/MinutoUtil.Core/Config.cs`). Picture
layers sit under the captions and the watermark; captions, year card and watermark are drawn last, untouched.

The brand comes from the owner's logo (`assets/brand/emblem.png`, `assets/brand/youtube-banner.png`): black, cream
`#F7E2C7` and red `#DC010A`, with heavy serif lettering. Text is Roboto Serif (`assets/fonts`, SIL Open Font License,
static instances renamed "Roboto Serif Black" and "Roboto Serif ExtraBold" so libass finds them by name in the run's
`fonts/` folder): Black for captions, the year and the map label, ExtraBold for the place line, the plain date stamp
and the watermark text. The two colors are `textColor` and `highlightColor` in `video.style`.

| Layer | What it looks like | Switch (default) |
|---|---|---|
| Vintage grade | faded blacks and whites, a faint warm push in the mid tones (black-and-white footage stays nearly neutral), so 1944 footage and color photos read as one film | `vintage` (true), `warmth` (0.035) |
| Vignette | darker edges, bright center, visible but not heavy | `vignette` (0.6, radians; 0 = off) |
| Film grain | fine luma grain changing every frame, added last so it is never sharpened | `grain` (7; 0 = off) |
| Flicker | a very light brightness flicker (part of `vintage`) | `vintage` |
| Light leaks | a warm glow from one side over two cross-fades (about a third and two thirds in, never the clip or the map) | `lightLeaks` (true) |
| Sharpening ("nítido") | a light denoise, then moderate unsharp on luma, before the grain, no halos | `denoise` (true), `sharpen` (0.7; 0 = off) |
| Contrast and color | a bit more contrast and saturation | `contrast` (1.12), `saturation` (1.18) |
| Year card | the year in big red Roboto Serif Black with a black outline, its last two digits rolling up like a counter from frame 0, then the place under it in cream, over the opening footage; replaces the plain date stamp | `yearCard` (true), `yearCardSeconds` (1.8) |
| Map insert | a 2-3 s shot right after the hook: an old-atlas map zooming from Europe into the place, with a red glow, a red pin and the place name in red (`highlightColor`). Drawn by `tools/mapinsert.py` from Natural Earth (public domain, downloaded once into `assets/cache/naturalearth`, validated as untrusted data). Needs `location` (name, latitude, longitude) in the script | `mapInsert` (true), `mapSeconds` (2.6) |
| Watermark | the channel emblem (96 px, cut out round from `emblem.png` by `tools/watermark.py` into `assets/brand/watermark.png`, overlaid by FFmpeg) and "Minuto Útil" in cream serif, both about 65% opaque, top left (clear of the TikTok and Shorts buttons). If the picture is missing, a vector hourglass is drawn instead | `watermark` (true), `watermarkImage` |
| Caption pop | each word appears as it is spoken and pops from 125% to 100% in 0.11 s; the line does not shift as it fills | `wordPop` (true) |
| Red key words | names, places, dates, months and numbers in the brand red, the rest cream, all with a thick black outline so red stays readable on dark footage. Long lines are squeezed using per-letter widths measured from libass, so they never cross the safe margins | `keyWords` (true), `textColor` (#F7E2C7), `highlightColor` (#DC010A) |
| Looping ending | the writer ends on a line that leads back into the hook, so the Short loops (`prompts/writer.md`) | prompt rule |
| No music, no sound effects | narration only; the owner finds music and effects distracting | none: there is no music path |

The file stays under 50 MB (Telegram's bot upload limit) through `video.maxVideoKbps` (4500).

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
| `published.json`, `tiktok-caption.txt` | written by `publish`: where the video went, and the caption to paste in TikTok |
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

## Publishing

`publish` sends a finished run folder to YouTube as a public Short and to the owner's TikTok inbox as a draft. It is a
separate command: `make` never uploads anything.

```powershell
dotnet run --project src/MinutoUtil.Cli -- publish --run output/2026-10-27/1114-v2 --dry-run   # shows what would be sent, calls nothing
dotnet run --project src/MinutoUtil.Cli -- publish --run output/2026-10-27/1114-v2 --youtube   # YouTube only
dotnet run --project src/MinutoUtil.Cli -- publish --run output/2026-10-27/1114-v2 --tiktok    # TikTok only
dotnet run --project src/MinutoUtil.Cli -- publish --run output/2026-10-27/1114-v2             # both
```

- **YouTube** (`Publishing/YouTubeUploader.cs`, resumable upload copied from Flopômetro): title from `metadata.json`
  (at most 100 characters, cut at a word; " #shorts" is added only when it fits), the description as written (Windows
  line ends and angle brackets removed; over YouTube's 5000 bytes, public domain and CC0 credit lines are folded into
  one "Mais N itens de domínio público" line, CC BY and CC BY-SA credits are never dropped: the command refuses
  instead), the hashtags as tags, category 27 (Education), `pt-BR`, public, not made for kids, no "altered or synthetic
  content" flag (archive photos with an AI narrator reading facts are not realistic synthetic depictions). All of these
  are under `youtube` in `config/minuto-util.json`. Before uploading, the token's channel is read
  (`channels?mine=true`) and anything but `youtube.channelId` (`UCNath7BzjeX3ehJCA7cNYAA`, Minuto Útil) is refused,
  so a Flopômetro sign-in can never post here. YouTube may lock uploads as private while the Google Cloud project has
  not passed its API audit; the command warns when the answered privacy is not "public".
- **TikTok** (`Publishing/TikTokUploader.cs`, Content Posting API, upload to inbox, scope `video.upload`): init with
  `FILE_UPLOAD`, the file PUT to the returned `upload_url` (one chunk up to 64,000,000 bytes, otherwise 20,000,000-byte
  chunks with the remainder on the last one), then the status is polled every 5 s until TikTok says
  `SEND_TO_USER_INBOX` (up to `tiktok.statusWaitSeconds`). The owner opens the inbox notification in the TikTok app and
  finishes the post (caption, sound, visibility). The inbox flow has no caption field, so the caption (title plus
  hashtags, at most 2200 characters) is written to `tiktok-caption.txt` and, when `MINUTO_TELEGRAM_BOT_TOKEN` and
  `MINUTO_TELEGRAM_CHAT_ID` are set, sent to the owner's Telegram as its own message to copy
  (`Publishing/TelegramNotifier.cs`; a Telegram failure is only a warning). TikTok allows 5 drafts waiting in the
  inbox per 24 hours. The drafts appear only in the phone app: Inbox, System notifications.
- Results go to `published.json` in the run folder (YouTube video id, URL, channel, privacy, time; TikTok publish_id,
  status, time), written after each platform succeeds. A folder already published to a platform is refused for that
  platform unless `--force` is given.
- The publish lines are appended to the run's `run.log`. Tokens are never printed or logged.

### YouTube sign-in, once

The Google Cloud OAuth client is Flopômetro's "Desktop app" client (its consent screen is already "In production").
The sign-in itself is Minuto Útil's own and is stored apart from Flopômetro's.

1. The client file is at `%LOCALAPPDATA%\MinutoUtil\client_secret.json` (a copy of
   `%LOCALAPPDATA%\Flopometro\client_secret.json`).
2. `dotnet run --project src/MinutoUtil.Cli -- youtube-login`. The consent page opens (the link is printed too; add
   `--no-browser` to open it yourself in the right browser profile). Pick the Google account, then the **Minuto Útil**
   channel in the channel chooser. Google shows no channel chooser for this account: the Minuto Útil channel
   belongs to the Google account "Grim Reaper", so pick that account (copium is Flopômetro). Access asked: upload videos, and read-only access to see which channel it is.
3. The app checks the channel id; a sign-in for any other channel is thrown away. The refresh token is saved
   DPAPI-encrypted for the Windows user in `%LOCALAPPDATA%\MinutoUtil\youtube-token.bin`.

### TikTok sign-in, once

In the TikTok developer portal (https://developers.tiktok.com), signed in with the owner's account:

1. **Manage apps > Connect an app**: create an app (name "Minuto Útil", category Entertainment or Education, a short
   description, an icon). Under **Platforms** tick **Desktop**.
2. **Add products**: **Login Kit** and **Content Posting API**. In Content Posting API leave **Direct Post** off: we
   only send drafts to the inbox.
3. **Scopes**: `user.info.basic` and `video.upload` (both come with those products; check they are listed).
4. **Login Kit > Redirect URI (Desktop)**: `http://localhost:8723/callback/` exactly (it must match
   `tiktok.redirectUri` in the config). TikTok allows only `localhost` or `127.0.0.1` with a port for desktop apps.
5. Switch the app to **Sandbox** (toggle at the top), **Create Sandbox**, then **Sandbox settings > Target users > Add
   account** and sign in with the Minuto Útil TikTok account. It can take up to an hour to show up. Sandbox needs no
   app review; make sure the sandbox has the same products, scopes and redirect URI (clone the configuration).
6. Copy the sandbox's **Client key** and **Client secret** (App details) into Windows user environment variables
   `MINUTO_TIKTOK_CLIENT_KEY` and `MINUTO_TIKTOK_CLIENT_SECRET`. Use the dialog (Start > "Edit environment variables
   for your account", or `rundll32 sysdm.cpl,EditEnvironmentVariables`) rather than a command, so the secret does not
   land in the PowerShell history.

Then `dotnet run --project src/MinutoUtil.Cli -- tiktok-login`, allow access in the browser and check that the
printed account name is the channel's. The refresh token (365 days; TikTok may replace it on each refresh, and the
new one is saved) goes DPAPI-encrypted to `%LOCALAPPDATA%\MinutoUtil\tiktok-token.bin`. If the local listener cannot
start, `tiktok-login --paste` asks you to paste the address the browser ends on (with `?code=`) instead.

## Cloud runs (no PC needed)

The private GitHub repository `copiumvideos-arch/minuto-util` renders and publishes on its own:

```
render.yml (08:30 and 15:30 Brasília, or "/novo" on Telegram)
  -> next topic from config/topics.json -> make -> video kept as a GitHub release "run-<tag>"
  -> Telegram: the video with "✅ Publicar" and "❌ Rejeitar"
tap -> relay/ (Cloudflare Worker, the bot's webhook) -> publish.yml
  -> Publicar: YouTube public Short + TikTok inbox draft, caption and YouTube link on Telegram
  -> Rejeitar: the release is deleted, the topic is not tried again
```

- **Topics**: `config/topics.json`, taken in order. An article that was rendered (in review, published or rejected) is
  skipped; one that failed gets one more try, then leaves the queue. Telegram warns when 5 topics are left. On
  Telegram, `/novo` renders the next topic now and `/novo <English Wikipedia article>` renders that article.
- **Trends**: once a day, before the topic is picked, `cloud trends` reads Google Trends Brazil (daily searches with
  a headline each) and the most read pages on Portuguese Wikipedia, and asks the writer models (one request,
  `prompts/trends.md`) for historical events related to them: elections in the news become the first democratic
  elections, the Magna Carta, the end of a monarchy. Never the current news itself, a living politician or a party,
  and only events at least 25 years old. Every pick is checked on Wikipedia (English article, not a disambiguation
  page, Portuguese twin) and against the runs so far, then queued in `state/trending.json` ahead of the backlog for 3
  days. The review message says which trend inspired the video. If anything fails, the backlog is used.
- **State**: the `state` branch holds `runs/<tag>.json` (topic, status, title, links), `quota-state.json` (the Gemini
  quota tracker, `make --quota-file`), `trending.json` and the sealed token files. Every job commits it with
  `.github/scripts/save-state.sh`; each run has its own file, so concurrent jobs never conflict.
- **Tokens on the runner**: the refresh tokens start from the Actions secrets. When a platform hands back a new refresh
  token (TikTok may on every refresh), it is written AES-256-GCM encrypted to `state/<platform>-token.enc` with the key
  in the `MINUTO_STATE_KEY` secret, and that file wins from then on (`SealedFileTokenStore`). Signing in again locally
  and running `cloud secrets` again makes a new key, so the runner starts over from the new sign-in.
- **No double posts**: the relay removes the buttons after the first tap, publish runs for the same tag never overlap,
  publish runs for different videos wait for each other (they share the TikTok token), and `published.json` is kept
  with the release, so a retry (the "🔁 Tentar de novo" button after a failure) only sends what is missing.
- **Minutes**: a private repository gets 2,000 free Actions minutes a month. A render is about 15-25 minutes on the
  2-core runner, a publish about 2. Check the real numbers under Settings > Billing after the first days before adding
  a third daily render in `render.yml`.

### Setting it up, once

1. `bash .github/scripts/first-setup.sh` (Git Bash, repository root): creates the private repository, pushes `main`,
   creates the `state` branch and runs `cloud secrets`, which copies the keys from this PC's environment, the Google
   client, both sign-ins (from the DPAPI files) and a new state key into the repository's Actions secrets through
   `gh`'s standard input. Nothing is printed.
2. Relay: create a fine-grained GitHub token (github.com > Settings > Developer settings > Fine-grained tokens) for
   the repository `copiumvideos-arch/minuto-util` only, with "Actions: read and write" and nothing else, and save it
   as the Windows user environment variable `MINUTO_RELAY_GITHUB_TOKEN` (the environment variables dialog, not a
   command). Then `cd relay && npx wrangler@4 login` (a free Cloudflare account) and `bash relay/setup.sh`: it deploys
   the Worker, gives it `TELEGRAM_BOT_TOKEN`, `OWNER_CHAT_ID`, `GITHUB_TOKEN` and a new random
   `TELEGRAM_WEBHOOK_SECRET`, and points the bot's webhook at it (`allowed_updates` message and callback_query). The
   relay answers only the owner's chat and checks Telegram's secret header on every call.
3. Send `/novo` to the bot (or start `render.yml` from the Actions tab) and watch the first run.

Tests: `cd relay && npm test` (node:test, GitHub and Telegram replaced by a fake fetch); `CloudTests.cs` for the topic
queue, the sealed token store, the publish/retry/reject flow and the review video upload.

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
dotnet test                       # about 80 s; the render test needs FFmpeg and the tools/ environment
cd tools; uv run pytest           # motion and voice tools
```

The render test runs the whole pipeline on real files with real FFmpeg and the real motion tool, replacing only the
network (Wikipedia, the writer, Smallest.ai answers with generated tones) and the models (generated depth maps, no
Whisper). Script tests parse a stored Gemini answer; the motion tests check on a synthetic scene that near pixels move
more than far ones and that Ken Burns glides without jumps.
Publishing tests (`PublishingTests.cs`) replace YouTube and TikTok with fake HTTP handlers that behave like the real
endpoints: the resumable upload resuming after a failed chunk, the channel guard, TikTok's chunk plan, Content-Range
headers, 206/201 answers and status polling, the published.json double-publish guard, and a dry run making no request.
The sign-in tests play the browser against the real local listener. Nothing in the tests talks to Google or TikTok.
