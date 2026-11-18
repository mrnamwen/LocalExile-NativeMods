#!/usr/bin/env python3
"""Copy overlay-src/ to an overlay folder as the client expects text files: UTF-16LE with a BOM and CRLF line ends.
usage: encode-overlay.py <overlay-src> <overlay>"""
import pathlib, sys

source, target = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
for path in source.rglob("*"):
    if path.is_file():
        text = path.read_text(encoding="utf-8").replace("\r\n", "\n").replace("\n", "\r\n")
        out = target / path.relative_to(source)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(b"\xff\xfe" + text.encode("utf-16-le"))
        print(out)
