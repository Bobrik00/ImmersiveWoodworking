# -*- coding: utf-8 -*-
"""Machine checks over the mod's lang files. Run it after ANY lang edit.

    python tools/check-lang.py

Exit code 0 = all green. Nothing here needs the game running.

The mod ships text in TWO asset domains -- `immersivewoodworking` (five languages) and `xskills` (two, the
xskills-domain copies that make the Gilded GUI show real ability names). Both are found by walking
`assets/*/lang/`, never by a literal, so a third domain is picked up the day it appears: the domain root used to
be hardcoded and the xskills folder was checked by nothing at all (AuditFixPlan T09.02, F-L7-09).

Checks, in order:
  1. every file parses as JSON
  2. inside each DOMAIN, all files carry the SAME key set in the SAME order. The files are structural mirrors
     of each other on purpose: that is how a missing key is caught by eye before any machine runs.
  3. no dash-like codepoint anywhere in any key or value.
     Project rule: player-visible text uses hyphen-minus only (memory vs-no-dashes-in-player-text).
  4. VTML is well formed: tags balanced, every <a> carries an href, no bare < or > in prose, and
     every tag NAME is one the engine actually handles. An unknown tag is not merely ignored: the
     switch in VtmlParser.Richtextify misses, the TagConverters lookup misses, and ChildElements are
     never recursed -- so the text INSIDE the tag disappears with no log line.
  5. every <hk> carries a REGISTERED hotkey code. An unknown code renders as a box with a single "?"
     (HotkeyComponent.cs:55-58) and logs nothing; vanilla breaks itself this way 6 times in de.json
     and 4 times in ru.json.
  6. every handbook:// target of a given key is IDENTICAL across all languages of its domain. A translator
     who "translates" an href breaks the link silently -- a dead handbook anchor produces no log
     line, no chat message and no exception (memory vs-handbook-link-anchors).
  7. no stray "{" placeholder outside the keys meant to carry one, and the placeholders a key does
     carry are the SAME in all languages (a dropped {0} costs the species name silently).
  8. blockhelp-* values carry no trailing period (they are HUD labels, not sentences)
  9. no duplicate keys -- json.loads keeps the last one and the first is lost without a word
 10. every lang key an ASSET points at exists and is non-empty in every file of its domain: handbook
     extraSections in blocktypes/itemtypes, and the title/text of a handbook page under config/handbook.
     A typo there costs a raw key on a handbook page and logs nothing.
 11. every lang key the C# points at exists too. Keys reach the handbook as DATA as well as through Lang.*
     (an ExtraHandbookSection carries a key that the ENGINE resolves), so check 10 alone printed "all present"
     having looked at a fraction of them (F-L7-18). Keys assembled from a variable cannot be resolved here;
     their prefixes are listed in RUNTIME_KEY_FAMILIES by hand, so the blind spot is a number rather than a
     silence.
 12. no value in a translated file is byte-identical to its English original. That is what an untranslated
     string looks like, and until this check existed nothing could tell one from a finished translation --
     the mod's own handbook title sat in English in all five files (F-L7-04). Deliberate exceptions live in
     tools/lang-untranslated-ok.json, one entry per key with a reason, and an entry that stops matching is a
     failure too, so the list cannot rot into a mute allowlist.
 13. a key shipped by TWO domains carries the same value in both. The xskills copies are documented as
     identical to the mod's own; they had already drifted in ru when the check was written.
"""
import io
import json
import os
import re
import sys
from collections import OrderedDict

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, ".."))
ASSETROOT = os.path.join(ROOT, "assets")
DOMAIN = "immersivewoodworking"                     # the mod's own domain: assets, and the five-language rule
ASSETS = os.path.join(ASSETROOT, DOMAIN)
LANGS = ["en", "ru", "de", "zh-cn", "pt-br"]        # required in the main domain; other domains ship what they ship
REF = "en"                                          # the language every other one is a translation OF
UNTRANSLATED_OK = os.path.join(HERE, "lang-untranslated-ok.json")

# Not a translation file: vanilla's lang folder carries one of these as the language REGISTRY.
NOT_A_LANG_FILE = {"languages"}

# Keys starting with an underscore are file metadata by convention (the xskills folder documents itself in a
# "_comment"), never shown to a player. They take part in the key-set mirror and nothing else: prose checks would
# fire on the angle brackets and placeholders such a note quotes.
def is_metadata(key):
    return key.startswith("_")


BAD_DASHES = {
    "—": "em dash",
    "–": "en dash",
    "‒": "figure dash",
    "―": "horizontal bar",
    "−": "minus sign",
    "‐": "unicode hyphen",
    "‑": "non-breaking hyphen",
    "﹘": "small em dash",
    "﹣": "small hyphen-minus",
    "－": "fullwidth hyphen-minus",
    "­": "soft hyphen",
}

# EXACTLY the cases handled in VtmlParser.Richtextify (vsapi/Common/Text/VtmlParser.cs:74-222).
# Anything else silently swallows its own content -- see check 4 in the module docstring.
KNOWN_TAGS = {"br", "hotkey", "hk", "i", "a", "icon", "itemstack", "font", "clear", "code", "strong"}
VOID_TAGS = {"br", "clear"}

# Hotkey codes the engine registers (Vintagestory.Client.HotkeyManager) plus the aliases the VTML
# parser rewrites before lookup (VtmlParser.cs:81-84). The alias comparison is EXACT, so a stray
# space inside the tag also breaks it -- vanilla ru.json ships "<hk>rightmouse </hk>" and gets "?".
HK_ALIASES = {"leftmouse": "primarymouse", "rightmouse": "secondarymouse", "toolmode": "toolmodeselect"}
HK_CODES = {"ctrl", "shift", "sneak", "sprint", "jump", "primarymouse", "secondarymouse",
            "toolmodeselect", "handbook", "journal", "selectionhandbook", "chatdialog"}

TAG_RE = re.compile(r"</?([a-zA-Z][a-zA-Z0-9]*)[^>]*>")
HK_RE = re.compile(r"<(?:hk|hotkey)>([^<]*)</(?:hk|hotkey)>")
PLACEHOLDER_RE = re.compile(r"\{(\d+)\}")
HREF_RE = re.compile(r'href="([^"]*)"')
# The three handbook pages that are COMPOSED at runtime are named in full rather than by a pattern:
# WoodworkingHandbookInfo.OwnPages fills their {0}/{1}/{2} with a config-conditional sentence or paragraph
# (AuditFixPlan T09.01; the Woodworking overview since the subsystem switches), and every OTHER handbook text
# must keep failing this check.
PLACEHOLDER_OK = re.compile(
    r"(-name$|^blockdesc-|^itemdesc-|^ingameerror-|^blockinfo-|^contained-|^abilitydesc-"
    r"|^choppingblock-handbook-text$|^sawhorse-handbook-text$|^craftinginfo-woodworking-text$)")

# ── C# key extraction (check 11) ─────────────────────────────────────────────────────────────────────────
# EVERY domain-prefixed string literal in the C# is treated as a lang key, because a key reaches the player by
# three routes and only one of them is greppable. Through Lang.* directly; as the ActionLangCode of a
# WorldInteraction or the code handed to TriggerIngameError, which the ENGINE resolves; and as the Text of an
# ExtraHandbookSection, resolved when the page is drawn. Scanning only Lang.* call sites saw 75 of the 127 keys
# and called it a complete pass -- the same shape of blindness this check exists to remove (F-L7-18).
#
# The price of scanning everything is that a domain-prefixed literal which is NOT a key has to be recognised.
# Exactly one shape of those exists: the label TesselateShape takes to name a shape in its own log lines. Its
# argument list is masked out below, so the two hand-kept tables stay about lang keys alone.
CS_SKIP_DIRS = {"bin", "obj", "graphify-out", ".git", ".shared", "Plans_archive", "Releases", "Releases-archive"}
LANG_CALL_RE = re.compile(r"\bLang\.[A-Za-z]+\s*\(")
TESSELATE_RE = re.compile(r"\bTesselateShape\s*\(")
# `Domain` is the mod's own const (only WoodworkingHandbookInfo declares one, = the main domain). The lookbehind
# keeps `Code.Domain`, `woodDomain` and `logDomain` out: those build asset codes and mesh cache keys, not keys.
DOMAIN_CONCAT_RE = re.compile(r'(?<![A-Za-z0-9_.])Domain\s*\+\s*"\:')
LITERAL_ARG_RE = re.compile(r'"([a-z][a-z0-9]*)\:([^"]*)"')

# Key families the C# assembles from a variable, listed by hand because no static reader can resolve them. The
# point of the list is the NUMBER: a new family that nobody wrote down here fails the check rather than joining
# the ones already invisible.
RUNTIME_KEY_FAMILIES = {
    "immersivewoodworking:ability-":
        "XSkills.cs: one per ability name registered into the forestry skill",
    "immersivewoodworking:abilitydesc-":
        "XSkills.cs: the description beside each of those",
    "xskills:ability-":
        "XSkills.cs debug dump: reads back what the Gilded GUI will look up in the xskills domain",
    "immersivewoodworking:material-":
        "WoodName.cs: the species word, last of three lookups after the bare name and the log's own domain",
    "immersivewoodworking:item-":
        "BlockEntitySawmill.cs: names the product item of the current cut",
    "immersivewoodworking:":
        "BlockEntityChopper/Sawmill.Parts.cs and BlockEntityChopper.cs: an ingame-error code and a part name, "
        "both whole keys held in a variable",
}

# Domain-prefixed literals that are not lang keys after all. Empty today: the only shape that existed, the
# TesselateShape label, is masked by the scanner itself, which is better than listing each one. Kept as the
# declared way out, so the next such literal is written down here rather than waved through by loosening
# the scan.
NOT_LANG_KEYS = {}


failures = []


def fail(msg):
    failures.append(msg)


BACKSLASH = chr(92)
IDENT_START = re.compile(r"[A-Za-z_$]")
IDENT = re.compile(r"[A-Za-z0-9_$]*")


def normalize_vs_json(text):
    """Turn a Vintage Story asset into strict JSON.

    The engine's reader is lenient in three ways this mod's assets all use: // and /* */ comments,
    UNQUOTED object keys, and trailing commas. Python's json accepts none of them, so normalise in
    one string-aware pass rather than with regexes that would also rewrite the inside of strings
    (an href like "handbook://..." contains // and would be eaten by a naive comment stripper).
    """
    out = []
    i = 0
    n = len(text)
    prev_significant = ""

    def skip_filler(j):
        """Advance past whitespace and comments; return the next content index."""
        while j < n:
            if text[j].isspace():
                j += 1
            elif text[j] == "/" and j + 1 < n and text[j + 1] == "/":
                while j < n and text[j] != "\n":
                    j += 1
            elif text[j] == "/" and j + 1 < n and text[j + 1] == "*":
                j += 2
                while j + 1 < n and not (text[j] == "*" and text[j + 1] == "/"):
                    j += 1
                j += 2
            else:
                break
        return j

    while i < n:
        c = text[i]

        if c == '"':                                    # copy strings verbatim
            out.append(c)
            i += 1
            esc = False
            while i < n:
                ch = text[i]
                out.append(ch)
                i += 1
                if esc:
                    esc = False
                elif ch == BACKSLASH:
                    esc = True
                elif ch == '"':
                    break
            prev_significant = '"'
            continue

        if c == "'":                                    # single-quoted string -> double-quoted
            i += 1
            body = []
            esc = False
            while i < n:
                ch = text[i]
                i += 1
                if esc:
                    body.append(ch)
                    esc = False
                elif ch == BACKSLASH:
                    body.append(ch)
                    esc = True
                elif ch == "'":
                    break
                else:
                    body.append(ch)
            out.append('"' + "".join(body).replace('"', BACKSLASH + '"') + '"')
            prev_significant = '"'
            continue

        if c == "/" and i + 1 < n and text[i + 1] in "/*":   # comments
            i = skip_filler(i)
            continue

        if c == "," and text[skip_filler(i + 1):skip_filler(i + 1) + 1] in ("}", "]"):
            i += 1                                      # trailing comma
            continue

        if IDENT_START.match(c) and prev_significant in ("{", ","):
            word = IDENT.match(text, i).group(0)
            after = skip_filler(i + len(word))
            if after < n and text[after] == ":":        # bare object key
                out.append('"' + word + '"')
                i += len(word)
                prev_significant = '"'
                continue

        out.append(c)
        if not c.isspace():
            prev_significant = c
        i += 1

    return "".join(out)


def check_tags(where, key, value):
    stack = []
    for m in TAG_RE.finditer(value):
        tag = m.group(1).lower()
        if tag not in KNOWN_TAGS:
            fail("%s / %s: <%s> is not a tag the engine handles; its CONTENT would vanish"
                 % (where, key, tag))
            continue
        if m.group(0).startswith("</"):
            if not stack or stack[-1] != tag:
                fail("%s / %s: unbalanced closing tag </%s>" % (where, key, tag))
                return
            stack.pop()
        elif tag not in VOID_TAGS and not m.group(0).endswith("/>"):
            stack.append(tag)
    if stack:
        fail("%s / %s: unclosed tag(s) %s" % (where, key, stack))

    # A bare < or > left in prose is not escaped by anyone downstream; it either eats the rest of
    # the line as a phantom tag or prints raw. Mask the tags we just validated, then look at what is left.
    for i, ch in enumerate(TAG_RE.sub(lambda m: " " * len(m.group(0)), value)):
        if ch in "<>":
            fail("%s / %s: bare %r at offset %d (escape it as &lt; / &gt;): ...%s..."
                 % (where, key, ch, i, value[max(0, i - 25):i + 25]))


def check_hotkeys(where, key, value):
    for code in HK_RE.findall(value):
        resolved = HK_ALIASES.get(code, code)
        if code != code.strip():
            fail("%s / %s: <hk>%r</hk> has surrounding whitespace; the alias lookup is exact"
                 % (where, key, code))
        elif resolved.lower() not in HK_CODES:
            fail("%s / %s: <hk>%s</hk> is not a registered hotkey code; it renders as a '?' box"
                 % (where, key, code))


# ── domains ──────────────────────────────────────────────────────────────────────────────────────────────

def discover_domains():
    """Every asset domain this mod ships text in, as {domain: [language, ...]}, found by walking the asset
    tree. The mod has two; the second one existed for a release before any check looked at it."""
    domains = {}
    if not os.path.isdir(ASSETROOT):
        fail("no assets folder at " + ASSETROOT)
        return domains

    for name in sorted(os.listdir(ASSETROOT)):
        langdir = os.path.join(ASSETROOT, name, "lang")
        if not os.path.isdir(langdir):
            continue
        langs = [os.path.splitext(f)[0] for f in os.listdir(langdir)
                 if f.endswith(".json") and os.path.splitext(f)[0] not in NOT_A_LANG_FILE]
        # English first because everything else is measured against it, then the shipping order, then
        # whatever a future domain adds.
        langs.sort(key=lambda l: (LANGS.index(l) if l in LANGS else len(LANGS), l))
        if langs:
            domains[name] = langs
    return domains


def load_domain(domain, langs):
    """Parse one domain's files, checking duplicate keys on the way (check 9). Returns {lang: OrderedDict}."""
    data = OrderedDict()
    for lang in langs:
        path = os.path.join(ASSETROOT, domain, "lang", lang + ".json")
        if not os.path.exists(path):
            fail("missing file: " + path)
            continue
        try:
            with open(path, encoding="utf-8") as fh:
                raw = fh.read()
            data[lang] = json.loads(raw, object_pairs_hook=OrderedDict)
        except Exception as exc:  # noqa: BLE001
            fail("%s/%s.json: JSON does not parse: %s" % (domain, lang, exc))
            continue
        pairs = []
        json.loads(raw, object_pairs_hook=lambda kv: pairs.extend(kv) or OrderedDict(kv))
        seen = set()
        for k, _v in pairs:
            if k in seen:
                fail("%s/%s.json: duplicate key %r; json keeps the LAST one and the first is lost"
                     % (domain, lang, k))
            seen.add(k)
    return data


def check_key_mirror(domain, data):
    """Check 2, inside one domain: same keys, same order, measured against that domain's own English file."""
    ref_keys = list(data[REF].keys())
    for lang, d in data.items():
        keys = list(d.keys())
        if keys == ref_keys:
            print("%-14s %-6s %d keys, order identical to %s" % (domain, lang, len(keys), REF))
            continue
        missing = [k for k in ref_keys if k not in d]
        extra = [k for k in keys if k not in data[REF]]
        if missing:
            fail("%s/%s: missing keys %s" % (domain, lang, missing[:10]))
        if extra:
            fail("%s/%s: extra keys %s" % (domain, lang, extra[:10]))
        if not missing and not extra:
            first = next(i for i, (a, b) in enumerate(zip(keys, ref_keys)) if a != b)
            fail("%s/%s: key ORDER diverges at index %d (%s vs %s %s)"
                 % (domain, lang, first, keys[first], REF, ref_keys[first]))
    return ref_keys


def check_values(domain, data):
    """Checks 3, 4, 5, 7 (the stray-brace half) and 8 over every value of one domain. Returns the hrefs seen
    and how many metadata keys were skipped, so the summary can say so out loud."""
    hrefs = set()
    skipped = 0
    for lang, d in data.items():
        for key, value in d.items():
            if not isinstance(value, str):
                continue
            if is_metadata(key):
                skipped += 1
                continue
            where = "%s/%s" % (domain, lang)
            for ch, name in BAD_DASHES.items():
                if ch in value or ch in key:
                    fail("%s / %s: contains %s" % (where, key, name))
            check_tags(where, key, value)
            check_hotkeys(where, key, value)
            for m in re.finditer(r"<a\b[^>]*>", value):
                if "href=" not in m.group(0):
                    fail("%s / %s: <a> without href: %s" % (where, key, m.group(0)))
            hrefs.update(HREF_RE.findall(value))
            if "{" in value and not PLACEHOLDER_OK.search(key):
                fail("%s / %s: unexpected '{' in value" % (where, key))
            if key.startswith("blockhelp-") and value.rstrip().endswith((".", "。")):
                fail("%s / %s: blockhelp value ends with a period" % (where, key))
    return hrefs, skipped


def check_cross_language(domain, data, ref_keys):
    """Checks 6 and 7: the same key must aim its handbook:// links at the same page and carry the same
    placeholders in every language of the domain."""
    for key in ref_keys:
        if is_metadata(key):
            continue
        per_lang = {}
        placeholders = {}
        for lang, d in data.items():
            v = d.get(key)
            if isinstance(v, str):
                per_lang[lang] = tuple(sorted(
                    h for h in HREF_RE.findall(v) if h.startswith("handbook://")))
                placeholders[lang] = tuple(sorted(set(PLACEHOLDER_RE.findall(v))))
        if len(set(per_lang.values())) > 1:
            fail("%s / %s: handbook:// targets differ between languages: %s" % (domain, key, per_lang))
        if len(set(placeholders.values())) > 1:
            fail("%s / %s: placeholders differ between languages: %s" % (domain, key, placeholders))


# ── check 12: a translated file that still holds the English string ───────────────────────────────────────

def load_untranslated_allowlist():
    """The deliberate exceptions, as {(domain, key): (langs, why)}. A file rather than a set inside this
    script so that the count is something the author writes down and a reviewer can read."""
    if not os.path.exists(UNTRANSLATED_OK):
        fail("missing exception list: " + UNTRANSLATED_OK)
        return {}
    try:
        with open(UNTRANSLATED_OK, encoding="utf-8") as fh:
            doc = json.load(fh)
    except Exception as exc:  # noqa: BLE001
        fail("%s does not parse: %s" % (os.path.basename(UNTRANSLATED_OK), exc))
        return {}

    allow = {}
    for entry in doc.get("exceptions", []):
        try:
            key = (entry["domain"], entry["key"])
            langs = set(entry["langs"])
            why = entry["why"].strip()
        except (KeyError, AttributeError):
            fail("%s: an entry is missing one of domain/key/langs/why: %r"
                 % (os.path.basename(UNTRANSLATED_OK), entry))
            continue
        if not why:
            fail("%s: %s has an empty 'why'; an exception without a reason is a silence"
                 % (os.path.basename(UNTRANSLATED_OK), key[1]))
        allow[key] = (langs, why)
    return allow


def check_untranslated(domains_data, allow):
    """Check 12. Every value equal to its English original is either a finding or a listed exception; a listed
    exception that no longer matches is a finding of its own, so the list stays a record of the present."""
    used = set()
    found = 0
    for domain, data in domains_data.items():
        if REF not in data:
            continue
        ref = data[REF]
        for lang, d in data.items():
            if lang == REF:
                continue
            for key, value in d.items():
                if is_metadata(key) or not isinstance(value, str):
                    continue
                if ref.get(key) != value:
                    continue
                langs, _why = allow.get((domain, key), (set(), ""))
                if lang in langs:
                    used.add((domain, key, lang))
                    continue
                found += 1
                fail("%s/%s / %s: identical to %s -- untranslated? (%r). If it is right, add it to %s"
                     % (domain, lang, key, REF, value[:60], os.path.basename(UNTRANSLATED_OK)))

    for (domain, key), (langs, _why) in sorted(allow.items()):
        for lang in sorted(langs):
            if (domain, key, lang) not in used:
                fail("%s: %s/%s/%s is listed as a deliberate exception but no longer matches the English "
                     "string; delete the entry" % (os.path.basename(UNTRANSLATED_OK), domain, key, lang))
    exceptions = sum(len(langs) for langs, _ in allow.values())
    print("%-14s %d key(s) / %d key+language pair(s) accepted as deliberately identical to %s"
          % ("exceptions", len(allow), exceptions, REF))
    return found


def check_cross_domain(domains_data):
    """Check 13. The xskills folder duplicates the ability strings so the Gilded GUI can resolve them in its own
    domain, and says in its own comment that the copies are identical. Nothing enforced that until now."""
    shared = 0
    keys_by_domain = {dom: set(data[REF].keys()) for dom, data in domains_data.items() if REF in data}
    domains = sorted(keys_by_domain)
    for i, a in enumerate(domains):
        for b in domains[i + 1:]:
            common = sorted(k for k in keys_by_domain[a] & keys_by_domain[b] if not is_metadata(k))
            for key in common:
                shared += 1
                for lang in sorted(set(domains_data[a]) & set(domains_data[b])):
                    va = domains_data[a][lang].get(key)
                    vb = domains_data[b][lang].get(key)
                    if va != vb:
                        fail("%s / %s: %s and %s disagree (%r vs %r); the copies are meant to be identical"
                             % (lang, key, a, b, va, vb))
    print("%-14s %d key(s) shipped by more than one domain, values agree" % ("cross-domain", shared))


# ── check 10: keys an asset points at ─────────────────────────────────────────────────────────────────────

def collect_asset_lang_refs():
    """Every lang key an asset names, as {reference: {source file, ...}}. Two shapes, both of which the engine
    resolves through Lang and neither of which logs a miss: the handbook extraSections of a blocktype/itemtype,
    and the title/text of a standalone handbook page under config/handbook."""
    refs = {}

    def note(ref, path):
        if isinstance(ref, str) and ref:
            refs.setdefault(ref, set()).add(os.path.relpath(path, ROOT).replace("\\", "/"))

    def walk_sections(node, path):
        if isinstance(node, dict):
            sections = node.get("handbook", {})
            if isinstance(sections, dict):
                for section in sections.get("extraSections") or []:
                    if isinstance(section, dict):
                        for field in ("title", "text"):
                            note(section.get(field), path)
            for value in node.values():
                walk_sections(value, path)
        elif isinstance(node, list):
            for value in node:
                walk_sections(value, path)

    def parsed(path):
        try:
            return json.loads(normalize_vs_json(open(path, encoding="utf-8-sig").read()))
        except Exception as exc:  # noqa: BLE001
            fail("%s: does not parse: %s" % (os.path.relpath(path, ROOT), exc))
            return None

    for folder in ("blocktypes", "itemtypes"):
        for dirpath, _dirnames, filenames in os.walk(os.path.join(ASSETS, folder)):
            for name in filenames:
                if name.endswith(".json"):
                    path = os.path.join(dirpath, name)
                    doc = parsed(path)
                    if doc is not None:
                        walk_sections(doc, path)

    # A handbook page of the mod's own -- the guide the handbook lists beside vanilla's. Its title and text are
    # lang keys like any other, and were the ones that sat untranslated with nothing looking at them.
    pagedir = os.path.join(ASSETS, "config", "handbook")
    if os.path.isdir(pagedir):
        for name in sorted(os.listdir(pagedir)):
            if not name.endswith(".json"):
                continue
            path = os.path.join(pagedir, name)
            doc = parsed(path)
            if isinstance(doc, dict):
                for field in ("title", "text"):
                    note(doc.get(field), path)

    return refs


# ── check 11: keys the C# points at ───────────────────────────────────────────────────────────────────────

def arg_span(text, open_paren):
    """The index just past the ')' that closes the '(' at open_paren."""
    depth = 0
    i = open_paren
    while i < len(text):
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return len(text)


def strip_cs_noise(text):
    """Whole-line comments out, TesselateShape argument lists blanked. Both hold strings that read exactly like
    lang keys and are not: a doc comment quoting a key by way of example, and the label TesselateShape prints
    when a shape fails to load."""
    lines = [("" if line.lstrip().startswith(("//", "*")) else line) for line in text.split("\n")]
    text = "\n".join(lines)

    out = []
    i = 0
    for m in TESSELATE_RE.finditer(text):
        if m.start() < i:
            continue
        end = arg_span(text, m.end() - 1)
        out.append(text[i:m.end()])
        out.append(" " * (end - m.end()))
        i = end
    out.append(text[i:])
    return "".join(out)


def collect_cs_lang_refs(known_domains):
    """Every lang key the C# names, as {reference: {source file, ...}}, plus the count of Lang.* call sites whose
    key is a variable and therefore unreadable from here.

    Every domain-prefixed literal counts, wherever it sits: the engine resolves keys the code never passes to
    Lang at all (a WorldInteraction's ActionLangCode, an ExtraHandbookSection's Text), and those were the ones
    nothing had ever looked at. The Lang.* walk that remains only COUNTS the unreadable call sites, so the
    number of keys this check cannot see is printed instead of assumed to be zero.
    """
    refs = {}
    dynamic = 0

    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = [d for d in dirnames if d not in CS_SKIP_DIRS and not d.startswith(".")]
        for name in sorted(filenames):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, ROOT).replace("\\", "/")
            # `Domain + ":key"` is the same literal written with the const; fold it before matching so one
            # regex sees both spellings.
            text = strip_cs_noise(DOMAIN_CONCAT_RE.sub('"' + DOMAIN + ":", open(path, encoding="utf-8-sig").read()))

            for m in LITERAL_ARG_RE.finditer(text):
                if m.group(1) in known_domains:
                    refs.setdefault(m.group(0)[1:-1], set()).add(rel)

            for m in LANG_CALL_RE.finditer(text):
                span = text[m.end():arg_span(text, m.end() - 1)]
                if not LITERAL_ARG_RE.search(span):
                    dynamic += 1                        # Lang.Get(someVariable) -- nothing here can resolve it

    return refs, dynamic


def check_refs(label, refs, domains_data):
    """Every collected reference resolves to a key present and non-empty in every file of its domain. A
    reference whose key is only a PREFIX (the C# appends a species, an ability name) has to be named in
    RUNTIME_KEY_FAMILIES: it cannot be resolved, and an unlisted one is a blind spot nobody wrote down."""
    resolved = 0
    families = set()
    for ref in sorted(refs):
        domain, _, key = ref.partition(":")
        if ref in NOT_LANG_KEYS:
            continue
        if not key or key.endswith("-") or "{" in key:
            family = domain + ":" + key
            if family not in RUNTIME_KEY_FAMILIES:
                fail("%s: %r is assembled at runtime and is not in RUNTIME_KEY_FAMILIES; add it with a note "
                     "on where the rest of the key comes from (%s)"
                     % (label, family, ", ".join(sorted(refs[ref]))))
            families.add(family)
            continue
        if domain not in domains_data:
            print("%-14s note: %s points outside the mod's domains: %s" % (label, label, ref))
            continue
        resolved += 1
        for lang, d in domains_data[domain].items():
            if key not in d:
                fail("%s: key %r missing from %s/%s.json (named in %s)"
                     % (label, key, domain, lang, ", ".join(sorted(refs[ref]))))
            elif not str(d[key]).strip():
                fail("%s: key %r is empty in %s/%s.json" % (label, key, domain, lang))
    return resolved, families


def main():
    domains = discover_domains()
    if DOMAIN not in domains:
        print("FATAL: no lang folder for the mod's own domain (%s)" % DOMAIN)
        return 1
    for lang in LANGS:
        if lang not in domains[DOMAIN]:
            fail("%s ships no %s.json; the five files are meant to mirror each other" % (DOMAIN, lang))

    domains_data = OrderedDict()
    for domain in sorted(domains, key=lambda d: (d != DOMAIN, d)):
        data = load_domain(domain, domains[domain])
        if REF not in data:
            fail("%s: no usable %s.json to compare the other languages against" % (domain, REF))
            continue
        domains_data[domain] = data

    if DOMAIN not in domains_data:
        print("FATAL: %s/%s.json unusable" % (DOMAIN, REF))
        return 1

    skipped_total = 0
    for domain, data in domains_data.items():
        ref_keys = check_key_mirror(domain, data)
        hrefs, skipped = check_values(domain, data)
        skipped_total += skipped
        check_cross_language(domain, data, ref_keys)
        if hrefs:
            print("%-14s %d distinct hrefs, target sets agree across all languages" % ("", len(hrefs)))
    if skipped_total:
        print("%-14s %d metadata key(s) (leading underscore) skipped by the prose checks"
              % ("", skipped_total))

    check_untranslated(domains_data, load_untranslated_allowlist())
    check_cross_domain(domains_data)

    asset_refs = collect_asset_lang_refs()
    resolved, _ = check_refs("assets", asset_refs, domains_data)
    print("%-14s %d lang key(s) referenced by assets, all present" % ("assets", resolved))

    cs_refs, cs_dynamic = collect_cs_lang_refs(set(domains_data))
    resolved, families = check_refs("code", cs_refs, domains_data)
    print("%-14s %d lang key(s) named by C#, all present; %d key famil(y/ies) assembled at runtime, "
          "listed by hand; %d Lang.* call site(s) take their key from a variable"
          % ("code", resolved, len(families), cs_dynamic))

    print("=" * 60)
    if failures:
        print("FAILURES (%d):" % len(failures))
        for f in failures:
            print("  X " + f)
        return 1
    print("all checks green")
    return 0


if __name__ == "__main__":
    sys.exit(main())
