"""Pure helpers for voice.py: matching the script's words to what Whisper heard.

Copied from Flopômetro (canal-dark tools/tts/align.py); kept free of model imports so the tests run in a second.
"""

import difflib
import re

# A group shorter than this is merged with the next sentence: "Wait." on its own often comes out clipped or garbled.
MIN_GROUP_WORDS = 6

SECTION_PAUSE = 0.35
PAUSE_AFTER = {"?": 0.28, "!": 0.22, ".": 0.2}
DEFAULT_PAUSE = 0.15


def sentence_groups(section: str) -> list[str]:
    """Splits a section into sentences, merging short ones into the next so each group is worth one generation."""
    sentences = [s.strip() for s in re.split(r"(?<=[.!?])\s+", section.strip()) if s.strip()]
    groups, pending = [], ""
    for sentence in sentences:
        pending = f"{pending} {sentence}".strip()
        if len(pending.split()) >= MIN_GROUP_WORDS:
            groups.append(pending)
            pending = ""
    if pending:
        if groups:
            groups[-1] = f"{groups[-1]} {pending}"
        else:
            groups.append(pending)
    return groups


def pause_after(group: str) -> float:
    """Silence after a group: a question gets a beat for the viewer to answer, a plain statement less."""
    return PAUSE_AFTER.get(group.rstrip()[-1:], DEFAULT_PAUSE)


def normalize(word: str) -> str:
    return re.sub(r"[^\w]", "", word.lower().replace("’", "'").replace("'", ""))


NUMBER = re.compile(r"^(\$?)(\d{1,3}(?:[.,]\d{3})+|\d+(?:[.,]\d+)?)(st|nd|rd|th)?(%?)$")


# num2words language codes, and the words that follow a dollar or percent amount, per narration language.
NUMBER_LANGUAGES = {"en": ("en", "dollars", "percent"), "pt": ("pt_BR", "dólares", "por cento")}

# Spoken Brazilian forms the script uses and Whisper writes out in full ("pra" is heard as "para").
SPOKEN_FORMS = {
    "pt": {"pra": ["para"], "pro": ["para", "o"], "pras": ["para", "as"], "pros": ["para", "os"], "tá": ["está"],
           "tô": ["estou"], "cê": ["você"], "né": ["não", "é"]},
}


def number_value(digits: str, language: str) -> int | float:
    """"4,000" and "2.5" in English; "4.000" and "2,5" in Portuguese, which swaps the separators."""
    thousands, decimal = (",", ".") if language == "en" else (".", ",")
    if re.fullmatch(rf"\d{{1,3}}(?:{re.escape(thousands)}\d{{3}})+", digits):
        return int(digits.replace(thousands, ""))
    digits = digits.replace(thousands, "").replace(decimal, ".")
    return float(digits) if "." in digits else int(digits)


def spoken_parts(word: str, language: str = "en") -> list[str]:
    """
    The normalized words a script or Whisper token stands for. "200" and "two hundred" both become
    ["two", "hundred"], "$70" becomes ["seventy", "dollars"], "22nd" becomes ["twentysecond"].
    In Portuguese, "200" becomes ["duzentos"] and "pra" becomes ["para"].
    """
    from num2words import num2words

    raw = re.sub(r"^[^\w$]+|[^\w%]+$", "", word.strip())
    number = NUMBER.match(raw)
    if not number:
        # Hyphens split on both sides, so "twenty-second" and "anti-cheat" match however Whisper writes them.
        # A respelled word ("plêi teste") is matched by its spoken parts, so spaces split it too.
        forms = SPOKEN_FORMS.get(language, {})
        parts = [w for w in (normalize(p) for p in re.split(r"[\s\-\u2010-\u2014]", word)) if w]
        return [f for p in parts for f in forms.get(p, [p])]
    dollar, digits, ordinal, percent = number.groups()
    lang, dollars, per_cent = NUMBER_LANGUAGES[language]
    value = number_value(digits, language)
    if ordinal and isinstance(value, int):
        text = num2words(value, to="ordinal", lang=lang)
    elif isinstance(value, int) and 1900 <= value <= 2099 and not dollar and "," not in digits:
        # Portuguese reads a year like any number ("dois mil e vinte e sete").
        text = num2words(value, to="year", lang=lang) if language == "en" else num2words(value, lang=lang)
    else:
        text = num2words(value, lang=lang)
    text += f" {dollars}" if dollar else ""
    text += f" {per_cent}" if percent else ""
    return [w for w in (normalize(p) for p in text.replace("-", " ").split()) if w]


def parse_pronunciations(lines) -> dict[str, str]:
    """
    "word<tab>spoken form" per line; blank lines and lines starting with # are skipped. Matching is case-sensitive, so
    "IA" (inteligência artificial) is respelled and the verb "ia" is not.
    """
    table = {}
    for line in lines:
        line = line.rstrip("\r\n")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        word, _, spoken = line.partition("\t")
        if word.strip() and spoken.strip():
            table[word.strip()] = spoken.strip()
    return table


def respell(word: str, table: dict[str, str], language: str = "en") -> str:
    """
    How the voice should say one script word, keeping its punctuation: "IA," becomes "i-á,". Outside English,
    numbers are spelled out too ("2027." becomes "dois mil e vinte e sete."): Chatterbox reads Portuguese digits
    unreliably, once saying "2016" for "2027".
    """
    lead, core, tail = re.match(r"^(\W*)(.*?)(\W*)$", word).groups()
    if core in table:
        return f"{lead}{table[core]}{tail}"
    if language != "en" and NUMBER.match(re.sub(r"^[^\w$]+|[^\w%]+$", "", word)):
        lead = re.match(r"^[^\w$]*", word).group()
        tail = re.search(r"[^\w%]*$", word).group()
        return f"{lead}{' '.join(spoken_parts(word, language))}{tail}"
    return word


def align(script_words: list[str], heard: list[tuple[str, float, float]], start: float, end: float,
          language: str = "en", written: list[str] | None = None):
    """
    Gives every script word a (start, end) inside [start, end], using the words Whisper heard as anchors.

    Captions keep the script's spelling; only the timing comes from Whisper. Script words Whisper missed share the
    time between their neighbouring anchors, in proportion to their length.

    written: the script's own spelling of each word, when script_words are respelled for the voice. Whisper often
    writes the name it recognises ("Xbox") rather than the respelling it heard ("Équis-bóx"), so either counts.

    Returns (times, score): score is the share of the script's spoken words that Whisper heard, 0-1.
    """
    if not script_words:
        return [], 1.0
    # Numbers are compared as words on both sides: the script may say "two hundred" where Whisper writes "200".
    wanted, owner = [], []
    for index, word in enumerate(script_words):
        for part in spoken_parts(word, language):
            wanted.append(part)
            owner.append(index)
    respelled = {}
    for original, spoken in zip(written or [], script_words):
        if normalize(original) and spoken_parts(original, language) != spoken_parts(spoken, language):
            respelled[normalize(original)] = spoken_parts(spoken, language)
    got, got_times = [], []
    for word, s, e in heard:
        parts = respelled.get(normalize(word)) or spoken_parts(word, language)
        step = (e - s) / max(len(parts), 1)
        for k, part in enumerate(parts):
            got.append(part)
            got_times.append((s + k * step, s + (k + 1) * step))
    matcher = difflib.SequenceMatcher(None, wanted, got, autojunk=False)

    times: list[tuple[float, float] | None] = [None] * len(script_words)
    matched = 0
    for block in matcher.get_matching_blocks():
        for k in range(block.size):
            s, e = got_times[block.b + k]
            index = owner[block.a + k]
            times[index] = (s, e) if times[index] is None else (times[index][0], e)
            matched += 1

    i = 0
    while i < len(times):
        if times[i] is not None:
            i += 1
            continue
        j = i
        while j < len(times) and times[j] is None:
            j += 1
        gap_start = times[i - 1][1] if i > 0 else start
        gap_end = times[j][0] if j < len(times) else end
        if gap_end <= gap_start:
            gap_end = gap_start + 0.05 * (j - i)
        lengths = [max(len(normalize(script_words[k])), 1) for k in range(i, j)]
        total, cursor = sum(lengths), gap_start
        for k, length in zip(range(i, j), lengths):
            step = (gap_end - gap_start) * length / total
            times[k] = (cursor, cursor + step)
            cursor += step
        i = j

    # Whisper's word times can overlap, and on very short clips run past the end; captions need them in order and
    # inside the clip.
    fixed, previous_end = [], start
    for s, e in times:
        s = min(max(s, previous_end), end)
        e = min(max(e, s + 0.01), end)
        fixed.append((round(s, 3), round(e, 3)))
        previous_end = e
    return fixed, matched / max(len(wanted), 1)


def extra_words(script_words: list[str], heard: list[tuple[str, float, float]], language: str = "en") -> int:
    """
    How many more words Whisper heard than the script has. A voice that takes instructions with the text (Gemini)
    can read part of the instructions aloud; the share-heard score alone would not notice the added words.
    """
    wanted = sum(len(spoken_parts(w, language)) for w in script_words)
    got = sum(len(spoken_parts(w, language)) for w, _, _ in heard)
    return got - wanted


def section_times(words: list[tuple[str, float, float, int]], sections: int, duration: float) -> list[dict]:
    """
    Start and end of each section from its words' times, for a recording made in one take. A section starts where
    its first word does (the first at 0) and ends just after its last word, never past the next section's start.
    """
    times = []
    for index in range(sections):
        own = [(s, e) for _, s, e, section in words if section == index]
        if not own:
            raise ValueError(f"section {index} has no words")
        times.append([0.0 if index == 0 else own[0][0], own[-1][1]])
    for index, current in enumerate(times):
        limit = times[index + 1][0] if index + 1 < len(times) else duration
        current[1] = min(current[1] + 0.15, limit)
    return [{"start": round(s, 3), "end": round(e, 3)} for s, e in times]
