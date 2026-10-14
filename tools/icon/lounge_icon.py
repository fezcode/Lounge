"""Lounge's icon, rendered the way Vista's icons were: a small 3D scene with real
materials and studio light, on a transparent background, seen at an angle.

A glossy piano-black flat-screen TV glowing warm amber, a strip of film curling out
from behind it, a white gamepad lying in front and a glossy blue music note beside it:
films, music and games, from the couch. Run headless:

    blender -b --factory-startup -P tools/icon/lounge_icon.py -- <out.png> [size]
"""
import math
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = argv[0] if argv else "//icon.png"
SIZE = int(argv[1]) if len(argv) > 1 else 768
# "small": just the glowing TV and the note, framed larger, for 16-32 px where four
# objects turn to noise (Vista drew separate small icons the same way).
SMALL = len(argv) > 2 and argv[2] == "small"

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene


# ------------------------------------------------------------------ materials

def material(name, color, rough, coat=0.0, coat_rough=0.05, metallic=0.0, emission=None, strength=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*color, 1)
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metallic
    b.inputs["Coat Weight"].default_value = coat
    b.inputs["Coat Roughness"].default_value = coat_rough
    if emission:
        b.inputs["Emission Color"].default_value = (*emission, 1)
        b.inputs["Emission Strength"].default_value = strength
    return m


piano = material("Piano black", (0.004, 0.004, 0.005), 0.15, coat=1.0, coat_rough=0.02)
satin = material("Satin metal", (0.30, 0.30, 0.33), 0.3, metallic=1.0)
plastic = material("White plastic", (0.82, 0.83, 0.85), 0.32, coat=0.5, coat_rough=0.12)
dark_plastic = material("Dark plastic", (0.02, 0.02, 0.025), 0.35, coat=0.4)
note_blue = material("Note blue", (0.01, 0.12, 0.65), 0.18, coat=1.0, coat_rough=0.02)
btn = {
    "A": material("A", (0.05, 0.45, 0.08), 0.2, coat=1.0),
    "B": material("B", (0.65, 0.04, 0.03), 0.2, coat=1.0),
    "X": material("X", (0.02, 0.18, 0.75), 0.2, coat=1.0),
    "Y": material("Y", (0.8, 0.55, 0.02), 0.2, coat=1.0),
}
guide = material("Guide", (0.9, 0.9, 0.9), 0.2, coat=1.0, emission=(1.0, 1.0, 1.0), strength=1.5)


def screen_material():
    """Warm amber light, brightest a little above centre, under glossy glass."""
    m = bpy.data.materials.new("Screen")
    m.use_nodes = True
    n = m.node_tree
    b = n.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (0, 0, 0, 1)
    b.inputs["Roughness"].default_value = 0.04
    b.inputs["Coat Weight"].default_value = 1.0
    b.inputs["Coat Roughness"].default_value = 0.01
    coord = n.nodes.new("ShaderNodeTexCoord")
    mapping = n.nodes.new("ShaderNodeMapping")
    mapping.inputs["Location"].default_value = (-0.42, -0.62, 0)
    mapping.inputs["Scale"].default_value = (1.25, 1.9, 1)
    grad = n.nodes.new("ShaderNodeTexGradient")
    grad.gradient_type = "SPHERICAL"
    ramp = n.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.32, 0.07, 0.01, 1)
    ramp.color_ramp.elements[1].position = 0.8
    ramp.color_ramp.elements[1].color = (1.0, 0.62, 0.20, 1)
    n.links.new(coord.outputs["UV"], mapping.inputs["Vector"])
    n.links.new(mapping.outputs["Vector"], grad.inputs["Vector"])
    n.links.new(grad.outputs["Fac"], ramp.inputs["Fac"])
    n.links.new(ramp.outputs["Color"], b.inputs["Emission Color"])
    b.inputs["Emission Strength"].default_value = 1.6
    return m


def film_material():
    """Dark film base with see-through sprocket holes and lit, slightly varied frames.
    UV: u runs along the strip in strip-widths, v across it (0..1)."""
    m = bpy.data.materials.new("Film")
    m.use_nodes = True
    n = m.node_tree
    nodes, links = n.nodes, n.links
    out = nodes["Material Output"]
    base = nodes["Principled BSDF"]
    base.inputs["Base Color"].default_value = (0.02, 0.016, 0.012, 1)
    base.inputs["Roughness"].default_value = 0.25
    base.inputs["Coat Weight"].default_value = 0.8

    frame = nodes.new("ShaderNodeBsdfPrincipled")
    frame.inputs["Base Color"].default_value = (0.1, 0.05, 0.02, 1)
    frame.inputs["Roughness"].default_value = 0.1
    frame.inputs["Coat Weight"].default_value = 1.0
    frame.inputs["Emission Strength"].default_value = 1.1
    hole = nodes.new("ShaderNodeBsdfTransparent")

    uv = nodes.new("ShaderNodeTexCoord")
    sep = nodes.new("ShaderNodeSeparateXYZ")
    links.new(uv.outputs["UV"], sep.inputs["Vector"])
    u, v = sep.outputs["X"], sep.outputs["Y"]

    def math(op, a, b=None, value=None):
        node = nodes.new("ShaderNodeMath")
        node.operation = op
        links.new(a, node.inputs[0])
        if b is not None:
            links.new(b, node.inputs[1])
        elif value is not None:
            node.inputs[1].default_value = value
        return node.outputs[0]

    def band(x, lo, hi):  # 1 inside [lo, hi]
        return math("MULTIPLY", math("GREATER_THAN", x, value=lo), math("LESS_THAN", x, value=hi))

    # Frames: windows across the middle of the strip, one per 0.8 widths.
    fu = math("FRACT", math("DIVIDE", u, value=0.8))
    in_frame = math("MULTIPLY", band(v, 0.18, 0.82), band(fu, 0.07, 0.93))
    # Sprocket holes: small windows near each edge, four per frame.
    hu = math("FRACT", math("DIVIDE", u, value=0.2))
    edge = math("MAXIMUM", band(v, 0.04, 0.11), band(v, 0.89, 0.96))
    in_hole = math("MULTIPLY", edge, band(hu, 0.3, 0.7))

    # Each frame lit a little differently: amber, rose, teal, gold.
    idx = math("FLOOR", math("DIVIDE", u, value=0.8))
    pick = math("FRACT", math("MULTIPLY", idx, value=0.37))
    ramp = nodes.new("ShaderNodeValToRGB")
    els = ramp.color_ramp.elements
    els[0].position, els[0].color = 0.0, (1.0, 0.55, 0.15, 1)
    els[1].position, els[1].color = 1.0, (0.2, 0.55, 0.65, 1)
    mid = els.new(0.45)
    mid.color = (0.95, 0.35, 0.3, 1)
    gold = els.new(0.7)
    gold.color = (1.0, 0.8, 0.35, 1)
    ramp.color_ramp.interpolation = "CONSTANT"
    links.new(pick, ramp.inputs["Fac"])
    links.new(ramp.outputs["Color"], frame.inputs["Emission Color"])

    mix_frame = nodes.new("ShaderNodeMixShader")
    links.new(in_frame, mix_frame.inputs["Fac"])
    links.new(base.outputs["BSDF"], mix_frame.inputs[1])
    links.new(frame.outputs["BSDF"], mix_frame.inputs[2])
    mix_hole = nodes.new("ShaderNodeMixShader")
    links.new(in_hole, mix_hole.inputs["Fac"])
    links.new(mix_frame.outputs["Shader"], mix_hole.inputs[1])
    links.new(hole.outputs["BSDF"], mix_hole.inputs[2])
    links.new(mix_hole.outputs["Shader"], out.inputs["Surface"])
    return m


# ------------------------------------------------------------------ helpers

def finish(o, mat, parent=None, smooth=True):
    if smooth:
        bpy.ops.object.shade_smooth()
    o.data.materials.append(mat)
    if parent:
        o.parent = parent
    return o


def box(name, size, loc, mat, bevel=0.03, segments=4, parent=None, rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.object
    o.name = name
    o.scale = size
    bpy.ops.object.transform_apply(scale=True)
    m = o.modifiers.new("Bevel", "BEVEL")
    m.width = bevel
    m.segments = segments
    return finish(o, mat, parent)


def cylinder(name, r, depth, loc, mat, parent=None, rot=(0, 0, 0), scale=(1, 1, 1), bevel=0.3):
    bpy.ops.mesh.primitive_cylinder_add(radius=r, depth=depth, location=loc, rotation=rot, vertices=64)
    o = bpy.context.object
    o.name = name
    o.scale = scale
    m = o.modifiers.new("Bevel", "BEVEL")
    m.width = min(r, depth) * bevel
    m.segments = 3
    return finish(o, mat, parent)


def sphere(name, loc, scale, mat, parent=None, rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=1, location=loc, rotation=rot, segments=64, ring_count=32)
    o = bpy.context.object
    o.name = name
    o.scale = scale
    return finish(o, mat, parent)


def empty(name, loc, rot_deg=(0, 0, 0)):
    e = bpy.data.objects.new(name, None)
    scene.collection.objects.link(e)
    e.location = loc
    e.rotation_euler = tuple(math.radians(a) for a in rot_deg)
    return e


# ------------------------------------------------------------------ the TV

tv = empty("TV", (0.25, 0.55, 0), (0, 0, -30))
box("Panel", (1.70, 0.07, 1.00), (0, 0, 1.12), piano, bevel=0.035, segments=5, parent=tv)
bpy.ops.mesh.primitive_plane_add(size=1, location=(0, -0.0365, 1.13), rotation=(math.radians(90), 0, 0))
scr = bpy.context.object
scr.scale = (1.58, 0.88, 1)
finish(scr, screen_material(), tv, smooth=False)
box("Neck", (0.10, 0.06, 0.56), (0, 0.03, 0.34), satin, bevel=0.02, parent=tv)
cylinder("Foot", 0.36, 0.04, (0, 0.03, 0.03), satin, parent=tv, scale=(1, 0.62, 1))


# ------------------------------------------------------------------ the film strip

def film_strip():
    """A ribbon of film rising from behind the TV's left edge and curling over in a loop."""
    width, steps = 0.26, 160
    pts = []
    for i in range(steps + 1):
        t = i / steps
        a = math.radians(-10 + 270 * t)                     # up out of the TV's back, over and down
        r = 0.36 + 0.12 * t
        pts.append(Vector((-0.55 - 0.25 * t + r * math.cos(a), 0.85 - 0.75 * t, 1.25 + r * math.sin(a))))
    verts, faces, uvs = [], [], []
    length = 0.0
    for i, p in enumerate(pts):
        tangent = (pts[min(i + 1, steps)] - pts[max(i - 1, 0)]).normalized()
        side = tangent.cross(Vector((0.35, 1.0, 0.15)).normalized()).normalized()
        if i:
            length += (p - pts[i - 1]).length
        verts += [p - side * width / 2, p + side * width / 2]
        uvs.append(length / width)
    for i in range(steps):
        a, b = 2 * i, 2 * i + 2
        faces.append((a, b, b + 1, a + 1))
    mesh = bpy.data.meshes.new("Film")
    mesh.from_pydata([tuple(v) for v in verts], [], faces)
    layer = mesh.uv_layers.new(name="UVMap")
    for poly in mesh.polygons:
        for li in poly.loop_indices:
            vi = mesh.loops[li].vertex_index
            layer.data[li].uv = (uvs[vi // 2], float(vi % 2))
    o = bpy.data.objects.new("Film", mesh)
    scene.collection.objects.link(o)
    bpy.context.view_layer.objects.active = o
    o.select_set(True)
    s = o.modifiers.new("Thickness", "SOLIDIFY")
    s.thickness = 0.008
    for p in mesh.polygons:
        p.use_smooth = True
    mesh.materials.append(film_material())
    o.select_set(False)
    return o


if not SMALL:
    film_strip()


# ------------------------------------------------------------------ the gamepad

def gamepad():
    """A generic two-grip controller (no maker's logo): a rounded outline with shoulders
    and grips, extruded and bevelled, with sticks, d-pad and face buttons on top. It lies
    on the floor in front of the TV, grips toward the viewer."""
    root = empty("Pad", (-0.12, -0.52, 0.0), (-6, 0, 22))
    outline = [(0.00, 0.22), (0.30, 0.25), (0.46, 0.15), (0.51, -0.04), (0.48, -0.30), (0.37, -0.43),
               (0.25, -0.38), (0.15, -0.18), (0.00, -0.13)]
    outline += [(-x, y) for x, y in reversed(outline[1:-1])]
    curve = bpy.data.curves.new("PadBody", "CURVE")
    curve.dimensions = "2D"
    curve.fill_mode = "BOTH"
    curve.extrude = 0.055
    curve.bevel_depth = 0.055
    curve.bevel_resolution = 8
    curve.resolution_u = 24
    spline = curve.splines.new("BEZIER")
    spline.bezier_points.add(len(outline) - 1)
    for bp, (x, y) in zip(spline.bezier_points, outline):
        bp.co = (x, y, 0)
        bp.handle_left_type = bp.handle_right_type = "AUTO"
    spline.use_cyclic_u = True
    body = bpy.data.objects.new("PadBody", curve)
    scene.collection.objects.link(body)
    body.location = (0, 0, 0.11)
    body.parent = root
    curve.materials.append(plastic)

    top = 0.22
    for x, y in ((-0.27, 0.05), (0.12, -0.11)):              # left stick high, right stick low
        cylinder("StickBase", 0.075, 0.03, (x, y, top), dark_plastic, parent=root)
        cylinder("Stick", 0.062, 0.05, (x, y, top + 0.04), dark_plastic, parent=root, bevel=0.45)
    box("DpadH", (0.13, 0.042, 0.03), (-0.12, -0.11, top), dark_plastic, bevel=0.01, parent=root)
    box("DpadV", (0.042, 0.13, 0.03), (-0.12, -0.11, top), dark_plastic, bevel=0.01, parent=root)
    for name, (x, y) in {"A": (0.27, -0.01), "B": (0.33, 0.05), "X": (0.21, 0.05), "Y": (0.27, 0.11)}.items():
        sphere(name, (x, y, top), (0.034, 0.034, 0.022), btn[name], parent=root)
    cylinder("Guide", 0.05, 0.016, (0, 0.13, top - 0.005), guide, parent=root)

if not SMALL:
    gamepad()


# ------------------------------------------------------------------ the music note

def music_note():
    """A glossy eighth note standing on the floor: tilted head, stem, swept flag."""
    root = empty("Note", (0.86, -0.12, 0), (0, 0, -22))
    root.scale = (1.1, 1.1, 1.1)
    sphere("Head", (0, 0, 0.17), (0.2, 0.14, 0.15), note_blue, parent=root, rot=(0, math.radians(-24), 0))
    cylinder("Stem", 0.032, 1.0, (0.17, 0, 0.67), note_blue, parent=root, bevel=0.4)
    curve = bpy.data.curves.new("Flag", "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = 0.045
    curve.bevel_resolution = 6
    spline = curve.splines.new("BEZIER")
    spline.bezier_points.add(2)
    p = spline.bezier_points
    p[0].co, p[0].handle_left, p[0].handle_right = (0.17, 0, 1.15), (0.17, 0, 1.25), (0.2, 0, 1.0)
    p[1].co, p[1].handle_left, p[1].handle_right = (0.42, 0, 0.82), (0.36, 0, 0.95), (0.47, 0, 0.7)
    p[2].co, p[2].handle_left, p[2].handle_right = (0.36, 0, 0.56), (0.45, 0, 0.62), (0.3, 0, 0.5)
    flag = bpy.data.objects.new("Flag", curve)
    scene.collection.objects.link(flag)
    flag.data.materials.append(note_blue)
    flag.parent = root


music_note()


# ------------------------------------------------------------------ floor, light, camera

# No floor: the contact shadow is added in 2D afterwards (a shadow catcher greys the
# whole floor with indirect light).
aim = empty("Aim", (0.42, 0.25, 0.72) if SMALL else (0.06, 0.05, 0.72))


def area(name, loc, energy, size, color=(1, 1, 1)):
    d = bpy.data.lights.new(name, "AREA")
    d.energy, d.size, d.color = energy, size, color
    o = bpy.data.objects.new(name, d)
    o.location = loc
    scene.collection.objects.link(o)
    o.constraints.new("TRACK_TO").target = aim
    return o


area("Key", (-3.5, -2.5, 4.5), 380, 3.0)
area("Fill", (3.5, -3.0, 2.0), 110, 2.5, (0.85, 0.9, 1.0))
area("Rim", (1.0, 4.0, 3.0), 300, 2.0)
area("Top", (0.0, -0.5, 5.0), 120, 4.0)

world = bpy.data.worlds.new("World")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.6, 0.62, 0.68, 1)
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.12
scene.world = world

cam_data = bpy.data.cameras.new("Camera")
cam_data.lens = 128 if SMALL else 110
cam = bpy.data.objects.new("Camera", cam_data)
cam.location = (-5.6, -7.0, 4.4)                 # off to the left and above: a three-quarter view
scene.collection.objects.link(cam)
cam.constraints.new("TRACK_TO").target = aim
scene.camera = cam

scene.render.engine = "CYCLES"
scene.cycles.samples = 256
scene.cycles.use_denoising = True
scene.render.film_transparent = True
scene.render.resolution_x = SIZE
scene.render.resolution_y = SIZE
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"
scene.view_settings.view_transform = "Standard"
scene.view_settings.look = "None"
scene.render.filepath = OUT
bpy.ops.render.render(write_still=True)
