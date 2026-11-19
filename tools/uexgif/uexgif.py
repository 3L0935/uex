#!/usr/bin/env python3
"""uexgif - turntable GIF renderer for uex (CUE4Parse) exports.

Takes the .psk / textures that `uex export` produced and renders the assembled
model rotating on itself as a GIF, with albedo textures, no GPU, no Blender:
pure numpy rasterizer (z-buffer, per-pixel affine UV, lambert + rim light).

Usage:
  python3 uexgif.py /path/to/export_dir [more dirs...] -o myers.gif
  python3 uexgif.py body/ tete/ masque/ suit/ -o myers.gif --frames 30 --size 460x620

Notes:
  * Every .psk found under the given dirs is loaded and assembled in its bind
    pose (that is the A-pose the mesh was cooked in), then rotated about Z (UE up).
  * Textures are matched from the material names (MI_Foo -> T_Foo_ALB.png),
    fuzzy fallback by token overlap.
"""
from __future__ import annotations

import argparse
import glob
import json
import math
import os
import re
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

# Windows: la console peut etre en cp1252, les accents dans les logs la font planter.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

__version__ = "1.0.1"

# Pillow a deplace ses constantes (LANCZOS -> Image.Resampling.LANCZOS,
# ADAPTIVE -> Image.Palette.ADAPTIVE): on accepte les deux generations.
_RESAMPLING = getattr(Image, "Resampling", Image)
_PALETTE = getattr(Image, "Palette", Image)
LANCZOS = getattr(_RESAMPLING, "LANCZOS", 1)
ADAPTIVE = getattr(_PALETTE, "ADAPTIVE", 1)

PSK_CHUNK = struct.Struct("<20siii")


# --------------------------------------------------------------------------- psk
def read_psk(path):
    d = open(path, "rb").read()
    off = 0
    chunks = {}
    while off + 32 <= len(d):
        raw, ctype, dsize, count = PSK_CHUNK.unpack_from(d, off)
        name = raw.split(b"\0")[0].decode("latin1", "replace").strip()
        data_off = off + 32
        if dsize > 0 and count > 0:
            chunks[name] = (data_off, dsize, count)
            off = data_off + dsize * count
        else:
            off = data_off
        if off >= len(d):
            break
    if "PNTS0000" not in chunks or "FACE0000" not in chunks or "VTXW0000" not in chunks:
        raise SystemExit(f"{path}: pas un .psk ActorX exploitable (chunks: {sorted(chunks)})")

    o, _, n = chunks["PNTS0000"]
    points = np.frombuffer(d, dtype="<f4", count=n * 3, offset=o).reshape(n, 3).astype(np.float64)

    # VTXW0000: 16 octets = index de point (16 bits non signe), 2 octets, u, v, mat, res, pad.
    # Les jeux > 32767 points/wedges ecrivent les index en non signe (pas en signe):
    # les lire en i16 produit des index negatifs et un maillage en charpie.
    o, _, n = chunks["VTXW0000"]
    w = np.frombuffer(d, dtype=np.dtype([("pt", "<u2"), ("u0", "<u2"), ("u", "<f4"), ("v", "<f4"),
                                         ("mat", "u1"), ("res", "u1"), ("pad", "<i2")]),
                       count=n, offset=o)
    wpt = w["pt"].astype(np.int32)
    wuv = np.stack([w["u"], w["v"]], axis=1).astype(np.float64)
    wmat = w["mat"].astype(np.int32)
    if wpt.max() >= len(points):                      # variante 32 bits
        w32 = np.frombuffer(d, dtype=np.dtype([("pt", "<i4"), ("u", "<f4"), ("v", "<f4"),
                                               ("mat", "u1"), ("res", "u1"), ("pad", "<i2")]),
                             count=n, offset=o)
        if w32["pt"].max() < len(points):
            wpt = w32["pt"].astype(np.int32)
            wuv = np.stack([w32["u"], w32["v"]], axis=1).astype(np.float64)
            wmat = w32["mat"].astype(np.int32)

    # FACE0000: 3 index de wedge (16 bits non signe), mat, aux, smoothing
    o, _, n = chunks["FACE0000"]
    f = np.frombuffer(d, dtype=np.dtype([("a", "<u2"), ("b", "<u2"), ("c", "<u2"),
                                         ("mat", "u1"), ("aux", "u1"), ("smooth", "<i4")]),
                      count=n, offset=o)
    faces = np.stack([f["a"], f["b"], f["c"]], axis=1).astype(np.int32)
    fmat = f["mat"].astype(np.int32)
    if faces.max() >= len(wpt):
        f32 = np.frombuffer(d, dtype=np.dtype([("a", "<i4"), ("b", "<i4"), ("c", "<i4")]),
                            count=n, offset=o)
        cand = np.stack([f32["a"], f32["b"], f32["c"]], axis=1).astype(np.int32)
        if cand.max() < len(wpt):
            faces = cand

    wnrm = None
    if "VTXNORMS" in chunks:
        o2, _, n2 = chunks["VTXNORMS"]
        wnrm = np.frombuffer(d, dtype="<f4", count=n2 * 3, offset=o2).reshape(n2, 3).astype(np.float64)
    elif "NORM0000" in chunks:
        o2, _, n2 = chunks["NORM0000"]
        wnrm = np.frombuffer(d, dtype="<f4", count=n2 * 3, offset=o2).reshape(n2, 3).astype(np.float64)

    mats = []
    if "MATT0000" in chunks:
        o, dsize, n = chunks["MATT0000"]
        for i in range(n):
            mats.append(d[o + i * dsize:o + i * dsize + 64].split(b"\0")[0].decode("latin1", "replace"))

    return {
        "path": path,
        "points": points,
        "wpt": wpt,
        "wuv": wuv,
        "wmat": wmat,
        "wnrm": wnrm,
        "faces": faces,
        "fmat": fmat,
        "mats": mats,
    }


# ------------------------------------------------------------------- textures
def norm_tokens(s):
    s = re.sub(r"\.(png|jpg|tga)$", "", s, flags=re.I)
    return [t for t in re.split(r"[_\W]+", s.lower()) if t]


def collect_textures(dirs):
    files = []
    for d in dirs:
        files += glob.glob(os.path.join(d, "**", "*.png"), recursive=True)
    return files


def find_mat_jsons(dirs, db):
    """Indexe les MI_*.json exportes par uex: nom de materiau -> proprietes."""
    for d in dirs:
        for f in glob.glob(os.path.join(d, "**", "*.json"), recursive=True):
            name = os.path.splitext(os.path.basename(f))[0]
            if name in db:
                continue
            try:
                j = json.load(open(f, "r", encoding="utf-8", errors="replace"))
            except Exception:
                continue
            if isinstance(j, dict) and ("Textures" in j or "Colors" in j or "Scalars" in j):
                db[name] = j
    return db


def tex_index(dirs):
    """basename minuscule (sans extension) -> chemin."""
    idx = {}
    for f in collect_textures(dirs):
        idx[os.path.splitext(os.path.basename(f))[0].lower()] = f
    return idx


def pick_texture(mat, texfiles):
    mt = norm_tokens(mat)
    if mt and mt[0] in ("mi", "m"):
        mt = mt[1:]
    best, best_score = None, -1
    for f in texfiles:
        bn = os.path.basename(f).lower()
        if "_alb" not in bn or any(k in bn for k in ("_nrm", "_mra", "_trs", "_msk")):
            continue
        bt = norm_tokens(bn)
        if bt and bt[0] in ("m", "t"):
            bt = bt[1:]
        inter = len(set(mt) & set(bt))
        score = 1.5 + inter * 2 - abs(len(set(bt)) - len(set(mt))) * 0.2
        if score > best_score:
            best, best_score = f, score
    return best


def _load_rgba(path, size):
    im = Image.open(path).convert("RGBA")
    im.thumbnail((size, size), LANCZOS)
    a = np.asarray(im, dtype=np.float32) / 255.0
    return a


def material_texture(mat, matdb, texidx, size=512, cache=None):
    """Renvoie {'rgb': lineaire (h,w,3), 'alpha': (h,w)|None, 'cutoff': float, 'src': str}.

    Priorite:
      1. le .json du materiau exporte par uex: texture _ALB (ou _OP pour l'opacite);
      2. materiau cheveu (Hair Color Root + _RCS): couleur racine/pointe + couverture;
      3. heuristique sur le nom (ancien comportement).
    """
    if cache is None:
        cache = {}
    if mat in cache:
        return cache[mat]

    info = {"rgb": None, "alpha": None, "cutoff": 0.5, "src": None}
    j = matdb.get(mat) or {}
    names = []
    for v in (j.get("Textures") or {}).values():
        if isinstance(v, dict):
            on = v.get("ObjectName")
            if isinstance(on, str):
                names.append(on.split("'")[1] if "'" in on else on)

    def find(suffix):
        for n in names:
            if n.lower().endswith(suffix):
                p = texidx.get(n.lower())
                if p:
                    return p
        return None

    alb = find("_alb")
    op = find("_op") or find("_msk")
    rcs = find("_rcs")
    colors = j.get("Colors") or {}

    if alb:
        a = _load_rgba(alb, size)
        rgb = np.power(a[..., :3], 2.2, dtype=np.float32)
        alpha = None
        if op:                                    # opacite separee
            o = _load_rgba(op, size)[..., 0]
            alpha = o if o.shape == rgb.shape[:2] else None
        elif a[..., 3].min() < 0.99:              # alpha dans l'albedo
            alpha = a[..., 3]
        info.update(rgb=rgb, alpha=alpha, src=os.path.basename(alb))
    elif "Hair Color Root" in colors and rcs:
        root = np.array([colors["Hair Color Root"][c] for c in "RGB"], np.float32)
        tip_src = colors.get("Hair Color Tip") or colors["Hair Color Root"]
        tip = np.array([tip_src[c] for c in "RGB"], np.float32)
        r = _load_rgba(rcs, size)[..., :3]
        grad = np.clip(r[..., 0], 0.0, 1.0)[..., None]         # R: racine -> pointe
        rgb = root[None, None, :] * (1.0 - grad) + tip[None, None, :] * grad
        mult = float((j.get("Scalars") or {}).get("Opacity Multiplier") or 1.0)
        info.update(rgb=np.clip(rgb, 0.0, 1.0),
                    alpha=np.clip(r[..., 1] * max(mult, 1.0), 0.0, 1.0),   # G: couverture
                    cutoff=0.40, src=os.path.basename(rcs) + " (hair)")
    else:
        p = pick_texture(mat, list(texidx.values()))
        if p:
            a = _load_rgba(p, size)
            info.update(rgb=np.power(a[..., :3], 2.2, dtype=np.float32),
                        alpha=(a[..., 3] if a[..., 3].min() < 0.99 else None),
                        src=os.path.basename(p))

    if info["rgb"] is None:
        cache[mat] = None
        return None
    if info["alpha"] is not None and info["alpha"].shape != info["rgb"].shape[:2]:
        h, w = info["rgb"].shape[:2]
        a8 = Image.fromarray((np.clip(info["alpha"], 0, 1) * 255).astype(np.uint8))
        info["alpha"] = np.asarray(a8.resize((w, h), LANCZOS), np.float32) / 255.0
    cache[mat] = info
    return info


# --------------------------------------------------------------------- render
def assemble(meshes, texidx, tex_cache, tex_size, matdb=None):
    """Garde les faces utiles et associe une texture à chaque face."""
    matdb = matdb or {}
    out = []
    for m in meshes:
        faces = m["faces"]
        wpt = m["wpt"]
        # coin -> point / uv
        pi = wpt[faces]                     # (F,3) index de point
        uv = m["wuv"][faces]                # (F,3,2)
        fmat = m["fmat"]
        texs = []
        for mi in range(len(m["mats"])):
            mat = m["mats"][mi] if m["mats"] else None
            texs.append(material_texture(mat, matdb, texidx, tex_size, tex_cache) if mat else None)
        fnrm = m["wnrm"][faces] if m.get("wnrm") is not None and len(m["wnrm"]) > 0 else None
        out.append({
            "name": os.path.basename(m["path"]),
            "points": m["points"],
            "pi": pi,
            "uv": uv,
            "nrm": fnrm,
            "fmat": fmat,
            "texs": texs,
        })
    return out


def render_frame(parts, angle_deg, W, H, center, scale,
                 sun_elev=35.0, sun_azim=35.0, sun=1.0, ambient=0.55,
                 exposure=1.15, fill=0.35, rim=0.25,
                 bg=(18, 20, 26), bg2=(52, 57, 68)):
    """Un soleil directionnel + ciel hemispherique + fill + rim, shading lisse."""
    a = math.radians(angle_deg)
    ca, sa = math.cos(a), math.sin(a)
    R = np.array([[ca, -sa, 0.0], [sa, ca, 0.0], [0.0, 0.0, 1.0]])

    g = np.linspace(0.0, 1.0, H, dtype=np.float32).reshape(H, 1, 1)
    img = np.array(bg, np.float32) * (1 - g) + np.array(bg2, np.float32) * g
    img = np.repeat(img, W, axis=1)
    zbuf = np.full((H, W), np.inf, dtype=np.float32)

    # soleil: vecteur surface -> soleil, en espace vue (x droite, y profondeur, z haut)
    el, az = math.radians(sun_elev), math.radians(sun_azim)
    Lkey = np.array([math.sin(az) * math.cos(el), -math.cos(az) * math.cos(el), math.sin(el)], np.float64)
    Lkey /= np.linalg.norm(Lkey)
    Lfill = np.array([-0.62, -0.42, 0.12], np.float64)
    Lfill /= np.linalg.norm(Lfill)
    Lrim = np.array([0.05, 0.92, 0.38], np.float64)
    Lrim /= np.linalg.norm(Lrim)
    Vdir = np.array([0.0, -1.0, 0.0], np.float64)
    Hvec = Lkey + Vdir
    Hvec /= np.linalg.norm(Hvec)

    sky = np.array([0.60, 0.70, 0.95], np.float32)
    grd = np.array([0.34, 0.30, 0.26], np.float32)
    sun_col = np.array([1.00, 0.96, 0.90], np.float32)

    xs = np.arange(W, dtype=np.float32)

    for part in parts:
        V = (part["points"] - center) @ R.T * scale
        sx = W * 0.5 + V[:, 0]
        sy = H * 0.5 - V[:, 2]
        sz = V[:, 1]

        pi = part["pi"]
        X, Y, Z = sx[pi], sy[pi], sz[pi]
        UV = part["uv"]
        NRM = part.get("nrm")

        area = (X[:, 1] - X[:, 0]) * (Y[:, 2] - Y[:, 0]) - (X[:, 2] - X[:, 0]) * (Y[:, 1] - Y[:, 0])
        idx = np.nonzero(np.abs(area) > 0.35)[0]
        idx = idx[np.argsort(Z.mean(axis=1)[idx])]

        p0 = V[pi[:, 0]]
        geo = np.cross(V[pi[:, 1]] - p0, V[pi[:, 2]] - p0)
        gl = np.linalg.norm(geo, axis=1)
        gl[gl == 0] = 1.0
        geo = geo / gl[:, None]

        for i in idx:
            x0, x1, x2 = X[i]
            y0, y1, y2 = Y[i]
            minx = max(int(np.floor(min(x0, x1, x2))), 0)
            maxx = min(int(np.ceil(max(x0, x1, x2))), W - 1)
            miny = max(int(np.floor(min(y0, y1, y2))), 0)
            maxy = min(int(np.ceil(max(y0, y1, y2))), H - 1)
            if minx > maxx or miny > maxy:
                continue
            det = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
            if abs(det) < 1e-9:
                continue

            gx = xs[minx:maxx + 1][None, :]
            gy = np.arange(miny, maxy + 1, dtype=np.float32)[:, None]
            l0 = ((y1 - y2) * (gx - x2) + (x2 - x1) * (gy - y2)) / det
            l1 = ((y2 - y0) * (gx - x2) + (x0 - x2) * (gy - y2)) / det
            l2 = 1.0 - l0 - l1
            mask = (l0 >= -0.002) & (l1 >= -0.002) & (l2 >= -0.002)
            if not mask.any():
                continue

            z = l0 * Z[i, 0] + l1 * Z[i, 1] + l2 * Z[i, 2]
            sub = zbuf[miny:maxy + 1, minx:maxx + 1]
            test = mask & (z < sub)
            if not test.any():
                continue

            if NRM is not None:
                Ls = np.stack([l0, l1, l2], axis=-1)
                n = np.einsum("hwc,ck->hwk", Ls, NRM[i])
                nl = np.sqrt((n * n).sum(axis=2))[:, :, None]
                n = n / np.maximum(nl, 1e-9)
            else:
                n = np.broadcast_to(geo[i], (l0.shape[0], l0.shape[1], 3))

            ndl = np.clip(n @ Lkey, 0.0, None)
            ndf = np.clip(n @ Lfill, 0.0, None)
            ndr = np.clip(n @ Lrim, 0.0, None) ** 3
            ndh = np.clip(n @ Hvec, 0.0, None) ** 36
            hemi = np.clip(0.5 + 0.5 * n[:, :, 2], 0.0, 1.0)[:, :, None]
            amb_col = grd[None, None, :] + (sky - grd)[None, None, :] * hemi

            diff = (amb_col * ambient
                    + sun_col[None, None, :] * (ndl * sun)[:, :, None]
                    + sun_col[None, None, :] * (fill * ndf)[:, :, None]
                    + sky[None, None, :] * (rim * ndr)[:, :, None])
            spec = (sun * ndh * 0.30)[:, :, None]

            mi = int(part["fmat"][i])
            tex = part["texs"][mi] if mi < len(part["texs"]) else None
            if tex is not None:
                rgb = tex["rgb"]
                th, tw = rgb.shape[:2]
                u = l0 * UV[i, 0, 0] + l1 * UV[i, 1, 0] + l2 * UV[i, 2, 0]
                v = l0 * UV[i, 0, 1] + l1 * UV[i, 1, 1] + l2 * UV[i, 2, 1]
                tx = np.clip((u * (tw - 1)).astype(np.int32), 0, tw - 1)
                ty = np.clip((v * (th - 1)).astype(np.int32), 0, th - 1)
                alb = rgb[ty, tx]
                if tex["alpha"] is not None:                 # alpha test (cheveux, cils, masque)
                    keep = tex["alpha"][ty, tx] >= tex["cutoff"]
                    test = test & keep
                    if not test.any():
                        continue
            else:
                alb = np.full(diff.shape, 0.55, np.float32)

            lin = alb * diff + spec
            col = 1.0 - np.exp(-exposure * np.clip(lin, 0.0, 8.0))       # tonemap doux
            col = np.power(np.clip(col, 0.0, 1.0), 1 / 2.2)               # -> sRGB

            sub[test] = z[test]
            target = img[miny:maxy + 1, minx:maxx + 1]
            target[test & mask] = col[test & mask] * 255.0

    return np.clip(img, 0, 255).astype(np.uint8)


def _draw_label(im, text):
    """Texte discret en bas de frame, sans dependre d'une police systeme."""
    d = ImageDraw.Draw(im)
    font = None
    try:
        font = ImageFont.load_default(size=max(9, im.size[1] // 46))
    except TypeError:              # Pillow < 10: pas d'argument size
        font = ImageFont.load_default()
    except Exception:
        font = None
    try:
        d.text((10, im.size[1] - 18), text, fill=(125, 128, 138), font=font)
    except Exception:
        d.text((10, im.size[1] - 18), text, fill=(125, 128, 138))


def fit_view(parts, margin=0.92):
    pts = np.concatenate([p["points"] for p in parts], axis=0)
    mn, mx = pts.min(axis=0), pts.max(axis=0)
    center = (mn + mx) / 2.0
    half = (mx - mn) / 2.0
    return center, half


def main(argv=None):
    ap = argparse.ArgumentParser(description="Turntable GIF d'un export uex (.psk + textures)")
    ap.add_argument("dirs", nargs="+", help="dossiers d'export (ou fichiers .psk)")
    ap.add_argument("-o", "--out", default="turntable.gif")
    ap.add_argument("--frames", type=int, default=36)
    ap.add_argument("--size", default="440x600", help="LARGEURxHAUTEUR")
    ap.add_argument("--fps", type=float, default=14.0)
    ap.add_argument("--tex", type=int, default=512, help="taille max des textures")
    ap.add_argument("--frames-dir", default=None, help="garde aussi les PNG")
    ap.add_argument("--yaw", type=float, default=360.0)
    ap.add_argument("--sun", type=float, default=1.0, help="intensite du soleil")
    ap.add_argument("--sun-elev", type=float, default=35.0, help="hauteur du soleil (deg)")
    ap.add_argument("--sun-azim", type=float, default=35.0, help="azimut du soleil (deg, 0=dans l'axe camera)")
    ap.add_argument("--ambient", type=float, default=0.55, help="lumiere ambiante (ciel)")
    ap.add_argument("--exposure", type=float, default=1.15)
    ap.add_argument("--fill", type=float, default=0.35)
    ap.add_argument("--rim", type=float, default=0.25)
    ap.add_argument("--label", default=None, help="texte en bas de chaque frame (defaut: uexgif n/N)")
    ap.add_argument("--version", action="version", version=f"uexgif {__version__}")
    args = ap.parse_args(argv)

    W, H = (int(x) for x in args.size.lower().split("x"))

    psk = []
    for d in args.dirs:
        if os.path.isdir(d):
            psk += sorted(glob.glob(os.path.join(d, "**", "*.psk"), recursive=True))
        elif d.endswith(".psk"):
            psk.append(d)
    if not psk:
        raise SystemExit("aucun .psk trouvé")

    meshes = [read_psk(p) for p in psk]
    dirs = [d for d in args.dirs if os.path.isdir(d)]
    texidx = tex_index(dirs)
    matdb = find_mat_jsons(dirs, {})
    tex_cache = {}
    parts = assemble(meshes, texidx, tex_cache, args.tex, matdb)

    center, half = fit_view(parts)
    margin = 0.92
    # échelle orthographique : la plus grande dimension tient dans la fenêtre
    spanx = max(half[0] * 2.0, 1e-6)      # largeur visible
    spanz = max(half[2] * 2.0, 1e-6)      # hauteur visible
    scale = min(W * margin / spanx, H * margin / spanz)

    srcs = sorted({v["src"] for v in tex_cache.values() if v and v.get("src")})
    print(f"{len(parts)} mesh(es), {sum(len(p['pi']) for p in parts)} triangles, "
          f"materiaux: {len(matdb)} json, textures: {srcs[:8]}")
    missing = [k for k, v in tex_cache.items() if v is None]
    if missing:
        print(f"  sans texture: {missing}")
    print(f"bbox: {half * 2} scale={scale:.3f}")

    frames = []
    if args.frames_dir:
        os.makedirs(args.frames_dir, exist_ok=True)
    for i in range(args.frames):
        ang = args.yaw * i / args.frames
        img = render_frame(parts, ang, W, H, center, scale,
                           sun_elev=args.sun_elev, sun_azim=args.sun_azim, sun=args.sun,
                           ambient=args.ambient, exposure=args.exposure,
                           fill=args.fill, rim=args.rim)
        im = Image.fromarray(img)
        label = args.label or f"uexgif {i + 1}/{args.frames} {ang:.0f}deg"
        _draw_label(im, label)
        frames.append(im)
        if args.frames_dir:
            im.save(os.path.join(args.frames_dir, f"frame_{i:03d}.png"))
        print(f"  frame {i + 1}/{args.frames}", flush=True)

    dur = int(1000 / args.fps)
    pal = [f.convert("P", palette=ADAPTIVE, colors=192) for f in frames]
    pal[0].save(args.out, save_all=True, append_images=pal[1:], duration=dur, loop=0, optimize=True)
    print(f"ecrit {args.out} ({os.path.getsize(args.out) / 1e6:.1f} Mo, {args.frames} frames)")


if __name__ == "__main__":
    main()
