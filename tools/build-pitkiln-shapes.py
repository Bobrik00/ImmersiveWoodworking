#!/usr/bin/env python3
# =====================================================================================================================
# PIT-KILN SHAPES — build the shipped mod copies from the authored Blender exports (BarkPlan/E06, T06.05).
#
# WHY A TOOL AND NOT A PLAIN FILE COPY
#   The authored exports in art/ carry Blender's duplicate suffixes on the TOP-LEVEL element names
#   ("Grass1.001", "Sticklayer1.003"). The pit kiln selects the geometry of each build stage BY ELEMENT NAME
#   (game:blocktypes/wood/pitkiln.json -> attributes/modelConfigs/<cfg>/buildStages, resolved in
#   BEPitKiln.updateSelectiveElements), and a plain stage name like "Grass1" is an EXACT match — "Grass1.001"
#   would draw NOTHING. So the suffixes are stripped here, on the shipped copy only; the .blend stays untouched.
#   Re-export from Blender -> re-run this script, or the grass/log/coal stages silently go invisible.
#
#   The exports also carry the Blender image names in their "textures" block ("oak", "birch", ...), which are not
#   asset paths and 404 at asset load (memory vs-blender-shape-texture-block-warning). They are rewritten to the
#   pit-kiln block's own texture paths. At render time the codes resolve against the BLOCK's texture dict anyway
#   (BEPitKiln.OnTesselation takes tesselator.GetTextureSource(Block)), so this only silences the load noise and
#   keeps the file self-consistent for the shape editor.
#
# THE ANVIL (toolmold-*-raw-anvil) has no authored bark: the user did not clad it in Blender and declared it
#   identical to the default kiln. Verified against the vanilla shapes 2026-07-29: anvil and default agree on every
#   top-level element except Grass5 (inflated by 0.05 on the anvil). So the anvil copy = the AUTHORED-EXPORT-FREE
#   vanilla anvil geometry + the bark groups lifted from the default export.
#
#   UV SPACE IS THE TRAP HERE (this is the bug that broke the bark textures in the first shipped attempt): a shape's
#   UVs are numbers in the space declared by its own textureWidth/textureHeight/textureSizes (vsapi Shape.cs:43-52),
#   NOT texture pixels. The authored exports declare 64; the vanilla shapes declare 16. Moving bark elements from a
#   64-space shape into a 16-space shape WITHOUT rescaling makes every bark UV 4x too large -> the bark samples a 4x
#   patch of the atlas -> smeared garbage. The graft below therefore multiplies every bark UV by 16/64.
#
# OUT: assets/immersivewoodworking/shapes/block/pitkiln/{default,vessel,anvil}.json
# RUN: python tools/build-pitkiln-shapes.py        (from the mod root; no args)
# =====================================================================================================================

import json
import os
import re
import sys

MOD_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_DIR = os.path.abspath(os.path.join(MOD_ROOT, "art", "shapes", "pitkiln"))
OUT_DIR = os.path.join(MOD_ROOT, "assets", "immersivewoodworking", "shapes", "block", "pitkiln")

# The pit-kiln block's own texture codes (game:blocktypes/wood/pitkiln.json -> textures), plus the TWO bark codes
# that patches/pitkiln_bark_model.json adds to the block.
#
# THE BARK GETS ITS OWN CODES ON PURPOSE. The bark was authored in the same Blender project as the bark ITEM, against
# the same two images — and the item shape resolves them (shapes/item/bark/bark-generic.json) as
# oak -> game:block/wood/oak and oak-bark_dark -> game:block/wood/oak-dark. Both are VERTICAL-grain wood, which is what
# a leaning bark strip needs. The kiln BLOCK however already owns the code `oak` and points it at
# block/wood/debarked/oak, and the obvious-looking block/wood/firewood/bark is a HORIZONTAL-grain texture: either one
# lands the grain across the strip instead of along it (user: «или повернуть кору на 90 градусов, или использовать ту
# же текстуру что и на обычном предмете коры»). Renaming the bark's #oak to #oak-bark_light keeps the vanilla sticks
# on debarked/oak and gives the bark the item's texture, with the authored UVs unrotated.
#
# Every path is `game:`-QUALIFIED. The vanilla shapes get away with bare paths because they live in the game domain;
# a copy under assets/immersivewoodworking/ does not — an unqualified path there resolves in the MOD domain
# (memory vs-mod-asset-path-domain) and 404s at load. Same rule the bark ITEM shapes already follow.
TEXTURES = {
    "side":           "game:block/wood/firewood/side",
    "south":          "game:block/wood/firewood/south",
    "north":          "game:block/wood/firewood/north",
    "bottom":         "game:block/hay/bed/bottom",
    "bark":           "game:block/wood/firewood/bark",
    "oak":            "game:block/wood/debarked/oak",
    "oak1":           "game:block/wood/planks/oak1",
    "coal":           "game:block/coal/charcoal",
    "wood":           "game:block/wood/oak-dark",
    "oak-bark_dark":  "game:block/wood/oak-dark",
    "oak-bark_light": "game:block/wood/oak",
}

# On BARK elements only: the authored #oak means the bark item's oak, not the kiln block's debarked oak.
BARK_TEXTURE_REMAP = {"#oak": "#oak-bark_light"}

# Element names each modelConfig's buildStages must be able to select. Stick stages are patched to the bare-star
# prefix form "{eleCode}Sticklayer1*" (pitkiln_bark_model.json), every other stage is an exact name.
REQUIRED = {
    "default": ["Grass1", "Grass2", "Grass3", "Grass4", "Grass5", "Sticklayer1", "Sticklayer2",
                "Logs1", "Logs2", "Logs3", "Logs4", "Coal1", "Coal2", "Coal3", "Coal4",
                "Coal5", "Coal6", "Coal7", "Coal8"],
    "anvil":   ["Grass1", "Grass2", "Grass3", "Grass4", "Grass5", "Sticklayer1", "Sticklayer2",
                "Logs1", "Logs2", "Logs3", "Logs4", "Coal1", "Coal2", "Coal3", "Coal4",
                "Coal5", "Coal6", "Coal7", "Coal8"],
    "vessel":  ["Grass1", "Grass2", "Grass3", "Grass4", "Grass5", "Sticklayer1", "Sticklayer2",
                "Logs1", "Logs2", "Logs3", "Logs4", "Logs5", "Logs6", "Logs7", "Logs8",
                "Coal1", "Coal2", "Coal3", "Coal4", "Coal5", "Coal6", "Coal7", "Coal8"],
}

SUFFIX = re.compile(r"\.\d+$")
NUMLIST = re.compile(r"\[\s*\n\s*(-?[\d.eE+-]+(?:,\s*\n\s*-?[\d.eE+-]+)*)\s*\n\s*\]")


def load(path):
    with open(path, encoding="utf-8-sig") as fh:
        return json.load(fh)


def is_bark(el):
    return (el.get("name") or "").startswith("BarkSticklayer")


def scale_uv(elements, factor):
    for el in elements:
        for face in (el.get("faces") or {}).values():
            if "uv" in face:
                # NO rounding: 16/64 is a power of two, so the multiply is exact and the normalised UV is
                # bit-identical to the source. Rounding here would shift the bark by a fraction of a texel.
                face["uv"] = [v * factor for v in face["uv"]]
        scale_uv(el.get("children") or [], factor)


def remap_bark_textures(elements):
    n = 0
    for el in elements:
        for face in (el.get("faces") or {}).values():
            new = BARK_TEXTURE_REMAP.get(face.get("texture"))
            if new:
                face["texture"] = new
                n += 1
        n += remap_bark_textures(el.get("children") or [])
    return n


def strip_suffixes(elements):
    """Blender's .00N suffix off the TOP-LEVEL names only — children are never selected by name."""
    renamed = []
    for el in elements:
        name = el.get("name") or ""
        short = SUFFIX.sub("", name)
        if short != name:
            el["name"] = short
            renamed.append((name, short))
    return renamed


def dump(obj, path):
    text = json.dumps(obj, indent=1, ensure_ascii=False)
    # collapse from/to/uv/rotationOrigin number arrays back onto one line — indent=1 explodes them to 3-4 lines each
    prev = None
    while prev != text:
        prev = text
        text = NUMLIST.sub(lambda m: "[" + ", ".join(p.strip() for p in m.group(1).split(",")) + "]", text)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text + "\n")
    return len(text)


def check(shape, cfg, name):
    top = [el.get("name") for el in shape["elements"]]
    dupes = {n for n in top if top.count(n) > 1}
    if dupes:
        sys.exit("FATAL %s: duplicate top-level element names after stripping: %s" % (name, sorted(dupes)))
    missing = [n for n in REQUIRED[cfg] if n not in top]
    if missing:
        sys.exit("FATAL %s: buildStages would not resolve, missing elements: %s" % (name, missing))
    for layer in ("BarkSticklayer1", "BarkSticklayer2"):
        n = len([t for t in top if t.startswith(layer)])
        if n == 0:
            sys.exit("FATAL %s: no %s* elements — a bark-clad kiln would draw an empty layer" % (name, layer))
        print("      %s*: %d elements" % (layer, n))
    codes = set()

    def walk(els):
        for el in els:
            for face in (el.get("faces") or {}).values():
                if face.get("texture"):
                    codes.add(face["texture"].lstrip("#"))
            walk(el.get("children") or [])
    walk(shape["elements"])
    unknown = sorted(c for c in codes if c not in TEXTURES)
    if unknown:
        sys.exit("FATAL %s: faces use texture codes the block does not define: %s" % (name, unknown))


def build(cfg):
    print("--- %s" % cfg)
    shape = load(os.path.join(SRC_DIR, "%s.json" % cfg))
    shape.pop("groups", None)  # Blender group tree; vsapi Shape has no such field, it is dead weight in the ship copy

    if cfg == "anvil":
        # vanilla anvil geometry (the authored export is a byte-identical copy) + bark lifted from the default export
        donor = load(os.path.join(SRC_DIR, "default.json"))
        bark = [el for el in donor["elements"] if is_bark(el)]
        factor = shape.get("textureWidth", 16) / donor.get("textureWidth", 16)
        scale_uv(bark, factor)
        shape["elements"].extend(bark)
        print("      grafted %d bark elements from default, UV x %s (%d-space -> %d-space)"
              % (len(bark), factor, donor.get("textureWidth", 16), shape.get("textureWidth", 16)))

    remapped = remap_bark_textures([el for el in shape["elements"] if is_bark(el)])
    print("      remapped %d bark faces %s" % (remapped, BARK_TEXTURE_REMAP))

    renamed = strip_suffixes(shape["elements"])
    print("      renamed %d top-level elements%s" % (len(renamed), (": " + ", ".join(a for a, _ in renamed)) if renamed else ""))
    shape["textures"] = dict(TEXTURES)
    if shape.get("textureSizes"):
        w, h = shape.get("textureWidth", 16), shape.get("textureHeight", 16)
        shape["textureSizes"] = {code: [w, h] for code in TEXTURES}

    check(shape, cfg, cfg)
    os.makedirs(OUT_DIR, exist_ok=True)
    size = dump(shape, os.path.join(OUT_DIR, "%s.json" % cfg))
    print("      -> %s (%d elements top-level, %.0f KB)"
          % (os.path.join("assets", "immersivewoodworking", "shapes", "block", "pitkiln", "%s.json" % cfg),
             len(shape["elements"]), size / 1024.0))


if __name__ == "__main__":
    print("src: %s" % SRC_DIR)
    for cfg in ("default", "vessel", "anvil"):
        build(cfg)
    print("done")
