# Writing scripts for Minuto Útil

How to write a history script that a Brazilian viewer, scrolling on a phone, watches to the end and wants to see
again. This is the reasoning and the craft behind `prompts/writer.md`. The prompt holds the rules the model must
follow; this guide explains why they exist, how to apply them well, and how to find out what works for this channel.

Read it before changing `prompts/writer.md`, `prompts/trends.md`, the word range in `config/minuto-util.json`, or
anything else that shapes a story. When a lesson here turns into a rule, the rule goes into the prompt and the reason
stays here.

## Contents

1. [How sure we are](#1-how-sure-we-are)
2. [The viewer](#2-the-viewer)
3. [What the platforms reward](#3-what-the-platforms-reward)
4. [Choosing the angle](#4-choosing-the-angle)
5. [The shape of a minute](#5-the-shape-of-a-minute)
6. [The hook](#6-the-hook)
7. [The middle](#7-the-middle)
8. [The turn, the payoff and the loop](#8-the-turn-the-payoff-and-the-loop)
9. [Writing for the ear](#9-writing-for-the-ear)
10. [Making it matter to a Brazilian viewer](#10-making-it-matter-to-a-brazilian-viewer)
11. [Writing for the pictures](#11-writing-for-the-pictures)
12. [Title, question and description](#12-title-question-and-description)
13. [A worked example](#13-a-worked-example)
14. [Checklists](#14-checklists)
15. [Measuring and learning](#15-measuring-and-learning)
16. [Open questions](#16-open-questions)
17. [Sources](#17-sources)
18. [The reference channel](#18-the-reference-channel)

## 1. How sure we are

Most advice about short video comes from tool vendors and creator blogs. Their numbers disagree with each other and
rarely say how they were measured. TikTok and YouTube do not publish their ranking weights. So every claim below is
marked with how much weight it carries:

- **Research**: backed by published psychology or media research. The effect is real; how big it is inside a
  60 second Short is not measured.
- **Platform**: stated by YouTube or TikTok, or visible in their analytics.
- **Practice**: what experienced short-form creators converge on. Plausible and consistent, not proven.
- **Hypothesis**: our own reasoning for this channel. Test it (section 15) before treating it as a rule.

The goal is to replace hypotheses with this channel's own numbers as videos come in. Nothing here should outrank
what our analytics show.

## 2. The viewer

**Who.** A Brazilian adult, curious, not a history student. Often 18 to 40, but the channel should not assume an
age. They did not search for this video; the feed handed it to them between a football clip and a recipe.

**Where.** On a phone, vertical, often with one hand, sometimes in noise (bus, kitchen, break at work). Captions
carry the story when the sound is low, which is why the caption words pop in and the key words are red.

**How much.** Brazilians are among the heaviest social media users in the world: about 3 hours 49 minutes a day for
online users, first in the world in the February 2025 We Are Social and DataReportal figures, against about 2 hours
20 minutes worldwide (Platform-adjacent data, see Sources). TikTok had about 82 million users in Brazil in 2023. The
competition for each second is fierce, and the viewer is very practiced at swiping.

**What they want from us.** Not a lesson. A story they can retell: "sabia que Júlio César recebeu um bilhete
avisando do plano e não leu?" A video earns its place when the viewer leaves with one fact they want to tell
someone. (Hypothesis, but it is the test every script should pass.)

**What they bring.** School history (Cabral, Tiradentes, Dom Pedro, the Lei Áurea, the World Wars, Egypt, Rome),
football, novelas, religion (a mostly Christian country, Catholic and evangelical), strong regional identities, and
a politically polarized moment. Section 10 covers how to use each of these and where they are risky.

## 3. What the platforms reward

**The swipe test comes first (Platform).** YouTube Studio shows "Viewed vs. swiped away" for every Short: the share
of people who watched instead of swiping. A new Short is shown to a small group first; if that group swipes, it
stops there. TikTok works the same way. So the first two or three seconds decide whether the rest of the script
matters at all.

**Then how much of it they watch (Platform).** Average percentage viewed and completion. A video watched past 100%
(rewatched) is the strongest signal a short video can send. This is why every script loops.

**Then whether they act (Platform, Practice).** Comments, shares, likes, follows. Comments matter for this channel
because Brazilian audiences comment a lot and argue in good humor; the `question` field is there to start that.

**Length (Practice, conflicting; owner's decision).** Shorter videos finish more often; longer videos that hold
attention earn more total watch time. Vendor numbers on the "ideal length" disagree. Until 2026-10-29 we used 130 to
170 words (about a minute), and too many scripts felt like summaries that opened an idea and never closed it. The owner
asked for whatever length it takes to introduce and finish one idea, up to the three minutes YouTube still treats as
a Short. We now use 220 to 380 words (about 1.5 to 2.5 minutes at the narrator's 150 words a minute), close to the
Shorts of the reference channel in section 18 (median 318 words). Files stay under Telegram's 50 MB because the
render lowers the bitrate cap for longer videos (`video.maxFileMegabytes`).

**Originality (Platform).** Since July 2025, YouTube's monetization policy excludes "inauthentic content": videos
that look made from a template with little variation, or easily replicated at scale. AI tools are allowed; sameness
is not. A faceless, automated channel is exactly what this policy watches. Every script must feel like its own story,
not the same skeleton with a new name. In practice:

- vary the hook type from video to video (section 6 lists several);
- vary the protagonist (a ruler, a soldier, a crowd, a child, an animal, an object);
- vary the ending (loop line, question out loud, a final image);
- never reuse signature phrases across videos ("e foi assim que...", "o que ninguém te contou").

## 4. Choosing the angle

The topic is picked by `prompts/trends.md`; the angle is picked by the writer. A good event with a weak angle makes
a weak video. Most bad scripts fail here, before the first sentence.

**An angle is one surprising thing inside the event (Practice).** Not "the Battle of Hastings", but "the king who
had just marched three hundred kilometers after winning another battle". Not "the fall of Constantinople" but "the
gate someone forgot to lock". Look in the sources for:

- a **decision** someone made that turned out to matter (or a warning someone ignored);
- a **contradiction**: the opposite of what you would expect ("the emperor who banned his own image");
- a **small cause with a large effect**: an object, a mistake, a delay, the weather;
- a **number that sounds wrong**: "only twelve days", "with one hundred and sixty men";
- a **human detail** that makes a famous figure a person: fear, a habit, a last word the sources confirm.

**Find the story first, then write.** Before any sentence, answer three questions from the sources (this is in the
prompt as "whose story it is, what they want or fear, and what stands in the way"):

1. Who is at the center? One person or one small group.
2. What do they want, or what do they fear?
3. What stands in the way, and how does it end?

If the sources do not answer all three, the angle is wrong. Pick another detail from the same event.

**The angle must survive the facts rule.** Only what the Wikipedia sources state. A gripping angle the sources only
hint at is not an angle; it becomes bait, and the comments will catch it.

**A quick test (Hypothesis).** Say the angle as one sentence a friend would repeat at lunch. "O faraó que mudou os
deuses do Egito e depois foi apagado da história" works. "A importância do Egito Antigo" does not.

## 5. The shape of two minutes

At about 150 words a minute, every 2.5 words is a second. A 300 word script, in time:

| Time | Words | Job | Prompt field |
| --- | --- | --- | --- |
| 0 to 7 s | up to 20 | Hook: the question, or the strange fact behind it. The strange part in the first 8 words. | `hook` |
| 7 to 20 s | 20 to 35 | The common version, then the turn ("Mas a história é mais complicada"), when the sources support one. | first section |
| 20 to 95 s | 180 to 200 | The explanation, one step per section, each causing or complicating the next; a question answered on the spot now and then. | middle sections |
| 95 to 120 s | 40 to 60 | Close the idea: "Ou seja," answers the hook, then one line that lands (callback, irony, bigger point). | last section |

The year card and map insert play over the first seconds; the hook must work under them, so it is never the only
place a date or place is said.

**Every section is a step (Practice).** One section, one thing changes. A section is 1 to 4 sentences, about ten to
fifteen seconds, and the shot plan gives each section its own pictures. If a section has two beats, split it; if it
has none, cut it.

## 6. The hook

The hook decides the swipe test. It is at most 14 words, but the gap must be clear in the first eight or so, since
that is the first three seconds.

**What a hook does (Research).** Curiosity comes from noticing a gap between what you know and what you want to know
(Loewenstein's information-gap theory). The gap pulls hardest when it is specific and feels small enough to close:
the viewer has a guess, but is not sure. A vague mystery ("algo estranho aconteceu") gives nothing to guess; an
answer given away closes the gap. Experiments on trivia found curiosity is highest at moderate confidence, and that
answers to questions people were curious about are remembered better days later.

**Hook types.** Rotate them; a channel where every hook has the same shape reads as a template.

| Type | Shape | Example |
| --- | --- | --- |
| Result without cause | Say what happened, hide why. | "Um faraó trocou os deuses de todo o Egito. Depois, tentaram apagar o nome dele." |
| Ignored warning | Someone knew, and it did not help. | "Júlio César recebeu um bilhete com o plano para matá-lo. E não leu." |
| Number that sounds wrong | A quantity the viewer will doubt. | "Cento e sessenta e oito homens derrubaram um império de milhões." |
| Contradiction | Two true things that seem impossible together. | "O homem que inventou a dinamite queria ser lembrado pela paz." |
| In the middle of the action | Drop the viewer into a moment, present tense. | "Madrugada no Atlântico. O vigia vê uma sombra na frente do navio." |
| Small cause, big effect | An object or detail that changed everything. | "Uma porta esquecida aberta decidiu o destino de Constantinopla." |
| Against what school taught | What most Brazilians learned, and what the sources add. | Only when the sources really contradict the common version (section 10). |

The examples show shape, not facts: every fact in a real hook comes from that video's sources.

**What kills a hook (Practice).**

- Greetings, the channel name, "você sabia", "hoje vamos falar", "nesse vídeo". The viewer is gone before the story.
- Background first: "No século quinze, a cidade de Constantinopla era...". Setting can come later, while the story
  already moves.
- A date or name as the first words when it means nothing to the viewer. "Em mil quatrocentos e cinquenta e três"
  costs two seconds and opens no gap.
- A gap the video does not close, or closes with something smaller than promised.
- Superlatives the sources do not support: "o maior mistério da história".

**Hook check.** Read only the hook aloud and ask: does a stranger now have one specific question? Could they picture
something? Does the video answer exactly that question at the end?

## 7. The middle

**One protagonist, at human scale (Research, Practice).** Narrative transportation research (Green and Brock) finds
that people absorbed in a story pay more attention, remember more and argue less with it. Absorption depends on
attention, emotion and mental imagery: the viewer has to picture a person in a place doing something. Armies,
treaties and "the economy" do not get pictured; a captain on a deck does.

**Cause and complication, never "e depois" (Practice, reference channel).** Each section either creates a new
problem or follows from the one before. A list of events in order ("e depois... e então...") is a summary, and viewers
swipe summaries. The logic matters more than the words: the reference channel (section 18) says "mas" nine times per
thousand words but "por isso" almost never; it links steps with "então", "só que", "aí", "daí", "ou seja", "porque".
The prompt lists those spoken links instead of asking for "por isso".

**Small gaps along the way (Practice).** The hook opens the big question. The middle keeps attention with small ones
that close quickly: "Só havia um problema.", "Ninguém esperava o que veio a seguir." (sparingly, at most once per
video, and never the same phrase in two videos). A concrete detail the viewer does not yet understand also works:
mention the note in the hand before saying what it says.

**Concrete beats abstract (Research, Practice).** A time of day, an object, a number, a place, a gesture. "Ao
amanhecer", "três navios", "a porta do templo". Concrete words are easier to picture, which is what absorbs the
viewer, and they are also what the media search can find a photo of (section 11).

**Every sentence has a job.** Raise the question, raise the stakes, turn, or pay off. Interesting background with no
job is cut. A good test: delete the sentence; if the story still works, it stays deleted.

**Explain names the first time (writer.md rule).** The viewer knows nothing. "O Carpathia, um navio que estava a
quilômetros dali". Better still, use a role ("o capitão", "a esposa dele") and drop the name. A minute has room for
two or three names at most.

## 8. The turn, the payoff and the loop

**The ending is what people remember (Research).** Kahneman's peak-end rule: people judge an experience mostly by
its most intense moment and by its end, not by its average. For a script: put the strongest true detail near the
end, and never let the last section go slack. A flat ending makes the whole video feel weaker, even if the middle
was good, and it is also the moment the viewer decides whether to let it loop.

**The turn.** Just before the end, short sentences, often one clause each. "Ele guardou o papel. Ia ler depois." The
rhythm speeding up tells the ear something is coming.

**The payoff answers the hook, exactly (Practice).** If the hook asked "why did they erase his name", the end says
why. Not a moral ("e isso mostra como a história é fascinante"), not a teaser ("mas essa é outra história"), not a
"follow for part two".

**Close the idea (owner's decision, reference channel).** Since 2026-10-29 the ending closes the idea instead of
looping: it answers the hook in plain words, often starting "Ou seja," and then lands one last line: a callback to
the opening image or words, a light true irony, or the bigger point the story makes. In a two minute video a rewatch
is rarer, and a forced loop line made endings feel cut off. The loop below stays as an option when the last line
happens to run into the hook naturally.

**The loop (Platform logic, Practice).** Shorts and TikTok replay automatically. If the last words flow into the hook,
part of the audience watches the start again before noticing, and a rewatch counts. Ways to build it:

- end on the subject the hook names: last line "...um bilhete que ele nunca abriu." runs into the hook "Júlio César
  recebeu um bilhete...";
- end with an unfinished phrase that the hook completes: "...e tudo dependia de uma coisa:" into the hook;
- end on the same image the hook shows, so the cut back to the start feels like a continuation.

Never say "assista de novo" or mention the loop.

**The question ending.** When the subject divides opinions (voting, laws, rights, wars, religion, a disputed
decision), the last section asks the viewer out loud instead of looping. It must be neutral (section 10). Otherwise
the question goes only in the caption.

## 9. Writing for the ear

The script is read by a text-to-speech voice (Smallest.ai, Kokoro as fallback) and timed word by word by Whisper for
the captions. Write for that.

**Sentences.** Short. Most under 15 words. One idea each. Subject, verb, object. A long sentence with two commas is
hard to follow by ear and harder for the voice to phrase.

**Spoken Brazilian Portuguese (writer.md rule).** As people speak it, not as a textbook writes it. "Ele não sabia",
not "Ele desconhecia". "Pegou", not "apoderou-se". Use proclisis the Brazilian way ("me disse", "se escondeu"), not
the European "disse-me". Present tense is allowed for drama.

**Register (reference channel).** Someone who knows the subject explaining it to a friend: relaxed, clear, a little
funny. The reference channel says "né" about nineteen times per thousand words and leans on "a gente", "você",
"os caras", "pra caramba", "inclusive", "por exemplo", "pelo menos". A synthetic voice cannot carry that much filler,
so the prompt allows "né?" a few times per script and the other words in small doses. No slang that belongs to one
region or one age group ("mano", "tipo assim", "véi"); "você" for the viewer, never "tu" or "vós". The narrator never
says "eu acho": the host of the reference channel gives opinions as a known historian, a faceless channel cannot.

**Write it as it is said.** The first scripts under the new rules still read like a textbook ("interveio para depor
o governo", "por considerar que contrariava seus interesses") and opened sections with a date. The prompt now shows
formal-to-spoken rewrites and an example script in the target voice; models copy an example more reliably than they
follow a rule. The example is about Julius Caesar on purpose, far from the Brazil topics, and the prompt says not to
reuse its phrases, so it shapes the tone without becoming a template.

**Every accent and cedilla.** Text without them is read wrong by the voice ("decisao" is not "decisão").

**Numbers in words (writer.md rule).** "Mil novecentos e quarenta e quatro", "cento e cinquenta e seis mil
soldados". Round where the sources allow it and the exact number does not matter: "quase três mil" is easier to hear
than "dois mil novecentos e setenta e sete". Keep exact numbers when the exactness is the point ("vinte e três").

**Things the voice reads badly (Practice, check in the renders).**

- Roman numerals: write "Dom Pedro Segundo", "Luís Dezesseis", "século dezesseis".
- Abbreviations: "Estados Unidos", not "EUA"; "antes de Cristo", not "a.C." in the spoken text.
- Foreign names: use the Portuguese form when one exists and is common (Júlio César, Carlos Magno, Gengis Khan,
  Cristóvão Colombo, Constantinopla). Otherwise the simplest spelling a Brazilian would say aloud.
- Homographs where the stress changes the meaning ("sede", "colher", "jogo"): reread and rephrase if the voice could
  pick the wrong one.
- Parentheses, quotation marks, dashes, emoji: never in the narration.

**Read it aloud.** The only reliable test. If you stumble, the voice will too.

## 10. Making it matter to a Brazilian viewer

Most of this section is **Hypothesis**: reasoned from the audience, not yet measured. It is the first place to test.

**A bridge to Brazil, when it is true.** A story far from Brazil lands harder when one sentence connects it to
something the viewer knows: the Portuguese navigators in the age of discovery, a Brazilian product, a word that came
into Portuguese, Dom Pedro's family ties to European courts. Only when the sources state the link. A forced bridge is
worse than none.

**What the viewer learned at school.** Most Brazilians share a school curriculum: Cabral, the capitanias, Tiradentes,
"Independência ou Morte", the Lei Áurea, Vargas, the two World Wars, Egypt and Rome. A story that adds a surprising
true detail to something everyone half remembers is strong: the viewer already has a picture, and now gets a twist.
"Against what school taught" hooks are only allowed when the sources really contradict the common version.

**Scale they can feel.** Comparisons to things a Brazilian can picture help a number land: a stadium, a city, a
bus, the distance between two Brazilian capitals. These are interpretation, not source facts, so they must be
arithmetic you can check and to something universally known. Open question for the prompt (section 16).

**Names in Portuguese.** Júlio César, Carlos Magno, Constantinopla, Troia. A viewer who hears the English form feels
the video was translated, which is also an originality red flag.

**Neutrality (writer.md rule).** Brazil is polarized and comment sections turn fast. The channel tells what
happened. It never names a current politician, party or candidate, never suggests fraud or conspiracy, never takes a
side in a divisive question. The military dictatorship (1964 to 1985), slavery and colonial violence, and religion are
real history and can be told, from the sources, with plain facts and without editorializing. When in doubt, choose a
different angle in the same event rather than soften the facts.

**Religion as history.** Founders, councils, schisms, miracles as reported. Say "segundo a tradição" or "segundo os
relatos da época" when the sources do; never mock and never preach.

**Comments.** Brazilians comment a lot and enjoy taking sides on harmless questions ("você teria confiado no
plano?"). Questions that ask what the viewer would do or prefer get answers; questions with a right answer get
silence. A question that invites a fight about today's politics gets the wrong kind of comments.

## 11. Writing for the pictures

The media step searches Wikimedia Commons, the Library of Congress and the Internet Archive with the `searchEn` and
`searchPt` queries of each section, and shows each section over its own shots. A sentence nothing can illustrate
becomes a generic picture, and a generic picture is a reason to swipe.

- Each section names something that exists in an archive: a place, a ship, a building, a painting of a person, a
  map, a manuscript. "O Senado romano", "o porto de Lisboa", "a rainha Isabel".
- Abstract beats ("a tensão aumentava", "o império entrou em crise") leave the media step without a target. Rewrite
  them as something visible: who did what, where.
- For events before photography, paintings, statues, coins, ruins and old maps carry the video. Choose angles whose
  key moments were painted or carved.
- Never write toward images of the dead or wounded; the media step blocks them anyway.

## 12. Title, question and description

**Title (Practice).** Under 70 characters, Portuguese. It is read on the Shorts shelf, in search and on TikTok
profiles, not in the feed itself, so it matters most for search and for the profile grid. It should promise the same
gap as the hook in different words, never a fact the video does not deliver. Good: "O bilhete que Júlio César não
leu". Bad: "A INCRÍVEL história de Júlio César!!!".

**Thumbnail text (Practice).** Two to five words on the cover image, one of them in red. Like the title, the cover is
seen on the profile grid, the Shorts shelf, search and shared links, not in the feed, where the video starts playing
at once. It is read in about a second at the size of a stamp, so it carries the gap in the fewest concrete words and
does not repeat the title (both are often seen side by side). The red word is the one that makes the gap: the
contradiction, the number, the strange object. The year sits above it in the channel's red, like the year card.

**Thumbnail scene (Practice).** Behind the text, a painting made from the script's `thumbnailScene`. Every cover gets
the same fixed style (a vintage oil painting) so the profile grid looks like one series, and a painting, unlike a
realistic image, is not mistaken for an archive photo on a channel built on real sources; the description also says
the cover is an AI illustration. The scene shows the place, the time and the objects of the story with one clear
subject in the middle, where the text goes. People stay distant or seen from behind and are never named: a painted
face of a real person would be invented, and image models get famous faces, uniforms and writing wrong. Viewers of
history channels notice wrong details, so the scene asks only for what the sources support.

**Question.** Short, open, neutral, about this story, answerable in one word or one line. "E você, teria ido ao
Senado naquele dia?" It goes in the caption, and out loud in the last section only for divisive subjects.

**Description.** Two or three sentences that sum up the story with dates in digits. Written for someone who already
watched: it is where they check a detail.

## 13. A worked example

Event: the assassination of Julius Caesar. The facts used here are the familiar ones from the ancient accounts
(Plutarch, Suetonius); in a real run every fact must appear in that run's Wikipedia sources, with a `sourceQuote`.

**A weak script (a summary).**

> Júlio César foi um dos líderes mais importantes de Roma. Ele conquistou a Gália e se tornou ditador. Muitos
> senadores tinham medo do poder dele. Em quinze de março do ano quarenta e quatro antes de Cristo, um grupo de
> senadores decidiu matá-lo. Ele foi ao Senado e foi atacado. Depois da morte dele, Roma entrou em guerra civil e
> acabou virando um império. Essa é uma das histórias mais famosas da Antiguidade.

What goes wrong: it opens with background; there is no gap; every link is "e depois"; there is no single moment to
picture; the ending is a moral ("uma das histórias mais famosas") instead of a payoff; nothing loops.

**A strong script (one story).**

> **Gancho:** Júlio César recebeu um bilhete com o plano para matá-lo. E não leu.
>
> **1.** Era quinze de março do ano quarenta e quatro antes de Cristo, e o homem mais poderoso de Roma tinha
> tudo a perder.
>
> **2.** Um adivinho já tinha avisado: cuidado com esse dia. Na noite anterior, Calpúrnia, a esposa dele, sonhou
> com a sua morte e implorou que ele ficasse em casa.
>
> **3.** Mas um dos conspiradores foi buscá-lo. Disse que o Senado esperava, e que faltar por causa de um sonho
> pareceria fraqueza. Por isso, César saiu.
>
> **4.** No caminho, um homem colocou um bilhete na mão dele. Era o plano inteiro. César guardou o papel. Ia ler
> depois.
>
> **5.** No Senado, os conspiradores o cercaram. Foram vinte e três golpes. Entre eles estava Bruto, que César
> tratava quase como um filho.
>
> **6.** César caiu ainda com o aviso que podia ter salvado sua vida. Um bilhete que ele nunca abriu.

Why it works:

- **Hook:** "ignored warning" type. The gap (why did he not read it, and what happened) is clear in eight words, and
  most viewers half know the answer, which is the moderate confidence that drives curiosity.
- **Stakes** in section 1, setting given while the story moves.
- **Links:** 2 builds the danger, 3 is a "mas" (someone talks him out of staying), 4 is a "por isso" chain into the
  note, 5 is the consequence, 6 the payoff. No "e depois".
- **Protagonist** at human scale: a man, his wife's dream, a piece of paper in his hand.
- **Turn** in section 4: four short sentences, the last one three words.
- **Peak-end:** the strongest detail (Bruto) just before the end; the end is the image of the unopened note.
- **Loop:** "Um bilhete que ele nunca abriu." runs into "Júlio César recebeu um bilhete...".
- **Violence** stated as a number, no description.
- **Pictures:** Roman Senate, paintings of the assassination, busts of Caesar and Brutus, a Roman street.

## 14. Checklists

**Before writing**

- [ ] I can say the angle as one sentence a friend would repeat.
- [ ] The sources name who is at the center, what they want or fear, and what stands in the way.
- [ ] The surprising detail at the heart of the angle is stated in the sources, not implied.
- [ ] The key moments can be shown with archive pictures.

**After writing** (also the owner's review in Telegram)

- [ ] The hook asks one specific question (or states the strange fact behind it) in its first eight words, with no
  greeting, no "você sabia".
- [ ] The common version is told and turned, when the sources support it.
- [ ] Every section causes or complicates the next; no "e depois" lists.
- [ ] Every sentence has a job; no background for its own sake.
- [ ] Two or three names at most, each explained the first time, or replaced by a role.
- [ ] The strongest true detail comes near the end, and the last section does not lose energy.
- [ ] The end answers exactly the hook's question ("Ou seja, ...") and lands one last line (or asks the question out
  loud, if divisive).
- [ ] It sounds spoken: casual words in small doses, sources named where the sources do, doubt admitted.
- [ ] Read aloud: no stumbles, numbers in words, accents everywhere, no Roman numerals or abbreviations.
- [ ] Neutral: no current politics, no side taken, religion told as history.
- [ ] The hook type, the protagonist type and the ending differ from the last few videos.

## 15. Measuring and learning

The guide is a starting point. The channel's own numbers decide.

**What to watch, per video.**

| Metric | Where | What it says about the script |
| --- | --- | --- |
| Viewed vs. swiped away | YouTube Studio, Advanced mode | The hook (and the first shot). |
| Average percentage viewed | YouTube Studio, YouTube Analytics API | The middle and the length. |
| Retention curve | YouTube Studio | Where exactly people leave: which section lost them. |
| Over 100% average viewed | YouTube Studio | Whether the loop works. |
| Comments per thousand views | `/stats`, YouTube Data API | Whether the question works. |
| Completion, rewatches | TikTok Studio | Same questions on TikTok. |

Today `/stats` reads only views, likes and comments (YouTube Data API `videos.list`). Retention and swipe-away need
the YouTube Analytics API (scope `yt-analytics.readonly`), which would be a natural next step: store them with each
run record on the `state` branch so scripts can be compared.

**What to record about each script**, so results can be grouped: hook type (section 6), protagonist type (ruler,
soldier, crowd, inventor, object), era and region, whether it had a Brazil bridge, ending type (loop, question out
loud), word count, number of names. Most of this can be tagged by the writer as extra fields at no extra request.

**How to test.** Change one thing at a time, across several videos, and compare groups, never single videos: one
Short's numbers swing wildly with the luck of its first audience. Ten videos per group is a bare minimum before
drawing a conclusion. Good first tests are in section 16.

**How to update.** When a result is clear, change `prompts/writer.md` (the rule) and this guide (the reason and the
evidence, marked **Channel data** with the dates and video count). Keep the prompt short: rules there, reasons here.

## 16. Open questions

Worth testing, roughly in order of expected impact:

1. **Length.** Do the 220 to 380 word videos (since 2026-10-29) earn more total watch time and follows than the
   130 to 170 word ones, despite a lower average percentage viewed? Compare the two groups once each has ten videos.
2. **Hook types.** Which of the types in section 6 has the lowest swipe-away for this audience?
3. **Brazil bridge.** Do videos with a sourced link to Brazil or to the school curriculum hold better?
4. **Era.** The topics are Brazil only for now, mixed over the colony, the Empire and the Republic. Which period
   holds best, and do world stories with a Brazil bridge do as well as Brazilian ones when they come back?
5. **Closed ending vs. loop.** Does closing the idea lose the rewatches the loop used to bring, and does it gain
   comments or follows?
6. **Scale comparisons.** Should the prompt allow "do tamanho de dois Maracanãs"-style comparisons, given the facts
   rule? If yes, with what limits?
7. **Hook length.** The prompt allows 20 words. Do hooks of 8 words or fewer do better?
8. **Voice speed.** The reference channel speaks at about 177 words a minute; our narrator at about 150. Does a
   slightly faster voice (Smallest `speed` 1.1) hold attention better in longer videos?

## 17. Sources

Research

- Loewenstein, G. (1994). The psychology of curiosity: a review and reinterpretation. *Psychological Bulletin*.
  Summary of the information-gap model: [Golman and Loewenstein, The desire for knowledge and wisdom](https://www.cmu.edu/dietrich/sds/docs/golman/the-desire-for-knowledge-and-wisdom.pdf).
- Recht and Yeung, curiosity, confidence and memory in trivia experiments: [University of Oxford](https://www.psy.ox.ac.uk/publication/2440158).
- Green and Brock (2000), narrative transportation: [overview](https://en.wikipedia.org/wiki/Transportation_theory_(psychology)),
  [Green and Appel (2024) review, preprint](https://mcm.uni-wuerzburg.de/fileadmin/06110300/2024/Pdfs/Green___Appel__2024__Advances_Preprint.pdf).
- Kahneman, Fredrickson, Schreiber and Redelmeier (1993), peak-end rule: [overview](https://en.wikipedia.org/wiki/Peak%E2%80%93end_rule).

Platforms and policy

- YouTube Shorts algorithm and the "Viewed vs. swiped away" metric: [vidIQ](https://vidiq.com/blog/post/youtube-shorts-algorithm/).
- YouTube inauthentic content policy: [Plagiarism Today](https://www.plagiarismtoday.com/2025/07/08/youtube-targets-inauthentic-content/),
  [TechRepublic](https://techrepublic.com/article/news-youtube-ai-video-monetization-rules). Check the official YouTube
  Partner Program policies for the current wording.

Audience

- Daily social media time by country, Brazil first: [Statista, from We Are Social and DataReportal](https://www.statista.com/statistics/433871/daily-social-media-usage-worldwide/).
- TikTok users in Brazil and Brazilian history humor on TikTok: [TecMundo](https://www.tecmundo.com.br/internet/404180-portugal-agora-e-guiana-brasileira-entenda-a-colonizacao-reversa-que-viralizou-no-tiktok.htm).
- Brazilians using TikTok to study (survey commissioned by TikTok): [Portal de Prefeitura](https://portaldeprefeitura.com.br/curiosidades/pesquisa-aponta-que-brasileiros-usam-tiktok-para-estudar/617130/).

Practice (creator and vendor guides; useful patterns, unverified numbers)

- [Bytecap, Shorts retention benchmarks](https://www.bytecap.io/research/youtube-shorts-retention-benchmarks)
- [Kineclip, three acts for 60 second stories](https://kineclip.com/blog/three-act-structure-for-60-second-stories-2026/)
  and [ending a short video](https://kineclip.com/blog/how-to-end-a-short-video-payoff-loop-cta-2026/)
- [TikTok completion rate by video length, estimates](https://ttcalculator.net/learn/completion-rate-by-video-length/)

## 18. The reference channel

On 2026-10-29 the owner asked for scripts that phrase, open, explain and close the way
[Estranha História](https://www.youtube.com/@henriquecaldeira) does: a Brazilian history channel by the historian
Henrique Caldeira, around 800 thousand subscribers. We read the Portuguese captions of his 30 most viewed Shorts and
20 most viewed long videos (one Short had none, so 49 transcripts). The transcripts were only read for this analysis
and are not kept in the repository; the prompt borrows patterns and common spoken words, never his lines or his
catchphrases, so the channel sounds like itself and not like a copy.

**Numbers (Channel data, his).** Shorts: 100 to 563 words, median 318 (about two minutes). Long videos: about 177
words a minute. Per thousand words in the Shorts: "né" 19, "mas" 9, "então" 5, "a gente" 4, "só que" 1, "ou seja"
1, "por isso" 0.3.

**How he opens.** Straight into the subject, never a greeting in Shorts. Four shapes: a direct "por que" question
about something familiar ("why do we say OK"); pointing at a strange picture ("this haircut is odd, right?"); a claim
that sounds wrong ("one of the most important kings in history probably never existed"); a playful hypothetical to
the viewer. Long videos then say in one line what the video will cover.

**How he explains.** Often first the common version or the popular theories, then the turn ("but the story is much
stranger than that"). Then step by step in time order, joined by spoken links. He names his source when a fact is
striking ("according to Plutarch", "a seventeenth century etiquette manual says, quote"), says plainly what cannot be
known, and makes a hard idea land with a comparison from today (Cleopatra lived closer to the iPhone than to the
building of the pyramids). Humor comes from the facts: dry understatement, irony, an absurd detail taken seriously.

**How he closes.** He answers the opening question in plain words, often with "ou seja", and lands a last line that
calls back to the start or turns it with a joke (after explaining why clocks show IIII: "now you can never unsee
it"). Sometimes a question to the comments. No moral, no summary of everything said.

**What we did not copy.** His first person opinions ("eu acho"), his sign-offs and channel lines, his on-camera
asides, and the amount of filler a live speaker gets away with.
