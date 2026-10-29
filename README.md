# Minuto Útil

Prototype of a faceless Brazilian Portuguese history channel for YouTube Shorts and TikTok: one real event, one
question about it explained and closed in 1.5 to 2.5 minutes, narration over archive photos and footage. This repository renders a video locally; a separate `publish` command uploads a finished one.
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
2. **Script** (`Scripts/ScriptWriter.cs`, `channels/minuto-util/prompts/writer.md`): one request. The writer may only use facts from the
   source text, and must copy, for each section, one sentence of the source that backs it (`sourceQuote`); the app
   checks every quote is really in the text and logs the ones that are not. Dashes are replaced with commas, digits
   left in the narration are flagged and spelled out for the voice (`Scripts/SpokenText.cs`, pt-BR numbers). Only a
   script far outside the word range (`writer.minWords` 220 to `writer.maxWords` 380, 6 to 12 sections) costs a
   second request. The style (open with a question, turn the common version, explain step by step, end on the
   fact that answers it) is modeled on the Estranha História channel; `docs/scriptwriting.md` section 18 has the analysis.
3. **Model requests**: the Gemini key's free quota is shared with the live Flopômetro channel. Each Gemini model
   (`gemini.models`, the same ids as Flopômetro) is asked once at most, never retried; a busy model hands over to the
   next, a daily quota is remembered in `data/quota-state.json`, and Flopômetro's own quota file is read (read only,
   `sharedQuotaFiles`) so a model the live channel used up is skipped without a request. OpenRouter's free writers
   come after Gemini, after Cloudflare Workers AI (open models on the owner's free Cloudflare account, 10,000 neurons
   a day; secrets `CLOUDFLARE_AI_TOKEN` and `CLOUDFLARE_ACCOUNT_ID`, `cloudflareAi` in the config). GitHub Models was
   retired on 2026-07-30. `writer.maxRequests` (5) caps the Gemini requests of a whole run, and
   `writer.maxSpareRequests` (6) the Cloudflare and OpenRouter ones, counted apart so failed Gemini requests never
   take the spares' turn. Workers AI is asked for up to 8,192 reply tokens; its default of 256 cuts a script short.
4. **Voice** (`Voice/SmallestVoice.cs`, `Voice/Narrator.cs`, `tools/voice.py`): Smallest.ai Waves
   (`lightning_v3.1_pro`, voice `thiago`, 24 kHz WAV), one request per section, each take cached next to its text in
   `voice/smallest/`. The voice reads straight through a colon, so a section is spoken in pieces after each colon
   and at a `|` written into a script by hand (`Voice/SpeechPieces.cs`; `section-NN-K.wav`), joined with
   `voice.colonPause` (0.45 s) and `voice.markPause` (0.3 s); the captions never show the `|`. If Smallest fails or runs out of credits, Kokoro `pm_santa` speaks every section instead (never
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
`video.style` in `channels/minuto-util/config.json` (defaults in `StyleOptions`, `src/MinutoUtil.Core/Config.cs`). Picture
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
| Thumbnail | `thumbnail.jpg`, 1080x1920 JPEG under 2 MB: the first still of the video (cropped to 9:16) with the same grade, a soft dark band, the year in big red (or "44 A.C."), the script's `thumbnailText` in cream Roboto Serif Black with the word between asterisks in red, and the watermark. The text block sits around y=900, inside the 3:4 crop TikTok's profile grid shows. Old scripts without `thumbnailText` show the title. Set as the YouTube thumbnail by `publish`, and sent on Telegram after the review video for the TikTok cover | `thumbnail` (true) |
| Painted cover picture | With `thumbnailPicture` "ai", the picture behind the thumbnail text is a painting of the script's `thumbnailScene` (one English sentence: place, time, objects, no named people or faces) made by Cloudflare Workers AI (`cloudflareAi.imageModel`, FLUX.2 klein 4B at 864x1536, about 160 of the free 10,000 daily neurons, one request per video) in the fixed style `cloudflareAi.imageStyle` (vintage oil painting, no text). Saved as `cover-art.jpg` with `cover-art.json` and reused while the scene is the same. The description's credits say the cover is an AI illustration, not a picture of the time. No scene, no Cloudflare key or a failed request: the archive picture, with a warning | `thumbnailPicture` ("archive"; "ai" in `channels/minuto-util/config.json`) |
| Cover as first frame | The thumbnail replaces the video's first frame (over the captions, after the grade; the length and the sync stay), so in TikTok's cover picker the owner leaves the selection at the start instead of uploading an image. One frame is a 1/30 s flash. The thumbnail is rendered before the video for this | `coverFrames` (1; 0 for none) |
| Closed ending | the writer answers the hook and lands one last line (a callback, an irony), instead of forcing a loop (`channels/minuto-util/prompts/writer.md`) | prompt rule |
| No music, no sound effects | narration only; the owner finds music and effects distracting | none: there is no music path |

The file stays under 50 MB (Telegram's bot upload limit) through `video.maxVideoKbps` (4500), lowered for longer
videos so the whole file fits in `video.maxFileMegabytes` (45): about 2,200 kbit/s at 2.5 minutes.

## Output

`output/<yyyy-MM-dd>/<HHmm>/`:

| File | What |
|---|---|
| `video.mp4` | the video |
| `script.json` | the script as written (after local cleanup); `script-writer.json` says which model wrote it and the warnings |
| `metadata.json` | title, description with "Fonte: Wikipedia" and a "Créditos" list of every media item, hashtags |
| `timings.json` | every word, section and shot in seconds, and Whisper's alignment score per section |
| `contact-sheet.png` | one frame every 3 seconds, for review |
| `thumbnail.jpg` | the cover image for YouTube and TikTok (see "Channel style") |
| `cover-art.jpg`, `cover-art.json` | the AI painting behind the cover text and the prompt it was painted from (`thumbnailPicture` "ai") |
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
  are under `youtube` in `channels/minuto-util/config.json`. Before uploading, the token's channel is read
  (`channels?mine=true`) and anything but `youtube.channelId` (`UCNath7BzjeX3ehJCA7cNYAA`, Minuto Útil) is refused,
  so a Flopômetro sign-in can never post here. YouTube may lock uploads as private while the Google Cloud project has
  not passed its API audit; the command warns when the answered privacy is not "public". After the upload,
  `thumbnail.jpg` is set as the custom thumbnail (`thumbnails.set`, 50 quota units, allowed by the `youtube.upload`
  scope). YouTube takes custom thumbnails only from a channel with a verified phone number (youtube.com/verify); a
  refusal is only a warning, written to `published.json` and to the Telegram message. On Shorts the custom thumbnail
  shows on the channel page, in search and in shared links; the Shorts feed itself starts the video.
- **TikTok** (`Publishing/TikTokUploader.cs`, Content Posting API, upload to inbox, scope `video.upload`): init with
  `FILE_UPLOAD`, the file PUT to the returned `upload_url` (one chunk up to 64,000,000 bytes, otherwise 20,000,000-byte
  chunks with the remainder on the last one), then the status is polled every 5 s until TikTok says
  `SEND_TO_USER_INBOX` (up to `tiktok.statusWaitSeconds`). The owner opens the inbox notification in the TikTok app and
  finishes the post (caption, cover, sound, visibility). The inbox flow takes no cover image either, so the review on
  Telegram sends `thumbnail.jpg` as a photo right after the video: save it and pick it as the cover in the app. The inbox flow has no caption field, so the caption (title plus
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

## Channels

One engine runs several channels. Each channel is a folder in `channels/` with everything that is its own:

```
channels/<id>/config.json          settings (overrides of src/MinutoUtil.Core/Config.cs), with the "channel" block below
channels/<id>/topics.json          the topic backlog
channels/<id>/prompts/writer.md    the script rules
channels/<id>/prompts/trends.md    the daily idea picker
```

Every command takes `--channel <id>`; the default is `minuto-util`, so every command line in this README works as
written for Minuto Útil. The `channel` block of `config.json` names everything that must never mix between channels
(`src/MinutoUtil.Core/Channel.cs` checks it; `tools/channels.py` reads the same fields for the workflows):

| Field | Minuto Útil | A new channel |
|---|---|---|
| `name` | `Minuto Útil`: watermark text, `/status` header, sign-in hints | its name |
| `envPrefix` | `MINUTO`: `MINUTO_TELEGRAM_BOT_TOKEN`, `MINUTO_YOUTUBE_REFRESH_TOKEN`, `MINUTO_STATE_KEY`... | e.g. `SPOILER`: `SPOILER_TELEGRAM_BOT_TOKEN`... |
| `tokenFolder` | `%LOCALAPPDATA%/MinutoUtil` (DPAPI sign-ins, `client_secret.json`) | e.g. `%LOCALAPPDATA%/MinutoUtil/channels/<id>` |
| `stateFolder` | `.`: the root of the `state` branch, where its files always were | `channels/<id>` (required) |
| `releasePrefix` | `run-` | `run-<id>-` (required) |
| `scheduleUtcHours` | `[14, 22]` | its render hours; `render.yml` needs a cron line for each (a test checks) |
| `ideasUtcHours` | `[9]` | its idea list hours, or `[]`; `ideas.yml` cron lines likewise |

The variable names and sign-in files under `youtube`, `tiktok` and `telegram` are filled in from `envPrefix` and
`tokenFolder` unless a config names them. The free keys (`GEMINI_API_KEY`, `OPENROUTER_API_KEY`, `SMALLEST_API_KEY`,
`CLOUDFLARE_*`) are shared by every channel. Sign-ins are bound to their channel: the DPAPI entropy and the sealed
files' AES-GCM associated data are `<id>-<platform>-token` (for `minuto-util` exactly what they were before channels).
`youtube.channelId` is required for publishing: an empty or wrong one refuses every sign-in.

### Trailer channels (Profissão Spoiler)

`media.footage` picks where the pictures come from: `archive` (Minuto Útil: public domain and CC pictures, the
vintage look) or `trailers` (Profissão Spoiler, a games channel). A trailer run writes and voices the script the same
way (Wikipedia sources, the channel's `writer.md`), then:

1. **Trailers** (`Media/SteamTrailers.cs`): the game is found on Steam by its Wikipedia article's name (the exact
   name first, then a later edition that starts with it; anything else is refused, and `make --steam-app <id>` names
   the app). Up to `media.trailers` (3) official trailers, highlighted ones first, are read from their HLS streams at
   the best size up to 1080p, video only, into `media/trailers/`; only https streams on `*.steamstatic.com` are
   followed. `media/trailers.json` keeps the list for a re-render.
2. **Cuts** (`tools/trailer.py`): shots are found in each trailer (cached in `shots.json` next to them), the narration
   is cut into 1.3 to 2.6 s slots on word starts (1.0 to 1.6 s in the first five seconds), each slot gets an unused
   high-motion shot from a different trailer than the one before, cropped to 9:16 on the action with a slow push in.
   The result is `footage.mp4` (silent, exactly the video's frames) and `cuts.json`.
3. **Final render**: the same as the archive look (captions, voice at `video.voiceLufs`, size cap), with the
   channel's style switches: Montserrat captions in white with key words in red, the channel's name as a text-only
   watermark, no vintage layers, year card, map or cover. No music: trending sounds are added in the TikTok app.

Its scripts state facts only and end on the last fact (no opinions, ironies or "Ou seja" wrap-ups), see
`channels/profissao-spoiler/prompts/writer.md`. The trailers are the studios' own promotional videos, credited in
the description; this is the owner's choice for this channel and does not change Minuto Útil's media rules.

## Cloud runs (no PC needed)

The public GitHub repository `copiumvideos-arch/minuto-util` renders and publishes on its own (public: unlimited
free Actions minutes on a 4-core runner; secrets stay in Actions secrets, tokens on the state branch are encrypted,
but run logs and the release videos waiting for review can be seen by anyone with the link):

```
render.yml (11:00 and 19:00 Brasília, or "/novo" on Telegram)
  -> next topic from channels/minuto-util/topics.json -> make -> video kept as a GitHub release "run-<tag>"
  -> Telegram: the video with "✅ Publicar" and "❌ Rejeitar"
tap -> relay/ (Cloudflare Worker, the bot's webhook) -> publish.yml
  -> Publicar: YouTube public Short + TikTok inbox draft, caption and YouTube link on Telegram
  -> Rejeitar: the release is deleted, the topic is not tried again
```

Every workflow takes a `channel` input (default `minuto-util`). A scheduled run is for every channel whose
`scheduleUtcHours` (render) or `ideasUtcHours` (ideas) has that hour, one matrix job each, so two channels at the same
hour render side by side. `tools/channels.py` picks them; `.github/scripts/cli.sh` runs the app for one channel. Each
step gets the channel's secrets as `secrets[format('{0}_TELEGRAM_BOT_TOKEN', prefix)]` under a fixed name
(`CH_TELEGRAM_BOT_TOKEN`), and `cli.sh` hands them to the app under the channel's own name
(`MINUTO_TELEGRAM_BOT_TOKEN`). Renders are one at a time per channel (`render-<id>`), publish runs per channel and
tag; channels never wait for or cancel each other.

- **Topics**: `channels/minuto-util/topics.json`, taken in order. For now the channel tells Brazil's history only: events in Brazil,
  the wars Brazil fought in South America and Brazil's part in the World Wars, mixed over the colony, the Empire and
  the Republic (negative years still work for anything before Christ: the Telegram lists show "44 a.C.", and the year
  card gives way to the plain date stamp). An article that was rendered
  (in review, published or rejected) is skipped; one that failed gets one more try, then leaves the queue. Telegram warns when 5 topics are left. On
  Telegram, `/novo` renders the next topic now and `/novo <English Wikipedia article>` renders that article.
- **Ideas (the owner chooses)**: every day at 06:00 Brasília, `ideas.yml` reads Google Trends Brazil (daily searches
  with a headline each) and the most read pages on Portuguese Wikipedia, and asks the writer models (one request,
  `channels/minuto-util/prompts/trends.md`) for 10 events from Brazil's history (at least two each from the colony, the Empire and the
  Republic, every region of the country, at most two from the same war), with the news only as a loose inspiration:
  an election in the news can become the Republic's first vote, a football final the Maracanazo. Never the current news itself, a living
  politician or a party, and only events at least 25 years old. Every pick is checked on Wikipedia (English article, not a disambiguation
  page; the Portuguese article the model names, or else the English page's link) and against past runs, recent
  lists and waiting ideas. The list goes to Telegram with buttons 1 to 10: a tap approves that idea (`approved/` on
  the state branch). Any text the owner sends the bot (or `/ideia <text>`) is an idea of its own, for example "por que
  só o Brasil usa urna eletrônica?": the picker turns it into the history that answers it and it goes first in line.
  `/novo [text]` makes a video right away. Renders take text ideas first, then tapped ideas, then `channels/minuto-util/topics.json`.
- **The queue on Telegram**: `/fila` (or `/queue`) lists the next videos with a ⏫ (to the top) and a 🗑 (remove) button
  per item; `/mover 5 2` puts item 5 second; `/remover 3` takes item 3 out; `/adicionar <text>` (or `/add`, or plain
  text) adds an idea, which becomes the next video. The owner's order is kept in `order.json` on the state branch
  (only the head is pinned, the default order fills the rest), removals in `removed/`. A review video's
  **🔁 Refazer** button makes the same topic again with a new script, voice and pictures.
- **More bot commands**: `/revisar on|off` (review mode: each run stops after the script, `make --script-only`, keeps
  its folder as `run.tar.gz` on the release and sends the script with ✅ Aprovar, 🔁 Outro roteiro, ❌ Rejeitar; a reply
  with edited lines, labeled `Título:`, `Gancho:`, `1.`, `Pergunta:`, is applied and the video made from it, through
  `render.yml` `continue`), `/pausar` and `/retomar` (scheduled renders skip while paused; `/novo` still works),
  `/status` (switches, what waits for the owner, recent results, next topics), `/stats` (views, likes and comments of
  the published Shorts, YouTube `videos.list`). A reply to a review video with a line of text becomes its title.
- **State**: the `state` branch holds `runs/<tag>.json` (topic, status, title, links), `quota-state.json` (the Gemini
  quota tracker, `make --quota-file`), `ideas/<date>.json`, `approved/*.json`, `order.json`, `removed/`,
  `settings.json` and the sealed token files. Minuto Útil's are at the root of the branch, as they always were; every
  other channel has the same files under `channels/<id>/` (`channel.stateFolder`), so two channels never share one.
  Every job commits it with `.github/scripts/save-state.sh`; each run has its own file, so concurrent jobs never conflict.
- **Tokens on the runner**: the refresh tokens start from the Actions secrets. When a platform hands back a new refresh
  token (TikTok may on every refresh), it is written AES-256-GCM encrypted to `<channel state folder>/<platform>-token.enc`
  with the key in the channel's `<PREFIX>_STATE_KEY` secret (`MINUTO_STATE_KEY`), and that file wins from then on
  (`SealedFileTokenStore`). Signing in again locally and running `cloud secrets` again makes a new key, so the runner
  starts over from the new sign-in.
- **No double posts**: the relay removes the buttons after the first tap, publish runs for the same channel and tag
  never overlap, publish runs for different videos of one channel wait for each other (they share its TikTok token),
  and `published.json` is kept with the release, so a retry (the "🔁 Tentar de novo" button after a failure) only
  sends what is missing.
- **TikTok check**: when a draft never shows up in the app, run `publish.yml` with the action `tiktok-check` (no tag).
  It sends to Telegram the TikTok account the sign-in belongs to (the drafts go only there; in Sandbox it must be a
  target user) and TikTok's current status of the last 5 drafts (waiting in the inbox, posted, or failed with the reason).
- **Volume**: two renders a day (the owner's choice; `/novo` makes more). TikTok accepts 5 inbox drafts per 24 hours,
  so post the drafts daily. The trend scan can queue more topics than that; the queue simply lasts longer.

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

### Adding a channel

1. `channels/<id>/` with `config.json` (the `channel` block above, `youtube.channelId`), `topics.json` and
   `prompts/writer.md`, `prompts/trends.md`. Add a cron line to `render.yml` (and `ideas.yml`) for each new hour.
2. Its own Telegram bot (BotFather), YouTube sign-in and TikTok app: `youtube-login --channel <id>`,
   `tiktok-login --channel <id>`, with the keys in the Windows user environment under its prefix
   (`<PREFIX>_TELEGRAM_BOT_TOKEN`, `<PREFIX>_TELEGRAM_CHAT_ID`, `<PREFIX>_TIKTOK_CLIENT_KEY`,
   `<PREFIX>_TIKTOK_CLIENT_SECRET`; the Google client file goes in its `tokenFolder`).
3. `dotnet run --project src/MinutoUtil.Cli -- cloud secrets --repo copiumvideos-arch/minuto-util --channel <id>`:
   its secrets and a new `<PREFIX>_STATE_KEY` into the Actions secrets (the shared free keys are copied again, same
   values).
4. `bash relay/setup.sh <id>`: deploys the Worker, gives it `TELEGRAM_BOT_TOKEN_<ID>` and `TELEGRAM_WEBHOOK_SECRET_<ID>`
   (`<ID>`: the id in upper case, `-` as `_`) and points the new bot's webhook at `<worker>/telegram/<id>`. Minuto Útil's
   bot keeps `/telegram` and its secrets.

Tests: `cd relay && npm test` (node:test, GitHub and Telegram replaced by a fake fetch); `CloudTests.cs` for the topic
queue, the sealed token store, the publish/retry/reject flow and the review video upload; `ChannelTests.cs` for the
channel split (Minuto Útil's settings, secret names and sealed files unchanged, two channels kept apart, the cron
lines matching `scheduleUtcHours`); `tools/test_channels.py` for the workflows' channel picking.

## Known limits

- loc.gov answers API clients from this PC with a Cloudflare challenge (403); the Library of Congress source then
  turns itself off for the run. Commons and the Internet Archive supply everything today.
- Wikimedia rate-limits downloads of original files from this IPv4 address (429, Retry-After 600); only standard
  thumbnail widths (960, 1280, 1920, 3840) are fetched.
- Picture choice is search rank plus metadata (year, title words); nothing looks at the pixels for relevance. A
  scanned negative or a modern photo without a date can slip through: review the contact sheet.
- The crop aims at the near-pixel centroid of the depth map; sometimes that is a foreground helmet, not a face.

Settings live in `channels/minuto-util/config.json` (defaults in `src/MinutoUtil.Core/Config.cs`), the writer's instructions in
`channels/minuto-util/prompts/writer.md`, and the reasoning behind them (audience, hooks, structure, how to measure what works) in
`docs/scriptwriting.md`.

## Tests

```powershell
dotnet test                       # about 80 s; the render test needs FFmpeg and the tools/ environment
cd tools; uv run pytest           # motion, voice, map and watermark tools, and the workflows' channel picking
```

The render test runs the whole pipeline on real files with real FFmpeg and the real motion tool, replacing only the
network (Wikipedia, the writer, Smallest.ai answers with generated tones) and the models (generated depth maps, no
Whisper). Script tests parse a stored Gemini answer; the motion tests check on a synthetic scene that near pixels move
more than far ones and that Ken Burns glides without jumps.
Publishing tests (`PublishingTests.cs`) replace YouTube and TikTok with fake HTTP handlers that behave like the real
endpoints: the resumable upload resuming after a failed chunk, the channel guard, TikTok's chunk plan, Content-Range
headers, 206/201 answers and status polling, the published.json double-publish guard, and a dry run making no request.
The sign-in tests play the browser against the real local listener. Nothing in the tests talks to Google or TikTok.
