# GPU requirements

A GPU is needed for exactly one feature: the 3D model preview. Importing, indexing, resolving,
comparing, previewing images, text, audio, GFF trees, and MDL metadata, building, verifying, and
publishing all work with no usable GPU at all. If 3D rendering is unavailable, a model preview slot
falls back to the MDL text preview and carries on.

## How rendering actually reaches your GPU

On Windows, Avalonia renders through **ANGLE**. That means the context the application gets is
**OpenGL ES via EGL**, not desktop OpenGL, and the ANGLE runtime translates it to Direct3D
underneath. `av_libglesv2.dll` ships in the release folder for exactly this reason, and the
tool-capability startup check reports whether it is present.

Because the profile is decided at runtime by ANGLE and by your driver, the application never assumes
what the context can do. It probes.

## When the probe runs

**Not at startup.** The tool-capability preflight reports GPU capability as deferred, and nothing
creates a rendering context until you actually open a 3D preview slot. The probe runs once, at
device construction, the first time a viewport initialises.

This is deliberate: an operator who never opens a 3D preview never has a GL context created on their
behalf, which is the difference between a driver problem being an inconvenience and it being a
startup crash.

## What the probe reads

| Probed | From | Used for |
|---|---|---|
| Profile | `GL_VERSION` contains `OpenGL ES` | Selects the shader dialect |
| Version | Major and minor parsed from `GL_VERSION` | Vertex array object and non-power-of-two texture assumptions |
| Renderer name | `GL_RENDERER` | Diagnostics only |
| Maximum texture size | `GL_MAX_TEXTURE_SIZE` | The hard capability floor below |

From those it derives the shader `#version` directive it will use — `#version 300 es` on an embedded
(ES) profile, `#version 330 core` on a desktop profile — and whether core vertex array objects and
full mipmapped, repeat-wrapped non-power-of-two textures can be assumed. Vertex array objects are
treated as core only at major version 3 or above; full NPOT support is assumed on desktop GL 2.0+ and
on GLES 3.0+ only, because GLES 2.0's NPOT support is too restricted to rely on.

## When the probe falls short

Initialisation never throws. A shortfall is reported, the slot degrades to text, and the reason is
folded into the slot's diagnostics so it stays visible.

| Condition | What you see |
|---|---|
| `GL_MAX_TEXTURE_SIZE` below 512 | `GL_MAX_TEXTURE_SIZE (n) is below the minimum required for model previews (512).` The slot shows the MDL text preview |
| Shader program fails to compile or link | The driver's link log, or `shader program failed to compile or link.` if the driver gave none. The slot shows the MDL text preview |
| The device is lost, or belongs to a different context | `device is lost or belongs to a different context` |
| The rendering context is lost while a preview is open | `The 3D rendering context was lost.` The slot degrades rather than continuing to show a dead, stale frame |
| A fourth concurrent viewport is attempted | `Model viewport limit reached: at most 3 concurrent 3D previews are allowed.` No GPU work is attempted at all for that slot |

512 is a floor, not a recommendation. Any GPU that can run NWN:EE clears it by a wide margin. Seeing
that message in practice almost always means a software rasteriser, a remote-desktop session, or a
virtual machine without GPU passthrough — not a real hardware limit.

## The three-viewport limit

At most **three** 3D viewports can hold a rendering context at once. This matches the comparison
panel's fixed three-slot layout, and it is enforced structurally rather than by hoping the UI never
asks for a fourth: each viewport must acquire one of three slots when it attaches to the visual tree,
and a refused acquire means the viewport performs no GPU work whatsoever and immediately reports
itself unavailable.

You can hit the limit transiently while switching which items occupy the comparison slots, because a
new viewport can attach before the outgoing one has detached and released its slot. The affected slot
degrades to text; changing the selection again re-creates it normally.

## Model features that do not render

Some MDL features are parsed and reported but not rendered. These are not GPU failures — they appear
as unsupported-feature diagnostics on the preview regardless of your hardware, and they never block a
build:

- animation playback — animations present on a model are reported and the model is drawn at rest;
- skinmesh nodes — drawn at rest pose, without bone deformation;
- particle emitters — reported, not drawn.

A preview showing these diagnostics is working correctly. See [Troubleshooting](TROUBLESHOOTING.md)
for the general rule on what warns versus what blocks a build.

## If 3D previews do not work

1. Confirm `av_libglesv2.dll` is present in the extracted release folder. The tool-capability
   preflight names it if it is missing. A partial extraction is the most common cause.
2. Read `Logs\srncc.log` for the capability record, which is written the first time a rendering
   device is constructed. It carries the renderer name and version string your driver reported —
   that single line usually identifies the problem.
3. Check whether the slot's diagnostics carry one of the messages in the table above.
4. If you are on remote desktop or in a VM, expect a software rasteriser and expect the floor check
   to be the thing you hit.

Everything else in the application continues to work while you sort this out.
