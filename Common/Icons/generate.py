"""
Generates the per-platform icon resources from the SVGs in this folder.

    python Common/Icons/generate.py

These SVGs are the shared masters, which is why they live in Common/ rather than under
either app: the two shells must show the *same* marks, and the only way to guarantee that
is for both to be generated from one set of files. Do not add a platform's icons anywhere
else, and do not hand-edit either output.

Two outputs, because neither toolkit renders SVG and neither would be worth a renderer for
twenty line drawings:

    win/Icons.xaml              WPF Geometry resources, keyed "Icon.<PascalName>"
    mac/Yinyue.Mac/UI/Icons.g.cs   the same path data as C# string constants

Every *.svg here becomes one <Geometry> resource keyed "Icon.<PascalName>" — skip-back.svg is
Icon.SkipBack, repeat-1.svg is Icon.Repeat1. The Icon control (win/Controls/Icon.cs) looks
them up by that key and draws them as a stroke in the current Foreground, so the SVGs' own
stroke attributes are not carried over: Lucide's 24-unit box, 2-unit round stroke, no fill,
is applied once in the control's template instead.

WPF has no SVG renderer, and adding one would cost startup time for twenty line drawings.
The path data is close enough to WPF's path mini-language that a small translation covers
it: shapes become paths, and each element's first move is made absolute so several elements
can be concatenated into one geometry without a relative "m" landing on the previous
element's end point.

Icons.xaml is committed. Only run this when an SVG is added or changed.
"""
import os
import re
import math
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT_WIN = os.path.join(ROOT, "win", "Icons.xaml")
OUT_MAC = os.path.join(ROOT, "mac", "Yinyue.Mac", "UI", "Icons.g.cs")
NS = "{http://www.w3.org/2000/svg}"

# SVG path grammar: how many numbers each command takes.
ARITY = {"M": 2, "L": 2, "T": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "A": 7, "Z": 0}
TOKEN = re.compile(r"[MmLlHhVvCcSsQqTtAaZz]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")


def key_for(file_name):
    stem = os.path.splitext(file_name)[0]
    return "Icon." + "".join(part[:1].upper() + part[1:] for part in stem.split("-"))


def num(value):
    text = f"{float(value):.4f}".rstrip("0").rstrip(".")
    return text if text not in ("", "-0") else "0"


def normalise_path(d):
    """
    Re-emits path data with one explicit command per segment and the first move absolute.
    Arc flags are checked to be single 0/1 tokens, so a compacted "01" cannot slip through
    as the number one.
    """
    tokens = TOKEN.findall(d)
    out = []
    i = 0
    command = None
    first = True

    while i < len(tokens):
        token = tokens[i]
        if token.isalpha():
            command = token
            i += 1
            if command.upper() == "Z":
                out.append("Z")
                continue
        elif command is None:
            raise SystemExit(f"path data starts with a number: {d!r}")
        else:
            # Implicit repeat: after a move it is a line, otherwise the same command.
            if command == "M":
                command = "L"
            elif command == "m":
                command = "l"

        arity = ARITY[command.upper()]
        args = tokens[i:i + arity]
        if len(args) != arity or any(a.isalpha() for a in args):
            raise SystemExit(f"malformed {command} in {d!r}")
        i += arity

        if command.upper() == "A" and (args[3] not in ("0", "1") or args[4] not in ("0", "1")):
            raise SystemExit(f"arc flags must be separate 0/1 tokens: {d!r}")

        emit = command
        if first:
            # The element starts at the origin, so its opening relative move is the same
            # point as an absolute one — and only as an absolute one can it follow another
            # element in the same geometry.
            if command == "m":
                emit = "M"
            first = False

        out.append(emit + ",".join(num(a) if k not in (3, 4) or command.upper() != "A" else a
                                   for k, a in enumerate(args)))

    return " ".join(out)


def shape_to_path(element):
    tag = element.tag.replace(NS, "")
    a = element.attrib

    if tag == "path":
        return a["d"]
    if tag == "line":
        return f"M{a['x1']} {a['y1']} L{a['x2']} {a['y2']}"
    if tag in ("polyline", "polygon"):
        points = re.findall(r"[-+]?(?:\d+\.?\d*|\.\d+)", a["points"])
        pairs = [f"{points[k]} {points[k + 1]}" for k in range(0, len(points), 2)]
        return "M" + " L".join(pairs) + (" Z" if tag == "polygon" else "")
    if tag in ("circle", "ellipse"):
        cx, cy = float(a["cx"]), float(a["cy"])
        rx = float(a.get("r", a.get("rx", 0)))
        ry = float(a.get("r", a.get("ry", 0)))
        return (f"M{cx - rx} {cy} A{rx} {ry} 0 1 1 {cx + rx} {cy} "
                f"A{rx} {ry} 0 1 1 {cx - rx} {cy} Z")
    if tag == "rect":
        x, y = float(a.get("x", 0)), float(a.get("y", 0))
        w, h = float(a["width"]), float(a["height"])
        r = float(a.get("rx", a.get("ry", 0)))
        if r == 0:
            return f"M{x} {y} H{x + w} V{y + h} H{x} Z"
        return (f"M{x + r} {y} H{x + w - r} A{r} {r} 0 0 1 {x + w} {y + r} V{y + h - r} "
                f"A{r} {r} 0 0 1 {x + w - r} {y + h} H{x + r} A{r} {r} 0 0 1 {x} {y + h - r} "
                f"V{y + r} A{r} {r} 0 0 1 {x + r} {y} Z")
    if tag == "g":
        if "transform" in a:
            raise SystemExit("transforms are not supported; bake the coordinates instead")
        return " ".join(shape_to_path(child) for child in element)
    raise SystemExit(f"unsupported element <{tag}>")


def convert(path):
    root = ET.parse(path).getroot()
    if root.attrib.get("viewBox", "0 0 24 24") != "0 0 24 24":
        raise SystemExit(f"{path}: expected a 24x24 viewBox")
    parts = [normalise_path(shape_to_path(child)) for child in root
             if child.tag.replace(NS, "") != "title"]
    return " ".join(parts)


def arc_to_cubics(x0, y0, rx, ry, rotation, large_arc, sweep, x1, y1):
    """
    SVG endpoint-parameterised arc to a list of cubic Bezier segments.

    AppKit has no endpoint-arc API -- NSBezierPath draws arcs from a centre, an angle and a
    radius -- so the conversion has to happen somewhere. It happens here rather than in the
    Mac renderer because it is arithmetic, not drawing: doing it once at generation time
    keeps the runtime parser to four commands and puts the fiddly part somewhere it can be
    read and diffed. WPF renders arcs natively, so the Windows output is untouched.

    Standard F.6.5 implementation notes from the SVG spec.
    """
    if rx == 0 or ry == 0 or (abs(x1 - x0) < 1e-12 and abs(y1 - y0) < 1e-12):
        return [("L", [x1, y1])]

    rx, ry = abs(rx), abs(ry)
    phi = math.radians(rotation)
    cos_phi, sin_phi = math.cos(phi), math.sin(phi)

    dx2, dy2 = (x0 - x1) / 2.0, (y0 - y1) / 2.0
    x1p = cos_phi * dx2 + sin_phi * dy2
    y1p = -sin_phi * dx2 + cos_phi * dy2

    # Scale the radii up if they are too small to span the two points at all.
    lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry)
    if lam > 1:
        scale = math.sqrt(lam)
        rx, ry = rx * scale, ry * scale

    num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
    den = rx * rx * y1p * y1p + ry * ry * x1p * x1p
    coef = math.sqrt(max(num / den, 0.0))
    if large_arc == sweep:
        coef = -coef

    cxp = coef * rx * y1p / ry
    cyp = -coef * ry * x1p / rx
    cx = cos_phi * cxp - sin_phi * cyp + (x0 + x1) / 2.0
    cy = sin_phi * cxp + cos_phi * cyp + (y0 + y1) / 2.0

    def angle_of(ux, uy):
        return math.atan2(uy, ux)

    theta1 = angle_of((x1p - cxp) / rx, (y1p - cyp) / ry)
    theta2 = angle_of((-x1p - cxp) / rx, (-y1p - cyp) / ry)
    delta = theta2 - theta1

    if sweep == 0 and delta > 0:
        delta -= 2 * math.pi
    elif sweep == 1 and delta < 0:
        delta += 2 * math.pi

    # A cubic approximates at most a quarter turn well, so split accordingly.
    segments = max(1, int(math.ceil(abs(delta) / (math.pi / 2) - 1e-9)))
    step = delta / segments
    alpha = 4.0 / 3.0 * math.tan(step / 4.0)

    out = []
    theta = theta1
    px, py = x0, y0

    for _ in range(segments):
        nxt = theta + step

        def point(t):
            return (cx + rx * math.cos(t) * cos_phi - ry * math.sin(t) * sin_phi,
                    cy + rx * math.cos(t) * sin_phi + ry * math.sin(t) * cos_phi)

        def deriv(t):
            return (-rx * math.sin(t) * cos_phi - ry * math.cos(t) * sin_phi,
                    -rx * math.sin(t) * sin_phi + ry * math.cos(t) * cos_phi)

        ex, ey = point(nxt)
        d1x, d1y = deriv(theta)
        d2x, d2y = deriv(nxt)

        out.append(("C", [px + alpha * d1x, py + alpha * d1y,
                          ex - alpha * d2x, ey - alpha * d2y,
                          ex, ey]))
        px, py = ex, ey
        theta = nxt

    return out


def to_mac_path(d):
    """
    Reduces normalised path data to the four commands the Mac renderer understands:
    M, L, C and Z. H and V become lines, arcs become cubics.
    """
    tokens = TOKEN.findall(d)
    out = []
    i = 0
    x = y = 0.0
    start_x = start_y = 0.0

    while i < len(tokens):
        command = tokens[i]
        i += 1

        if command.upper() == "Z":
            out.append("Z")
            x, y = start_x, start_y
            continue

        arity = ARITY[command.upper()]
        args = [float(a) for a in tokens[i:i + arity]]
        i += arity

        # normalise_path only makes the opening move absolute; everything after it may
        # still be relative, because WPF reads both. Resolve against the current point here.
        upper = command.upper()
        relative = command.islower()

        if upper == "M":
            x, y = (x + args[0], y + args[1]) if relative else (args[0], args[1])
            start_x, start_y = x, y
            out.append(f"M{num(x)},{num(y)}")
        elif upper == "L":
            x, y = (x + args[0], y + args[1]) if relative else (args[0], args[1])
            out.append(f"L{num(x)},{num(y)}")
        elif upper == "H":
            x = x + args[0] if relative else args[0]
            out.append(f"L{num(x)},{num(y)}")
        elif upper == "V":
            y = y + args[0] if relative else args[0]
            out.append(f"L{num(x)},{num(y)}")
        elif upper == "C":
            pts = [(x + args[k] if relative else args[k],
                    y + args[k + 1] if relative else args[k + 1]) for k in (0, 2, 4)]
            out.append("C" + ",".join(f"{num(px)},{num(py)}" for px, py in pts))
            x, y = pts[2]
        elif upper == "A":
            ex = x + args[5] if relative else args[5]
            ey = y + args[6] if relative else args[6]
            for kind, values in arc_to_cubics(x, y, args[0], args[1], args[2],
                                              int(args[3]), int(args[4]), ex, ey):
                out.append(kind + ",".join(num(v) for v in values))
                x, y = values[-2], values[-1]
        else:
            raise SystemExit(f"unexpected command {command} after normalisation")

    return " ".join(out)


def pascal(file_name):
    stem = os.path.splitext(file_name)[0]
    return "".join(part[:1].upper() + part[1:] for part in stem.split("-"))


def write_windows(files, paths):
    lines = [
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        "    <!--",
        "        GENERATED by Common/Icons/generate.py from the SVGs in that folder. Do not",
        "        edit by hand: change or add an SVG and run the script. Lucide icons, ISC licence",
        "        (Common/Icons/LICENSE); heart-shuffle is Yinyue's own. Drawn by the Icon",
        "        control as a 2-unit round stroke in a 24-unit box.",
        "    -->",
    ]
    for file_name in files:
        lines.append(f'    <Geometry x:Key="{key_for(file_name)}">{paths[file_name]}</Geometry>')
    lines.append("</ResourceDictionary>")

    with open(OUT_WIN, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")
    print(f"wrote {OUT_WIN}: {len(files)} icons")


def write_mac(files, paths):
    lines = [
        "// GENERATED by Common/Icons/generate.py from the SVGs in that folder. Do not edit by",
        "// hand: change or add an SVG and run the script. Lucide icons, ISC licence",
        "// (Common/Icons/LICENSE); heart-shuffle is Yinyue's own.",
        "//",
        "// The same path data the WPF app gets, as strings rather than as XAML geometries.",
        "// Icon.cs strokes them at 2 units in a 24-unit box, so the two apps draw one mark.",
        "",
        "namespace Yinyue.UI",
        "{",
        "    public static class Icons",
        "    {",
    ]
    for file_name in files:
        lines.append(f'        public const string {pascal(file_name)} = "{to_mac_path(paths[file_name])}";')
    lines.append("")
    lines.append("        /// <summary>Every icon by name, for the generated-set test.</summary>")
    lines.append("        public static readonly System.Collections.Generic.IReadOnlyDictionary<string, string> All =")
    lines.append("            new System.Collections.Generic.Dictionary<string, string>")
    lines.append("            {")
    for file_name in files:
        lines.append(f'                ["{pascal(file_name)}"] = {pascal(file_name)},')
    lines.append("            };")
    lines.append("    }")
    lines.append("}")

    os.makedirs(os.path.dirname(OUT_MAC), exist_ok=True)
    with open(OUT_MAC, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")
    print(f"wrote {OUT_MAC}: {len(files)} icons")


def main():
    files = sorted(f for f in os.listdir(HERE) if f.endswith(".svg"))
    paths = {f: convert(os.path.join(HERE, f)) for f in files}

    write_windows(files, paths)
    write_mac(files, paths)


if __name__ == "__main__":
    main()
