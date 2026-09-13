"""
Generates win/Icons.xaml from the SVGs in this folder.

    python win/Assets/Icons/generate.py

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
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Icons.xaml"))
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


def main():
    files = sorted(f for f in os.listdir(HERE) if f.endswith(".svg"))
    lines = [
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        "    <!--",
        "        GENERATED by win/Assets/Icons/generate.py from the SVGs in that folder. Do not",
        "        edit by hand: change or add an SVG and run the script. Lucide icons, ISC licence",
        "        (win/Assets/Icons/LICENSE); heart-shuffle is Yinyue's own. Drawn by the Icon",
        "        control as a 2-unit round stroke in a 24-unit box.",
        "    -->",
    ]
    for file_name in files:
        lines.append(f'    <Geometry x:Key="{key_for(file_name)}">{convert(os.path.join(HERE, file_name))}</Geometry>')
    lines.append("</ResourceDictionary>")

    with open(OUT, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")
    print(f"wrote {OUT}: {len(files)} icons")


if __name__ == "__main__":
    main()
