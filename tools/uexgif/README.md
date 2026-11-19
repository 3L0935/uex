# uexgif — turntable preview renderer for uex exports

Optional companion to `uex`. It takes what `uex export` just wrote (`.psk` / `.pskx`
ActorX meshes and `.png` textures) and renders the assembled model **rotating on
itself**, in its bind pose, as a GIF, an animated WebP or a PNG sprite sheet.

No GPU, no Blender, no engine: a small numpy rasterizer (z-buffer, per-pixel affine UV,
interpolated normals, sun + sky + fill + rim, Blinn specular, exponential tonemap, sRGB).

## Install

```bash
pip install -r requirements.txt      # numpy + Pillow, nothing else
python3 uexgif.py --version
```

Python 3.8+, Linux / macOS / Windows. It only *reads* an export directory, so uex's
output-path quirk on POSIX never applies here.

- On Windows, call it with `py uexgif.py …` (`python3` opens the Microsoft Store stub on
  a stock install).
- Deep `Game/Content/…` trees under a rips folder can pass 260 characters; enable
  `LongPathsEnabled` if the OS complains.

## Usage

```bash
# one export folder per part; everything is assembled in the same space
python3 uexgif.py rips/body rips/mask1978 rips/suit1978 -o myers.gif \
        --frames 32 --size 480x640

# animated WebP (full colour, no palette shimmer) and a sprite sheet
python3 uexgif.py rips/body -o preview.webp --frames 48 --size 720x960
python3 uexgif.py rips/body -o sheet.png --format sprites --frames 12
```

A folder, a single `.psk`/`.pskx`, or several of either. Duplicates are dropped, and a
file passed directly still pulls its own folder for textures and material JSON.

| flag | default | what |
|---|---|---|
| `-o, --out` | `turntable.gif` | output file |
| `--format` | from the extension | `gif`, `webp`, `sprites` |
| `--frames` | 36 | frames per loop |
| `--size` | 440x600 | frame size, `WIDTHxHEIGHT` |
| `--fps` | 14 | GIF/WebP frame rate |
| `--tex` | 512 | max texture size fed to the renderer |
| `--frames-dir` | off | keep every PNG frame too |
| `--yaw` | 360 | degrees per loop |
| `--sun` | 1.0 | sun intensity |
| `--sun-elev` | 35 | sun height, degrees |
| `--sun-azim` | 35 | 0 = sun along the camera axis, positive = to the right |
| `--ambient` | 0.55 | hemispheric sky light |
| `--fill` / `--rim` | 0.35 / 0.25 | frontal fill / back rim |
| `--exposure` | 1.15 | tonemap exposure |
| `--label` | `uexgif n/N` | caption burned into each frame |
| `--quiet` | off | no per-frame progress |

Lighting is anchored to the camera, not to the model: the sun stays put while the model
turns. A dark shot is fixed with `--sun 1.4 --ambient 0.7`, not by tweaking textures.

Video, if you want it: `--frames-dir frames` then
`ffmpeg -framerate 14 -i frames/frame_%03d.png -pix_fmt yuv420p preview.mp4`. Keeping
ffmpeg out of the tool is deliberate: numpy + Pillow only.

## What it does

1. **Parses `.psk`** (ActorX): `PNTS0000` (points), `VTXW0000` (wedges: point + UV +
   material), `FACE0000` / `FACE3200` (triangles), `MATT0000` (material names),
   `VTXNORMS` (smooth normals). Every chunk is bounds-checked and its declared record
   size is used as the stride.
2. **32-bit index slots.** The wedge point index and the face indices are written as
   32-bit fields (the 16-bit variants leave the upper half at zero); above 65 535 entries
   a 16-bit read wraps silently, so the parser reads u32 first and falls back to u16 only
   when those values cannot be indices. Meshes above 65 536 wedges ship a `FACE3200`
   chunk, which is supported.
3. **Resolves materials** from the `MI_*.json` uex writes next to each mesh
   (`Textures`, `Colors`, `Scalars`):
   - `_ALB` as albedo, `_OP` as opacity, PNG alpha when there is one;
   - **hair** (no `_ALB`, but `Hair Color Root`/`Tip` + `_RCS`): colour rebuilt
     root→tip, coverage from the `_RCS` G channel, then alpha-tested;
   - otherwise a fuzzy filename match that requires at least one shared token, so a
     missing texture is reported instead of silently borrowing the wrong one.
4. **Renders** each frame: rotate around Z (UE is Z-up), orthographic projection fitted
   to the swept radius so no part leaves the frame at any angle, z-buffer, interpolated
   normals rotated with the model, two-sided shading, wrapped UVs.
5. **Encodes**: GIF with a single global palette and no dither (per-frame palettes plus
   Floyd–Steinberg make the background shimmer), animated WebP, or a PNG sheet.

## Limits

- **Bind pose only.** Applying a `.psa` needs skinning (`REFSKELT` hierarchy +
  `RAWWEIGHTS` + key tracks); not implemented yet.
- No normal, roughness or metallic maps: diffuse shading only, no metal, no
  subsurface, no refraction.
- Masked materials are alpha-tested, not sorted: thin hair can flicker with the view.
- Nanite, grooms and multiple LODs of the same mesh are not handled: every `.psk` found
  is loaded, so two LODs in one folder z-fight.
- One process, one triangle loop: ~4 s per frame at 480x640 for a 155k-triangle
  character. Batching the rasterizer or adding `--jobs` is the next speed lever.
