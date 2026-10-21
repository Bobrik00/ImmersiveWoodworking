"""HalfLogBlockPlan T02.01 + T02.02 — generate the half-log slab's shape in all 24 states.

    python tools/gen-halflogslab-shape.py

Writes:
    art/shapes/halflogslab.json                                                  base state, source of record
    assets/immersivewoodworking/shapes/block/halflogslab/{rot}-{run}-{face}.json  24 baked states
and prints the collision/selection box table T03.01 needs.

GEOMETRY — a literal half block (user decision, 2026-07-31)
ONE rectangular element, 16 x 8 x 16, filling exactly half the cell: 6 quads, 12 triangles, nothing more.
No rounded profile, no stepped bands. The base state is rot=down, run=we, face=split — the block sits in
the lower half, its split face is the top, and the log runs east-west.

Because the geometry is a plain box, `run` and `face` no longer change the SHAPE, only which face carries
which texture: `run` decides which pair of faces are the log's ends (tree rings), `face` decides whether
the split plane or the bark looks outward. `rot` is the only part that moves geometry, so there are 6
distinct boxes and 24 texture assignments over them. They are still baked one file per state so the block
can address them with a single placeholder path.

WHY A GENERATOR INSTEAD OF A .blend (T02.01 deviation)
The shape has to hit exact numbers — exactly half a cell, faces flush with the cell walls so a row of
blocks has no seams — and the same 24 states have to stay in step with the collision boxes and the
skipVariants list. All three come out of the same few lines here, so they cannot drift apart. Re-run this
script after changing anything; never hand-edit the generated JSON.

WHY 24 BAKED FILES INSTEAD OF rotateX/Y/Z (T02.02 deviation)
Block-shape rotation goes through Mat4f.RotateXYZ (M = Rx*Ry*Rz, so Z is applied to the vertex first), but
the sign convention the block tessellator feeds it cannot be confirmed from the sources shipped here — it
would have to be guessed and then debugged by eye across 24 states. Baked coordinates have no convention
to get wrong. It is also what vanilla does: block/basic/slab/slab-{rot}.json, one file per state.

STATE CONSTRUCTION
  rot  = which half of the cell is solid; `outward` = the direction away from the supporting face
         (down -> +Y, up -> -Y, north -> +Z, south -> -Z, east -> -X, west -> +X;
          matches vanilla slab-{rot}.json, verified: slab-north spans z 0..8)
  run  = the log axis (we -> X, ns -> Z, ud -> Y); the 12 combinations with the axis parallel to `outward`
         are geometrically impossible and are cut by skipVariants in the blocktype
  face = which surface looks outward: `split` (the flat plane) or `bark`

The flat face's normal f is +outward for `split` and -outward for `bark`. The transform is the signed
permutation with columns [axis, f, axis x f] (right-handed by construction, so always a proper rotation),
applied about the cell centre. For `bark` that lands the solid in the WRONG half — the shape is only half a
cell, so the two faces of one rot differ by a turn about the half's own centre, not the cell's — and a
translation of 8 along -outward puts it back. Textures follow the rotation; UVs are recomputed from the
transformed box, never carried over.

TEXTURE KEYS follow LogTexSource's contract, so one geometry serves every species:
    #wood          — bark: the two long sides and the back
    #debarked_wood — the split face
    #wood_corner   — end grain: the two faces at the log's ends

TEXTURE ORIENTATION (T02.04, added after two confirmed defects in the first cut)
The first version emitted one unrotated texture for all 24 states, which is wrong twice over.

(1) GRAIN. bark/{wood} and debarked/{wood} are DIRECTIONAL, and they are 90 degrees out of phase with
    each other: bark runs its grain down v, debarked runs it along u. A face's u and v carry different
    world axes depending on which way it points, so one unrotated texture cannot be right for more than
    one `run`. 48 of the 96 grain-bearing faces were wrong, including down-we-split, the state the
    creative tab and the handbook show. Fixed by turning a face whenever the log's axis does not already
    sit under that texture's native grain axis — see build_state and rot90_uv.

    Vanilla solves the same problem by SWITCHING ASSET, bark/{wood} <-> bark/{wood}-h. That route is
    closed to us: debarked/ has no -h files at all (19 textures, zero siblings), and a wood mod may ship
    bark/{wood} without an -h either, so any -h dependency is a latent 404 for third-party species. Face
    rotation needs only the base asset, which is what indappledgroves' logslab does.

(2) END-GRAIN WINDOW. treetrunk/{wood} is a WHOLE round section — rim on all four edges, pith at the
    centre — and a half log shows half of it. The half has to be chosen so the pith lands on the sawn
    plane and the rim on the bark side. uv_for derives the rect from the box, and `bark` differs from
    `split` only by a shift that leaves the box identical, so both got the same rect and one of the two
    was necessarily inverted. It was `bark`: its split plane sits on a cell BOUNDARY, which the formula
    maps to texture 0 or 16 (the rim), putting the pith out on the bark side. Fixed by mirror_uv.

Both fixes are computed from the same signed permutation the geometry already uses; there is no table.
Verification anchor: running this rule on state `down-ns-split` reproduces indappledgroves'
shipped shapes/block/wood/logslab-down.json on all six faces — texture key, uv and rotation alike.
"""
import json, os, re

CENTRE = (8.0, 8.0, 8.0)

TEX_SIDE, TEX_SPLIT, TEX_END = "#wood", "#debarked_wood", "#wood_corner"

EAST, WEST = (1, 0, 0), (-1, 0, 0)
UP, DOWN = (0, 1, 0), (0, -1, 0)
SOUTH, NORTH = (0, 0, 1), (0, 0, -1)

OUTWARD = {"down": UP, "up": DOWN, "north": SOUTH, "south": NORTH, "east": WEST, "west": EAST}
AXIS = {"we": EAST, "ns": SOUTH, "ud": UP}
FACE_NAME = {NORTH: "north", SOUTH: "south", EAST: "east", WEST: "west", UP: "up", DOWN: "down"}
ROTS = ["north", "east", "south", "west", "up", "down"]
RUNS = ["ns", "we", "ud"]
FACES = ["bark", "split"]


def cross(u, v):
    return (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])


def neg(u):
    return (-u[0], -u[1], -u[2])


def base_box():
    """The base state: the lower half of the cell, split face up, log running east-west."""
    tex = {
        UP: TEX_SPLIT,                  # the split plane
        DOWN: TEX_SIDE,                 # bark, against the support
        NORTH: TEX_SIDE, SOUTH: TEX_SIDE,   # bark, the log's long sides
        EAST: TEX_END, WEST: TEX_END,       # tree rings, the log's ends
    }
    return (0.0, 0.0, 0.0), (16.0, 8.0, 16.0), tex, "halflogslab"


def rotation(rot, run, face):
    """Signed-permutation rotation (as 3 columns) plus the translation that keeps the solid in its half."""
    o = OUTWARD[rot]
    a = AXIS[run]
    f = o if face == "split" else neg(o)
    mat = (a, f, cross(a, f))
    shift = (0.0, 0.0, 0.0) if face == "split" else tuple(-8.0 * c for c in o)
    return mat, shift


def apply(mat, shift, p):
    colx, coly, colz = mat
    d = [p[i] - CENTRE[i] for i in range(3)]
    return tuple(CENTRE[i] + colx[i] * d[0] + coly[i] * d[1] + colz[i] * d[2] + shift[i] for i in range(3))


def rotate_dir(mat, d):
    colx, coly, colz = mat
    return tuple(colx[i] * d[0] + coly[i] * d[1] + colz[i] * d[2] for i in range(3))


def uv_for(direction, lo, hi):
    """Standard box UV layout in 16x16 texture space, recomputed from the transformed box."""
    x0, y0, z0 = lo
    x1, y1, z1 = hi
    if direction == NORTH:
        return [16 - x1, 16 - y1, 16 - x0, 16 - y0]
    if direction == SOUTH:
        return [x0, 16 - y1, x1, 16 - y0]
    if direction == EAST:
        return [16 - z1, 16 - y1, 16 - z0, 16 - y0]
    if direction == WEST:
        return [z0, 16 - y1, z1, 16 - y0]
    if direction == UP:
        return [x0, z0, x1, z1]
    return [x0, 16 - z1, x1, 16 - z0]


def axis_of(d):
    """The axis letter of a unit direction: (0, 0, -1) -> 'z'."""
    return "xyz"[[abs(c) for c in d].index(1)]


def face_uv_axes(direction):
    """Which WORLD axis the engine's u and v carry on a given face. Read off uv_for's own branches:
    u is world X on north/south/up/down and world Z on east/west; v is world Y on the four side faces
    and world Z on up/down. This is the only piece of engine convention the orientation rule needs."""
    n = axis_of(direction)
    return ("z" if n == "x" else "x"), ("z" if n == "y" else "y")


def rot90_uv(uv):
    """The rect that, read through `"rotation": 90`, samples the same texels as `uv` read plainly.

    A face rotation permutes which geometry corner maps to which CORNER of the rect; it does NOT swap
    the rect's own extents. So a 16x8 face carrying rotation 90 has to be handed a transposed 8x16
    rect, or the image comes out squashed 2:1 and off the wrong half of the texture. On the square
    faces this is the identity, because uv_for's rects always run ascending in both components.

    The SENSE is fixed empirically, not derived. vsapi declares ShapeElementFace.Rotation as a bare
    float (ShapeElementFace.cs:26) and ships no consumer of it — the tessellator that turns degrees
    into a corner shift lives in the closed lib — so the convention is not knowable from the sources
    here. Anchor is indappledgroves' logslab, the same block solving the same problem:
        logslab-down (box y 0..8)   east/west  uv [0, 0, 8, 16]   rotation 90
        logslab-up   (box y 8..16)  east/west  uv [8, 0, 16, 16]  rotation 90
    Feeding uv_for's plain rects for those two boxes through this transform reproduces both exactly.
    The conjugate map [v0, 16-u1, v1, 16-u0] reproduces their COMPLEMENTS, and that is the tell: the
    two halves must sample ADJACENT halves of the bark image so a down half stacked under an up half
    reads as one log. Do not "simplify" this line without re-checking it against those two files.
    """
    u0, v0, u1, v1 = uv
    return [16 - v1, u0, 16 - v0, u1]


def mirror_uv(uv, comp):
    """Reverse one component's endpoints. Descending rects are legal and mirror the image (vanilla
    ships 670 of them with u0 > u1, including block/basic/slab/slab-down.json's up face)."""
    u0, v0, u1, v1 = uv
    return [u1, v0, u0, v1] if comp == "u" else [u0, v1, u1, v0]


def build_state(rot, run, face):
    mat, shift = rotation(rot, run, face)
    inverse = {rotate_dir(mat, d): d for d in (NORTH, SOUTH, EAST, WEST, UP, DOWN)}
    frm, to, tex, name = base_box()
    a, b = apply(mat, shift, frm), apply(mat, shift, to)
    lo = tuple(round(min(a[i], b[i]), 4) for i in range(3))
    hi = tuple(round(max(a[i], b[i]), 4) for i in range(3))

    log_axis = axis_of(AXIS[run])         # the direction the grain has to run
    split_axis = axis_of(OUTWARD[rot])    # the normal of the split plane

    faces = {}
    for d in (NORTH, EAST, SOUTH, WEST, UP, DOWN):
        key = tex[inverse[d]]
        _, vax = face_uv_axes(d)
        uv = uv_for(d, lo, hi)
        entry = {"texture": key, "uv": uv}
        if key == TEX_END:
            # Rings, not grain: nothing to align, but the half of the round section that shows has to
            # be the right one. `split` is already right — its plane is the cell midplane and uv_for
            # maps 8 to texture 8, the pith. `bark` puts its plane on a cell boundary, which lands the
            # rim there and the pith outside, so mirror the component carrying the split-plane normal.
            if face == "bark":
                entry["uv"] = mirror_uv(uv, "v" if split_axis == vax else "u")
        else:
            # Turn the face whenever the log's axis is not already under the texture's native grain
            # axis. Never emit anything but 90: vanilla's 83k rotation values contain no negatives.
            native_v = key == TEX_SIDE    # bark runs its grain down v, debarked along u
            if (log_axis == vax) != native_v:
                entry["uv"] = rot90_uv(uv)
                entry["rotation"] = 90
        faces[FACE_NAME[d]] = entry
    return {
        "textureWidth": 16,
        "textureHeight": 16,
        # Fully qualified on purpose: a BARE texture name here 404s at asset load. The block overrides these
        # per species at runtime (T03.02); these defaults are what makes the shape viewable standalone.
        "textures": {
            "wood": "game:block/wood/bark/oak",
            "debarked_wood": "game:block/wood/debarked/oak",
            "wood_corner": "game:block/wood/treetrunk/oak",
        },
        "elements": [{"name": name, "from": list(lo), "to": list(hi), "faces": faces}],
    }


# ── FACE TEXTURE CODES FOR THE CHISEL (AuditFixPlan T04.02) ─────────────────────────────────────────
#
# The block's own `textures` keys are the SHAPE's material names (#wood, #debarked_wood, #wood_corner),
# and the chisel cannot read those. BlockEntityMicroBlock.VoxelMaterial.FromBlock asks the block for six
# textures by WORLD FACE CODE — texSource["north"], ["up"] and so on — and hands every face that does not
# resolve the same fallback, block.Textures.First(). Undeclared, that is the bark slot, so a hewn half-log
# came out bark on all six sides: no rings on the ends, no split plane.
#
# So the blocktype has to declare those six codes as well, and WHICH family each one carries is exactly
# what build_state already decides per state. Deriving the rules here rather than writing them into the
# asset by hand is the same discipline the shapes themselves follow: one rule, one place, no drift.
#
# THE RULE, read straight off base_box + rotation():
#   * the two faces along the log's axis (`run`) are the sawn ends       -> treetrunk
#   * the face whose outward normal is f is the split plane              -> debarked
#     (f = OUTWARD[rot] on `split`, its opposite on `bark`, which makes the split-plane rot the OPPOSITE
#      of the face for `split` and the face ITSELF for `bark`)
#   * the remaining three faces are the log's round side                 -> bark, or debarked when stripped
#
# GLOB FORM. Keys are matched by WildcardUtil against the WHOLE code halflogslab-{wood}-{skin}-{rot}-{run}-{face},
# `*` spans dashes, and the first match wins, so: every `debarked` rule goes FIRST (a leading `*` would
# otherwise let a barked rule swallow it, and `*-barked-*` would match `-debarked-` too), the catch-all `*`
# goes LAST, and every key carries one `-*` per variant part that follows the one it keys on.
# Only TWO debarked rules are needed against four barked ones, because on a stripped half-log the round side
# and the split plane are the same texture.

RUN_OF_AXIS = {"x": "we", "y": "ud", "z": "ns"}

BARK_TEX = "game:block/wood/bark/{wood}"
DEBARKED_TEX = "game:block/wood/debarked/{wood}"
END_TEX = "game:block/wood/treetrunk/{wood}"
END_DEBARKED_TEX = "game:block/wood/treetrunk/debarked/{wood}"

# shape material key + skin -> the texture that material is made of. The single table both the rule
# emitter and the verifier below read, so they cannot disagree about what `debarked` means.
FAMILY = {
    ("barked", TEX_SIDE): BARK_TEX, ("barked", TEX_SPLIT): DEBARKED_TEX, ("barked", TEX_END): END_TEX,
    ("debarked", TEX_SIDE): DEBARKED_TEX, ("debarked", TEX_SPLIT): DEBARKED_TEX, ("debarked", TEX_END): END_DEBARKED_TEX,
}


def face_rules(direction):
    """The ordered (glob, texture) rules for ONE face code, as the blocktype's `<face>ByType` object."""
    run_key = RUN_OF_AXIS[axis_of(direction)]
    rot_split = FACE_NAME[neg(direction)]   # OUTWARD[rot] == direction  =>  rot is the opposite face
    rot_bark = FACE_NAME[direction]         # f == -OUTWARD[rot] == direction  =>  rot IS this face
    return [
        ("halflogslab-*-debarked-*-%s-*" % run_key, END_DEBARKED_TEX),
        ("halflogslab-*-debarked-*", DEBARKED_TEX),
        ("*-%s-*" % run_key, END_TEX),
        ("*-%s-*-split" % rot_split, DEBARKED_TEX),
        ("*-%s-*-bark" % rot_bark, DEBARKED_TEX),
        ("*", BARK_TEX),
    ]


def glob_match(pattern, code):
    """WildcardUtil.Match as the engine does it: `*` spans anything including dashes, whole string, no case."""
    return re.fullmatch("".join(".*" if c == "*" else re.escape(c) for c in pattern), code, re.IGNORECASE) is not None


def verify_face_rules():
    """Run the emitted rules over all 48 states and demand they reproduce build_state's own face textures.

    This is the whole reason the rules live here: the asset says one thing, gen says another, and only a
    machine comparing the two can notice they drifted. BlockHalfLogSlab.SelfCheck makes the same comparison
    at runtime against the LOADED block, which catches the third party — a patch from another mod.
    """
    rules = {FACE_NAME[d]: face_rules(d) for d in (NORTH, EAST, SOUTH, WEST, UP, DOWN)}
    bad = []
    for skin in ("barked", "debarked"):
        for rot in ROTS:
            for run in RUNS:
                if not valid(rot, run):
                    continue
                for face in FACES:
                    state = build_state(rot, run, face)
                    code = "halflogslab-oak-%s-%s-%s-%s" % (skin, rot, run, face)
                    for name, entry in state["elements"][0]["faces"].items():
                        want = FAMILY[(skin, "#" + entry["texture"].lstrip("#"))]
                        got = next(tex for glob, tex in rules[name] if glob_match(glob, code))
                        if got != want:
                            bad.append("%s face %s: rules say %s, shape says %s" % (code, name, got, want))
    return bad


def print_face_rules():
    """The six `<face>ByType` objects, tab-indented for pasting into blocktypes/halflogslab.json."""
    for d in (NORTH, EAST, SOUTH, WEST, UP, DOWN):
        rules = face_rules(d)
        print('		"%sByType": {' % FACE_NAME[d])
        for i, (glob, tex) in enumerate(rules):
            print('			"%s": { "base": "%s" }%s' % (glob, tex, "" if i == len(rules) - 1 else ","))
        print("		},")


def valid(rot, run):
    """A log cannot run along the direction it is pressed against."""
    o, a = OUTWARD[rot], AXIS[run]
    return abs(o[0] * a[0] + o[1] * a[1] + o[2] * a[2]) < 0.5


def collision_box(rot):
    """The occupied half of the cell, in block units — the same for all runs and faces of a rot."""
    box = {"x1": 0.0, "y1": 0.0, "z1": 0.0, "x2": 1.0, "y2": 1.0, "z2": 1.0}
    o = OUTWARD[rot]
    if o == UP:      box["y2"] = 0.5
    elif o == DOWN:  box["y1"] = 0.5
    elif o == SOUTH: box["z2"] = 0.5
    elif o == NORTH: box["z1"] = 0.5
    elif o == WEST:  box["x1"] = 0.5
    elif o == EAST:  box["x2"] = 0.5
    return box


def write(path, data):
    """Indented JSON, but with the numeric arrays (from / to / uv) kept on one line like vanilla shapes."""
    text = json.dumps(data, indent="\t")
    text = re.sub(r"\[\s*([-\d.,\s]+?)\s*\]",
                  lambda m: "[" + ", ".join(part.strip() for part in m.group(1).split(",")) + "]",
                  text)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text + "\n")


if __name__ == "__main__":
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    shapedir = os.path.join(here, "assets", "immersivewoodworking", "shapes", "block", "halflogslab")

    write(os.path.join(here, "art", "shapes", "halflogslab.json"),
          build_state("down", "we", "split"))

    n = 0
    for rot in ROTS:
        for run in RUNS:
            if not valid(rot, run):
                continue
            for face in FACES:
                write(os.path.join(shapedir, "%s-%s-%s.json" % (rot, run, face)), build_state(rot, run, face))
                n += 1
    print("wrote %d state shapes to %s" % (n, shapedir))

    frm, to, _, _ = base_box()
    print("\nbase state (rot=down, run=we, face=split): one box %s .. %s  (%g x %g x %g)"
          % (list(frm), list(to), to[0] - frm[0], to[1] - frm[1], to[2] - frm[2]))

    bad = verify_face_rules()
    print("\nface texture codes for the chisel (T04.02), 36 rules over six face codes: %s"
          % ("MISMATCH:\n  " + "\n  ".join(bad) if bad else "all 48 states agree with the shapes"))
    print_face_rules()

    print("\nskipVariants (axis parallel to the supporting face's normal):")
    print("  " + ", ".join('"*-%s-%s-*"' % (rot, run) for rot in ROTS for run in RUNS if not valid(rot, run)))

    print("\ncollision / selection box per rot (block units), for T03.01:")
    for rot in ROTS:
        b = collision_box(rot)
        print("  *-%-5s-*-*  x1:%.1f y1:%.1f z1:%.1f x2:%.1f y2:%.1f z2:%.1f"
              % (rot, b["x1"], b["y1"], b["z1"], b["x2"], b["y2"], b["z2"]))
