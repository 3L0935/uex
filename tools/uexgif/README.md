# uexgif — turntable preview GIF for uex exports

Optional companion to `uex`. It takes what `uex export` just wrote (`.psk` ActorX meshes
and `.png` textures) and renders the assembled model **rotating on itself** as a GIF, in
its bind pose (the A-pose the game cooked).

No GPU, no Blender, no engine: a small numpy rasterizer (z-buffer, per-pixel affine UV,
smooth normals, lambert + sky + fill + rim, exponential tonemap, sRGB).

## Install

```bash
pip install -r requirements.txt      # numpy + Pillow, nothing else
```

Pure Python 3.9+, works on Windows, macOS and Linux. It only reads an export directory,
so uex's path quirks on POSIX never apply here.

## Usage

```bash
# one export folder per part; they are assembled in the same space
python3 uexgif.py rips/body rips/mask1978 rips/suit1978 -o myers.gif \
        --frames 32 --size 480x640
```

A folder, a single `.psk`, or several of either. Everything found is merged, not
splatted one after the other.

| flag | default | what |
|---|---|---|
| `--frames` | 36 | frames in the loop |
| `--size` | 440x600 | frame size, `LxH` |
| `--fps` | 14 | GIF frame rate |
| `--tex` | 512 | max texture size fed to the renderer |
| `--frames-dir` | off | also keep the PNG frames |
| `--yaw` | 360 | degrees per loop |
| `--sun` | 1.0 | sun intensity |
| `--sun-elev` | 35 | sun height in degrees |
| `--sun-azim` | 35 | 0 = sun along the camera axis, positive = to the right |
| `--ambient` | 0.55 | hemispheric sky light |
| `--fill` / `--rim` | 0.35 / 0.25 | frontal fill / back rim |
| `--exposure` | 1.15 | tonemap exposure |
| `--label` | `uexgif n/N` | caption burned at the bottom |

A dark shot is fixed with `--sun 1.4 --ambient 0.7`, not by tweaking textures.

## What it does

1. **Parses `.psk`** (ActorX): `PNTS0000` (points), `VTXW0000` (wedges: point + UV +
   material), `FACE0000` (triangles), `MATT0000` (material names), `VTXNORMS` (smooth
   normals).
2. **Unsigned 16-bit indices.** A `.psk` stores point and wedge indices as `uint16`;
   above 32767 entries (a big mesh does reach that) a signed read yields negative
   indices and a shredded mesh. The parser reads `u2` and only falls back to 32-bit
   when values are out of range.
3. **Resolves materials** from the `MI_*.json` uex writes next to each mesh
   (`Textures`, `Colors`, `Scalars`):
   - `_ALB` as albedo, `_OP`/`_MSK` as opacity, PNG alpha when there is one;
   - **hair** (material with `Hair Color Root`/`Tip` and no `_ALB`): colour rebuilt
     root→tip, coverage read from the `_RCS` G channel, then alpha-tested;
   - otherwise a fuzzy filename match (`Boot` finds `Boots`).
4. **Renders** each frame: rotate around Z (UE is Z-up), orthographic projection fitted
   to the whole bounding box, z-buffer, interpolated normals, sun + sky + fill + rim.
5. **Assembles** the GIF with Pillow (adaptive palette).

## Limits

- **Bind pose only.** Applying a `.psa` needs skinning (`REFSKELT` hierarchy +
  `RAWWEIGHTS` + key tracks); not implemented yet.
- No normal, roughness or metallic maps: diffuse shading only, no metal, no
  subsurface, no refraction.
- Transparent materials are alpha-tested, not sorted: thin hair and eyelashes can
  flicker depending on the view.
- A `.psk` whose vertices live in another space than the body will look offset; that is
  an export decision (same skeleton = same space), not something the viewer can guess.
