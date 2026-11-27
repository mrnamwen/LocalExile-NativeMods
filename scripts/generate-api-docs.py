#!/usr/bin/env python3
"""Deterministic API reference Markdown generator for NativeMods.

Parses public C# types, members, and XML documentation comments directly from:
  - src/LocalExile.Client.Sdk/
  - src/LocalExile.Client.ServerBridge/
  - src/LocalExile.Client.AssetPipeline/
  - src/LocalExile.Client.Waypoint/

When compiled XML documentation files (LocalExile.Client.*.xml) exist under
an optional --xml-dir or build output directory, their member summaries are
used as a fallback/enrichment source while preserving deterministic ordering.

Usage:
  python3 scripts/generate-api-docs.py          # Write docs/api/*.md
  python3 scripts/generate-api-docs.py --check  # Exit non-zero if stale
"""

from __future__ import annotations

import argparse
import dataclasses
import html
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple


REPO_ROOT = Path(__file__).resolve().parent.parent
API_OUT_DIR = REPO_ROOT / "docs" / "api"

ASSEMBLIES: Tuple[Tuple[str, Path], ...] = (
    ("LocalExile.Client.Sdk", REPO_ROOT / "src" / "LocalExile.Client.Sdk"),
    ("LocalExile.Client.ServerBridge", REPO_ROOT / "src" / "LocalExile.Client.ServerBridge"),
    ("LocalExile.Client.AssetPipeline", REPO_ROOT / "src" / "LocalExile.Client.AssetPipeline"),
    ("LocalExile.Client.Waypoint", REPO_ROOT / "src" / "LocalExile.Client.Waypoint"),
)


@dataclasses.dataclass(frozen=True)
class PageSpec:
    slug: str
    title: str
    assembly: str
    description: str
    guide_link: Optional[str] = None
    guide_label: Optional[str] = None


PAGE_SPECS: Tuple[PageSpec, ...] = (
    PageSpec(
        slug="sdk-core",
        title="Client SDK — Core & Address Library",
        assembly="LocalExile.Client.Sdk",
        description="Core mod entry contracts (`IClientMod`, `IClientContext`, `IGameServices`, `SdkVersion`, `ClientModManifest`) and build-matched Address Library types (`IAddressLibrary`, `AddressEntry`, `ClientBuildId`).",
        guide_link="../guides/getting-started.md",
        guide_label="Getting Started Guide",
    ),
    PageSpec(
        slug="sdk-ui-screens",
        title="Client SDK — UI, Stock Screens & Toasts",
        assembly="LocalExile.Client.Sdk",
        description="Native `.ui` windows, typed controls (`IUiService`, `IUiWindow`, `IUiControl`, `IButton`, `ILabel`, `ITextBox`, `ICheckBox`, `IClipboardService`), stock panel & slot management (`IScreensService`, `ScreenSlot`, `GamePanel`), and HUD notifications (`IToastService`).",
        guide_link="../guides/ui-and-screens.md",
        guide_label="UI & Stock Screens Guide",
    ),
    PageSpec(
        slug="sdk-input-hover-pregame",
        title="Client SDK — Input, Keybinds, Hover & Pre-Game",
        assembly="LocalExile.Client.Sdk",
        description="Suppressible keyboard/mouse hooks (`IInputService`, `IKeybindRegistry`, `KeyChord`), hovered and cursor item inspection (`IHoverService`), and pre-game character creation option injection (`IPreGameService`).",
        guide_link="../guides/input-hover-pregame.md",
        guide_label="Input, Hover & Pre-Game Guide",
    ),
    PageSpec(
        slug="sdk-world-filter-stats-camera",
        title="Client SDK — World, Audio, Stats, Lifecycle, Item Filter & Camera",
        assembly="LocalExile.Client.Sdk",
        description="Live `EntityManager` queries & client VFX (`IWorldService`), FMOD 2.03 2D/3D spatial audio (`IAudioService`), live entity/player stats (`IStatsService`), area transitions (`IGameLifecycle`), client item filter integration (`IItemFilterService`), and camera projection/UI coords/zoom (`ICameraService`, `TryWorldToUiPosition`).",
        guide_link="../guides/world-filter-stats-camera.md",
        guide_label="World, Filter, Stats & Camera Guide",
    ),
    PageSpec(
        slug="sdk-net-files-raw-extend",
        title="Client SDK — Networking, Overlays, Protocol, Stacks, Extend & Raw",
        assembly="LocalExile.Client.Sdk",
        description="Typed bidirectional networking (`INetChannel`), GEAL script invocation (`IScriptService`), file overlays (`IFileOverlayService`, `ClientTextEncoding`), opt-in 32-bit stacks (`IStackExtensionService`), wire protocol codecs (`MessageCodec`, `ModEnvelope`), extend registries (`IGealBuiltinRegistry`, `IUiControlRegistry`, `IRulesetRegistry`, `IOpcodeRegistry`, `IContentRegistry`), and gated raw memory/hooks (`IRawMemory`).",
        guide_link="../guides/capabilities-and-limits.md",
        guide_label="Capabilities & Limits Matrix",
    ),
    PageSpec(
        slug="server-bridge",
        title="ServerBridge API Reference",
        assembly="LocalExile.Client.ServerBridge",
        description="Gateway-side dual-mod host (`ClientBridgeHost`, `LocalExileGatewayHost`), Tier 1 `0.3.0`/`0.4.0`/`1.2.0` sessions (`IClientPhase1Session`, `IClientPhase2Session`, `IClientPhase4Session`), server-driven UI (`IClientUiSession`), atomic item transactions (`LocalExileGatewayTransactions`), custom Pet MTX & Currency registrars, and affix manipulation (`LocalExileItemAffixService`).",
        guide_link="../guides/server-bridge.md",
        guide_label="ServerBridge Guide",
    ),
    PageSpec(
        slug="asset-pipeline",
        title="AssetPipeline API Reference",
        assembly="LocalExile.Client.AssetPipeline",
        description="Deterministic 3.29.3d content conversion and `.datc64` row-appending library & CLI (`Bundles2Reader`, `Dat64Table`, `Dat64TablePatcher`, `DatPatchMerger`, `MaxwellDatPatcher`, `CurrencyDatPatcher`, `PoeHashing`, `DdsTextureConverter`, `GlbToFmtConverter`, `GlbToAstConverter`, `GlbToSmdConverter`, `GlbToPoeMeshConverter`, `PoeFmtWriter`, `PoeSmdWriter`, multi-bone `PoeAstWriter`, `AudioTranscoder`).",
        guide_link="../guides/content-and-assets.md",
        guide_label="Content & Assets Guide",
    ),
    PageSpec(
        slug="waypoint",
        title="Waypoint Integration API Reference",
        assembly="LocalExile.Client.Waypoint",
        description="Dual-side `mod.json` manifest models (`DualModManifest`, `NativeClientManifest`), package & `.ui` validation (`DualModPackageValidator`, `UiLayoutLinter`), stored-block `NMB1` bundling (`ModBundlePacker`), SemVer ranges, PE patcher bridge, and installer/extender management (`WaypointModInstaller`, `WaypointExtenderManager`).",
        guide_link="../architecture/waypoint-integration.md",
        guide_label="Waypoint Integration Guide",
    ),
)


def classify_file_to_page(assembly: str, rel_path: str) -> str:
    """Deterministically maps any .cs file under the 4 assemblies to a PageSpec slug."""
    norm = rel_path.replace("\\", "/")
    if assembly == "LocalExile.Client.ServerBridge":
        return "server-bridge"
    if assembly == "LocalExile.Client.AssetPipeline":
        return "asset-pipeline"
    if assembly == "LocalExile.Client.Waypoint":
        return "waypoint"
    if assembly == "LocalExile.Client.Sdk":
        if norm in ("Core.cs",) or norm.startswith("Addresses/"):
            return "sdk-core"
        if norm in ("Engine/UiTypes.cs", "Engine/ScreenTypes.cs", "Engine/ToastTypes.cs"):
            return "sdk-ui-screens"
        if norm in ("Engine/InputTypes.cs", "Engine/HoverTypes.cs", "Engine/PreGameTypes.cs"):
            return "sdk-input-hover-pregame"
        if norm in (
            "Engine/WorldAudioStatsLifecycle.cs",
            "Engine/ItemFilterTypes.cs",
            "Engine/CameraTypes.cs",
        ):
            return "sdk-world-filter-stats-camera"
        if (
            norm.startswith("Protocol/")
            or norm.startswith("Extend/")
            or norm.startswith("Raw/")
            or norm.startswith("Engine/FileOverlay")
            or norm.startswith("Engine/NetAndScript")
        ):
            return "sdk-net-files-raw-extend"
        # Forward-compatible deterministic fallback for new SDK files
        lower = norm.lower()
        if any(k in lower for k in ("ui", "screen", "toast")):
            return "sdk-ui-screens"
        if any(k in lower for k in ("input", "hover", "pregame", "keybind")):
            return "sdk-input-hover-pregame"
        if any(k in lower for k in ("world", "audio", "stat", "filter", "camera", "lifecycle")):
            return "sdk-world-filter-stats-camera"
        return "sdk-net-files-raw-extend"
    raise ValueError(f"Unknown assembly: {assembly}")


# ---------------------------------------------------------------------------
# XML Doc Comment Parsing & Formatting
# ---------------------------------------------------------------------------

@dataclasses.dataclass
class DocComment:
    summary: str = ""
    remarks: str = ""
    returns: str = ""
    params: Dict[str, str] = dataclasses.field(default_factory=dict)


def _inline_xml_to_markdown(elem: ET.Element) -> str:
    parts: List[str] = []
    if elem.text:
        parts.append(elem.text)
    for child in elem:
        tag = child.tag.lower()
        if tag == "c":
            code_txt = "".join(child.itertext()).strip()
            parts.append(f"`{code_txt}`")
        elif tag in ("see", "seealso"):
            cref = child.attrib.get("cref") or child.attrib.get("langword") or child.attrib.get("href") or ""
            label = "".join(child.itertext()).strip()
            if not label:
                # Strip XML doc prefix like T:, M:, P:, F:, E: and namespace clutter when short
                cleaned = re.sub(r"^[A-Z]:", "", cref)
                cleaned = cleaned.replace("{", "<").replace("}", ">")
                label = cleaned.split(".")[-1] if "." in cleaned and "(" not in cleaned else cleaned
            parts.append(f"`{label}`" if label else "")
        elif tag in ("paramref", "typeparamref"):
            name = child.attrib.get("name", "")
            parts.append(f"`{name}`" if name else "")
        elif tag == "para":
            inner = _inline_xml_to_markdown(child).strip()
            if inner:
                parts.append("\n\n" + inner + "\n\n")
        else:
            parts.append(_inline_xml_to_markdown(child))
        if child.tail:
            parts.append(child.tail)
    return "".join(parts)


def _normalize_prose(text: str) -> str:
    paragraphs = [p.strip() for p in re.split(r"\n\s*\n", text) if p.strip()]
    cleaned = [re.sub(r"\s+", " ", p) for p in paragraphs]
    return " ".join(cleaned).strip()


def parse_xml_doc_lines(lines: Sequence[str]) -> DocComment:
    if not lines:
        return DocComment()
    stripped = []
    for raw in lines:
        s = raw.strip()
        if s.startswith("///"):
            s = s[3:]
            if s.startswith(" "):
                s = s[1:]
        stripped.append(s)
    xml_body = "\n".join(stripped).strip()
    if not xml_body:
        return DocComment()

    doc = DocComment()
    try:
        root = ET.fromstring(f"<docroot>{xml_body}</docroot>")
    except ET.ParseError:
        # Graceful fallback: strip tags and unescape entities
        plain = re.sub(r"<[^>]+>", "", xml_body)
        doc.summary = _normalize_prose(html.unescape(plain))
        return doc

    summary_el = root.find("summary")
    if summary_el is not None:
        doc.summary = _normalize_prose(_inline_xml_to_markdown(summary_el))
    else:
        # Handle doc comments without explicit <summary> wrapper
        direct = _normalize_prose(_inline_xml_to_markdown(root))
        if direct and not list(root):
            doc.summary = direct

    remarks_el = root.find("remarks")
    if remarks_el is not None:
        doc.remarks = _normalize_prose(_inline_xml_to_markdown(remarks_el))

    returns_el = root.find("returns")
    if returns_el is not None:
        doc.returns = _normalize_prose(_inline_xml_to_markdown(returns_el))

    for p_el in root.findall("param"):
        pname = p_el.attrib.get("name", "").strip()
        if pname:
            doc.params[pname] = _normalize_prose(_inline_xml_to_markdown(p_el))

    return doc


def load_compiled_xml_docs(search_roots: Sequence[Path]) -> Dict[str, DocComment]:
    """Loads any compiled C# XML documentation files (.xml) if present."""
    docs: Dict[str, DocComment] = {}
    xml_files: List[Path] = []
    for root in search_roots:
        if not root.exists():
            continue
        for p in sorted(root.rglob("LocalExile.Client.*.xml")):
            xml_files.append(p)
    for xml_path in xml_files:
        try:
            tree = ET.parse(xml_path)
            root = tree.getroot()
        except ET.ParseError:
            continue
        members_el = root.find("members")
        if members_el is None:
            continue
        for m in members_el.findall("member"):
            name = m.attrib.get("name", "")
            if not name:
                continue
            dc = DocComment()
            s = m.find("summary")
            if s is not None:
                dc.summary = _normalize_prose(_inline_xml_to_markdown(s))
            r = m.find("returns")
            if r is not None:
                dc.returns = _normalize_prose(_inline_xml_to_markdown(r))
            for p_el in m.findall("param"):
                pn = p_el.attrib.get("name", "").strip()
                if pn:
                    dc.params[pn] = _normalize_prose(_inline_xml_to_markdown(p_el))
            docs[name] = dc
    return docs


# ---------------------------------------------------------------------------
# Lightweight C# Tokenizer & Public API Extractor
# ---------------------------------------------------------------------------

@dataclasses.dataclass
class MemberInfo:
    kind: str  # "constructor" | "constant" | "field" | "property" | "event" | "method" | "enum_value"
    name: str
    signature: str
    doc: DocComment
    line: int


@dataclasses.dataclass
class TypeInfo:
    assembly: str
    namespace: str
    source_rel_path: str
    kind: str  # "interface" | "class" | "record" | "record struct" | "struct" | "enum" | "delegate"
    name: str
    full_name: str
    signature: str
    attributes: List[str]
    doc: DocComment
    primary_params: List[Tuple[str, str]]  # (param_decl, param_name)
    members: List[MemberInfo]
    line: int
    page_slug: str


@dataclasses.dataclass
class Token:
    kind: str  # "IDENT" | "SYM" | "STR" | "DOC" | "PP"
    value: str
    line: int


def tokenize_cs(source: str) -> List[Token]:
    """Tokenizes C# source preserving /// doc comments and handling strings/comments accurately."""
    tokens: List[Token] = []
    n = len(source)
    i = 0
    line = 1

    while i < n:
        ch = source[i]

        # Whitespace
        if ch in " \t\r":
            i += 1
            continue
        if ch == "\n":
            line += 1
            i += 1
            continue

        # Preprocessor directive at line start (we skip line)
        if ch == "#":
            j = source.find("\n", i)
            if j == -1:
                break
            i = j + 1
            line += 1
            continue

        # Line / XML doc comment
        if ch == "/" and i + 1 < n and source[i + 1] == "/":
            j = source.find("\n", i)
            end = n if j == -1 else j
            text = source[i:end]
            if text.startswith("///") and not text.startswith("////"):
                tokens.append(Token("DOC", text, line))
            if j == -1:
                break
            i = j + 1
            line += 1
            continue

        # Block comment
        if ch == "/" and i + 1 < n and source[i + 1] == "*":
            end = source.find("*/", i + 2)
            if end == -1:
                break
            line += source.count("\n", i, end + 2)
            i = end + 2
            continue

        # Raw string literal (""" or $""" or $$""")
        if (ch == '"' and source[i:i + 3] == '"""') or (
            ch == "$" and i + 1 < n and source[i:i + 4].lstrip("$").startswith('"""')
        ):
            start = i
            start_line = line
            while i < n and source[i] == "$":
                i += 1
            qcount = 0
            while i < n and source[i] == '"':
                qcount += 1
                i += 1
            closer = '"' * qcount
            end = source.find(closer, i)
            if end == -1:
                end = n
            else:
                end += qcount
            line += source.count("\n", start, end)
            tokens.append(Token("STR", source[start:end], start_line))
            i = end
            continue

        # Verbatim / interpolated string prefixes: @"...", $@"...", @$"...", $"..."
        if (
            (ch in ('@', '$') and i + 1 < n and source[i + 1] == '"')
            or (source[i:i + 2] in ('$@', '@$') and i + 2 < n and source[i + 2] == '"')
        ):
            start = i
            start_line = line
            is_verbatim = "@" in source[i:i + 2]
            while i < n and source[i] in "@$":
                i += 1
            i += 1  # opening "
            while i < n:
                c = source[i]
                if c == "\n":
                    line += 1
                if is_verbatim:
                    if c == '"':
                        if i + 1 < n and source[i + 1] == '"':
                            i += 2
                            continue
                        i += 1
                        break
                else:
                    if c == "\\" and i + 1 < n:
                        i += 2
                        continue
                    if c == '"':
                        i += 1
                        break
                i += 1
            tokens.append(Token("STR", source[start:i], start_line))
            continue

        # Standard string literal
        if ch == '"':
            start = i
            start_line = line
            i += 1
            while i < n:
                c = source[i]
                if c == "\n":
                    line += 1
                if c == "\\" and i + 1 < n:
                    i += 2
                    continue
                if c == '"':
                    i += 1
                    break
                i += 1
            tokens.append(Token("STR", source[start:i], start_line))
            continue

        # Char literal
        if ch == "'":
            start = i
            start_line = line
            i += 1
            while i < n:
                c = source[i]
                if c == "\\" and i + 1 < n:
                    i += 2
                    continue
                if c == "'":
                    i += 1
                    break
                i += 1
            tokens.append(Token("STR", source[start:i], start_line))
            continue

        # Identifier or keyword (including @keyword)
        if ch.isalpha() or ch == "_" or (ch == "@" and i + 1 < n and (source[i + 1].isalpha() or source[i + 1] == "_")):
            start = i
            if ch == "@":
                i += 1
            while i < n and (source[i].isalnum() or source[i] == "_"):
                i += 1
            tokens.append(Token("IDENT", source[start:i], line))
            continue

        # Number literal (hex, float, underscores, suffixes)
        if ch.isdigit():
            start = i
            while i < n and (source[i].isalnum() or source[i] in "_."):
                if source[i] == "." and (i + 1 >= n or not source[i + 1].isdigit()):
                    break
                i += 1
            tokens.append(Token("IDENT", source[start:i], line))
            continue

        # Multi-character symbols
        two = source[i:i + 2]
        if two in ("=>", "<<", ">>", "==", "!=", "<=", ">=", "&&", "||", "??", "::", ".."):
            tokens.append(Token("SYM", two, line))
            i += 2
            continue

        tokens.append(Token("SYM", ch, line))
        i += 1

    return tokens


def _format_tokens(toks: Sequence[Token]) -> str:
    """Renders a slice of C# tokens into clean normalized C# signature text."""
    out: List[str] = []
    no_space_before = {",", ";", ")", "]", ".", "?", ":"}
    no_space_after = {"(", "[", ".", "!", "~"}

    prev: Optional[Token] = None
    angle_depth = 0
    for idx, t in enumerate(toks):
        if t.kind == "DOC":
            continue
        v = t.value
        next_t = toks[idx + 1] if idx + 1 < len(toks) else None

        if not out:
            out.append(v)
            if v == "<":
                angle_depth += 1
            prev = t
            continue

        pv = prev.value if prev else ""

        # Decide whether to insert space
        need_space = True
        if v in no_space_before:
            need_space = False
        elif pv in no_space_after:
            need_space = False
        elif v == "<" and prev and (prev.kind == "IDENT" or pv in (">", "]")):
            # Generic opening <
            need_space = False
            angle_depth += 1
        elif pv == "<" and angle_depth > 0:
            need_space = False
        elif v == ">" and angle_depth > 0:
            need_space = False
            angle_depth -= 1
        elif v == "(" and prev and (prev.kind == "IDENT" or pv == ">"):
            # Method/ctor call or parameter list unless after a keyword
            if pv not in ("new", "return", "where", "in", "out", "ref", "params", "static", "readonly", "public", "event", "delegate"):
                need_space = False
        elif v == "[" and prev and (prev.kind == "IDENT" or pv in (">", "?", "]")):
            if pv not in ("params", "return", "=>", "="):
                need_space = False
        elif v == ":" and pv != "":
            # Base type list ` : `
            need_space = True
        elif pv == ":" and v != "":
            need_space = True
        elif pv == "?":
            need_space = v not in (".", ",", ")", "]", ">", "[")
        elif pv == ",":
            need_space = True
        elif v == "=>" or pv == "=>":
            need_space = True
        elif v == "=" or pv == "=":
            need_space = True
        elif v == "|" or pv == "|" or v == "<<" or pv == "<<":
            need_space = True
        elif pv == ")" and v == "(":
            need_space = False

        if need_space:
            out.append(" ")
        out.append(v)
        prev = t

    text = "".join(out)
    text = re.sub(r"\s+", " ", text).strip()
    # Clean up common spacing quirks
    text = re.sub(r"\s+,", ",", text)
    text = re.sub(r"\(\s+", "(", text)
    text = re.sub(r"\s+\)", ")", text)
    text = re.sub(r"\[\s+\]", "[]", text)
    text = re.sub(r"\s+\?", "?", text)
    return text


def _find_matching(tokens: Sequence[Token], start: int, open_sym: str, close_sym: str) -> int:
    depth = 0
    i = start
    while i < len(tokens):
        v = tokens[i].value
        if tokens[i].kind == "SYM":
            if v == open_sym:
                depth += 1
            elif v == close_sym:
                depth -= 1
                if depth == 0:
                    return i
        i += 1
    return len(tokens) - 1


def _split_top_level(tokens: Sequence[Token], sep: str = ",") -> List[List[Token]]:
    parts: List[List[Token]] = []
    cur: List[Token] = []
    paren = bracket = brace = angle = 0
    for t in tokens:
        if t.kind == "DOC":
            continue
        if t.kind == "SYM":
            if t.value == "(":
                paren += 1
            elif t.value == ")":
                paren = max(0, paren - 1)
            elif t.value == "[":
                bracket += 1
            elif t.value == "]":
                bracket = max(0, bracket - 1)
            elif t.value == "{":
                brace += 1
            elif t.value == "}":
                brace = max(0, brace - 1)
            elif t.value == "<":
                angle += 1
            elif t.value == ">":
                angle = max(0, angle - 1)
            elif t.value == sep and paren == 0 and bracket == 0 and brace == 0 and angle == 0:
                if cur:
                    parts.append(cur)
                    cur = []
                continue
        cur.append(t)
    if cur:
        parts.append(cur)
    return parts


def _extract_param_name(param_tokens: Sequence[Token]) -> str:
    # Strip default value (= ...)
    core: List[Token] = []
    paren = bracket = angle = 0
    for t in param_tokens:
        if t.kind == "SYM":
            if t.value == "(":
                paren += 1
            elif t.value == ")":
                paren = max(0, paren - 1)
            elif t.value == "[":
                bracket += 1
            elif t.value == "]":
                bracket = max(0, bracket - 1)
            elif t.value == "<":
                angle += 1
            elif t.value == ">":
                angle = max(0, angle - 1)
            elif t.value == "=" and paren == 0 and bracket == 0 and angle == 0:
                break
        core.append(t)
    for t in reversed(core):
        if t.kind == "IDENT":
            return t.value.lstrip("@")
    return ""


def _consume_docs_and_attrs(tokens: Sequence[Token], i: int, end: int) -> Tuple[int, List[str], List[str]]:
    docs: List[str] = []
    attrs: List[str] = []
    while i < end:
        if tokens[i].kind == "DOC":
            docs.append(tokens[i].value)
            i += 1
        elif tokens[i].kind == "SYM" and tokens[i].value == "[":
            close = _find_matching(tokens, i, "[", "]")
            attrs.append(_format_tokens(tokens[i:close + 1]))
            i = close + 1
        else:
            break
    return i, docs, attrs


TYPE_KEYWORDS = {"class", "interface", "struct", "enum", "record", "delegate"}
MODIFIER_KEYWORDS = {
    "public", "private", "protected", "internal",
    "static", "sealed", "abstract", "partial", "readonly", "ref",
    "new", "virtual", "override", "extern", "unsafe", "async", "const", "volatile", "required", "file",
}


def _parse_enum_body(tokens: Sequence[Token], start: int, end: int) -> List[MemberInfo]:
    members: List[MemberInfo] = []
    i = start
    while i < end:
        i, docs, _attrs = _consume_docs_and_attrs(tokens, i, end)
        if i >= end:
            break
        if tokens[i].kind == "SYM" and tokens[i].value == ",":
            i += 1
            continue
        item_start = i
        item_line = tokens[i].line
        item_toks: List[Token] = []
        while i < end:
            if tokens[i].kind == "SYM" and tokens[i].value == ",":
                i += 1
                break
            if tokens[i].kind != "DOC":
                item_toks.append(tokens[i])
            i += 1
        if item_toks and item_toks[0].kind == "IDENT":
            name = item_toks[0].value
            sig = _format_tokens(item_toks)
            members.append(MemberInfo(
                kind="enum_value",
                name=name,
                signature=sig,
                doc=parse_xml_doc_lines(docs),
                line=item_line,
            ))
    return members


def _parse_type_members(
    tokens: Sequence[Token],
    body_start: int,
    body_end: int,
    parent_type: TypeInfo,
    assembly: str,
    namespace: str,
    rel_path: str,
    page_slug: str,
    nested_types_out: List[TypeInfo],
) -> List[MemberInfo]:
    members: List[MemberInfo] = []
    is_interface = parent_type.kind == "interface"
    short_parent_name = parent_type.name.split("<")[0]

    i = body_start
    while i < body_end:
        i, docs, attrs = _consume_docs_and_attrs(tokens, i, body_end)
        if i >= body_end:
            break
        if tokens[i].kind == "SYM" and tokens[i].value == ";":
            i += 1
            continue

        decl_start = i
        decl_line = tokens[i].line

        # Collect modifiers
        mods: List[str] = []
        j = i
        while j < body_end and tokens[j].kind == "IDENT" and tokens[j].value in MODIFIER_KEYWORDS:
            mods.append(tokens[j].value)
            j += 1

        # Check visibility
        has_access_mod = any(m in ("public", "private", "protected", "internal") for m in mods)
        is_public = ("public" in mods) or (is_interface and not has_access_mod)

        # Check if nested type declaration
        if j < body_end and tokens[j].kind == "IDENT" and tokens[j].value in TYPE_KEYWORDS:
            nested, next_i = _parse_type_decl(
                tokens, decl_start, body_end, docs, attrs,
                assembly, namespace, rel_path, page_slug,
                enclosing_name=parent_type.name,
                nested_types_out=nested_types_out,
            )
            if nested is not None and is_public:
                nested_types_out.append(nested)
            i = next_i
            continue

        # Scan to declaration terminator (';' or '{' or '=>') at depth 0
        k = j
        paren = bracket = angle = 0
        term_kind = ""
        term_idx = body_end
        while k < body_end:
            tk = tokens[k]
            if tk.kind == "SYM":
                if tk.value == "(":
                    paren += 1
                elif tk.value == ")":
                    paren = max(0, paren - 1)
                elif tk.value == "[":
                    bracket += 1
                elif tk.value == "]":
                    bracket = max(0, bracket - 1)
                elif tk.value == "<" and paren == 0 and bracket == 0:
                    angle += 1
                elif tk.value == ">" and paren == 0 and bracket == 0 and angle > 0:
                    angle -= 1
                elif paren == 0 and bracket == 0 and angle == 0:
                    if tk.value in (";", "{", "=>"):
                        term_kind = tk.value
                        term_idx = k
                        break
            k += 1

        header_toks = [t for t in tokens[decl_start:term_idx] if t.kind != "DOC"]

        # Advance i past the full member body
        accessor_summary = ""
        if term_kind == ";":
            i = term_idx + 1
        elif term_kind == "=>":
            # Scan until ';' at depth 0
            m = term_idx + 1
            p2 = b2 = c2 = 0
            while m < body_end:
                tv = tokens[m].value
                if tokens[m].kind == "SYM":
                    if tv == "(":
                        p2 += 1
                    elif tv == ")":
                        p2 = max(0, p2 - 1)
                    elif tv == "[":
                        b2 += 1
                    elif tv == "]":
                        b2 = max(0, b2 - 1)
                    elif tv == "{":
                        c2 += 1
                    elif tv == "}":
                        c2 = max(0, c2 - 1)
                    elif tv == ";" and p2 == 0 and b2 == 0 and c2 == 0:
                        m += 1
                        break
                m += 1
            i = m
        elif term_kind == "{":
            close_brace = _find_matching(tokens, term_idx, "{", "}")
            # Check if this is a property/indexer/event block vs method body
            # Method/constructor has '(' before '{' (excluding attribute/generic)
            inner_toks = tokens[term_idx + 1:close_brace]
            i = close_brace + 1
            # Optional property initializer `= ...;` after `{ get; set; }`
            if i < body_end and tokens[i].kind == "SYM" and tokens[i].value == "=":
                m = i + 1
                p2 = b2 = c2 = 0
                while m < body_end:
                    tv = tokens[m].value
                    if tokens[m].kind == "SYM":
                        if tv == "(":
                            p2 += 1
                        elif tv == ")":
                            p2 = max(0, p2 - 1)
                        elif tv == "[":
                            b2 += 1
                        elif tv == "]":
                            b2 = max(0, b2 - 1)
                        elif tv == "{":
                            c2 += 1
                        elif tv == "}":
                            c2 = max(0, c2 - 1)
                        elif tv == ";" and p2 == 0 and b2 == 0 and c2 == 0:
                            m += 1
                            break
                    m += 1
                i = m

            # Summarize property accessors if applicable
            acc_parts: List[str] = []
            for idx_a, at in enumerate(inner_toks):
                if at.kind == "IDENT" and at.value in ("get", "set", "init", "add", "remove"):
                    # Only top-level inside property block
                    prev_a = inner_toks[idx_a - 1].value if idx_a > 0 else ""
                    if prev_a not in ("private", "internal", "protected"):
                        if at.value not in acc_parts:
                            acc_parts.append(at.value)
            if acc_parts:
                accessor_summary = "{ " + " ".join(f"{a};" for a in acc_parts) + " }"
        else:
            i = body_end

        if not is_public or not header_toks:
            continue

        # Skip explicit interface implementations (e.g. `void IDisposable.Dispose()`)
        # Classify member kind
        member = _classify_member(header_toks, mods, term_kind, accessor_summary, short_parent_name, docs, decl_line, is_interface)
        if member is not None:
            members.append(member)

    return members


def _classify_member(
    header_toks: Sequence[Token],
    mods: Sequence[str],
    term_kind: str,
    accessor_summary: str,
    short_parent_name: str,
    docs: Sequence[str],
    line: int,
    is_interface: bool,
) -> Optional[MemberInfo]:
    doc = parse_xml_doc_lines(docs)

    # Strip leading attributes inside header if any
    # Event: contains `event` keyword before name
    tok_vals = [t.value for t in header_toks]
    if "event" in tok_vals:
        # Remove `= ...` if event has initializer
        clean_toks = _strip_initializer(header_toks)
        name = _last_ident(clean_toks)
        sig = _format_tokens(clean_toks)
        return MemberInfo("event", name, sig, doc, line)

    # Constant
    if "const" in mods:
        sig = _format_tokens(header_toks)
        clean_toks = _strip_initializer(header_toks)
        name = _last_ident(clean_toks)
        return MemberInfo("constant", name, sig, doc, line)

    # Check whether header has a top-level '(' for method/ctor/operator
    paren_idx = _find_top_level_paren(header_toks)
    if paren_idx != -1:
        # Ignore tuple return type `(ulong Count, ulong Offset) MethodName(...)` — find the LAST top-level `(`
        last_paren_idx = _find_last_top_level_paren(header_toks)
        # Check if there is an '=' before last_paren_idx (e.g., field initializer `public static readonly X Y = new(...)`)
        eq_idx = _find_top_level_sym(header_toks, "=")
        if eq_idx != -1 and eq_idx < last_paren_idx:
            # It's a field or property with initializer!
            pass
        else:
            # Check if it's an indexer `this[...]` (which has `[` not `(`)
            # Constructor check: token right before `(` is parent type name and no return type
            before_paren = header_toks[:last_paren_idx]
            # Strip `where ...` and `: base(...)` / `: this(...)` from signature
            method_sig_toks = _strip_ctor_base_init(header_toks)
            sig = _format_tokens(method_sig_toks)

            # Identify method name (token before `(` or before `<T>(`)
            name_idx = last_paren_idx - 1
            if name_idx >= 0 and header_toks[name_idx].value == ">":
                # Skip back over generic params `<...>`
                depth = 1
                name_idx -= 1
                while name_idx >= 0 and depth > 0:
                    if header_toks[name_idx].value == ">":
                        depth += 1
                    elif header_toks[name_idx].value == "<":
                        depth -= 1
                    name_idx -= 1
            name = header_toks[name_idx].value if name_idx >= 0 else ""

            # Check for explicit operator
            if "operator" in [t.value for t in before_paren]:
                op_idx = [t.value for t in before_paren].index("operator")
                op_name = "operator " + _format_tokens(before_paren[op_idx + 1:])
                return MemberInfo("method", op_name, sig, doc, line)

            # Check for constructor
            non_mod_before = [t for t in before_paren if not (t.kind == "IDENT" and t.value in MODIFIER_KEYWORDS)]
            if len(non_mod_before) == 1 and non_mod_before[0].value == short_parent_name:
                return MemberInfo("constructor", short_parent_name, sig, doc, line)

            # Skip explicit interface implementation (contains '.' right before name)
            if name_idx >= 1 and header_toks[name_idx - 1].value == ".":
                return None

            return MemberInfo("method", name, sig, doc, line)

    # Indexer `this[...]`
    if "this" in tok_vals and "[" in tok_vals:
        sig = _format_tokens(header_toks)
        if accessor_summary:
            sig = f"{sig} {accessor_summary}"
        elif term_kind == "=>":
            sig = f"{sig} {{ get; }}"
        return MemberInfo("property", "this[]", sig, doc, line)

    # Property vs Field
    if term_kind == "{" or term_kind == "=>":
        sig = _format_tokens(header_toks)
        if accessor_summary:
            sig = f"{sig} {accessor_summary}"
        elif term_kind == "=>":
            sig = f"{sig} {{ get; }}"
        name = _last_ident(header_toks)
        return MemberInfo("property", name, sig, doc, line)

    # Field (ends with `;`, possibly with `= value`)
    clean_toks = _strip_initializer(header_toks)
    name = _last_ident(clean_toks)
    # Keep short initializers for static readonly/primitives, omit bulky `new HashSet...`
    full_sig = _format_tokens(header_toks)
    if len(full_sig) > 120 or "new " in full_sig:
        sig = _format_tokens(clean_toks)
    else:
        sig = full_sig
    return MemberInfo("field", name, sig, doc, line)


def _strip_initializer(toks: Sequence[Token]) -> List[Token]:
    out: List[Token] = []
    paren = bracket = angle = 0
    for t in toks:
        if t.kind == "SYM":
            if t.value == "(":
                paren += 1
            elif t.value == ")":
                paren = max(0, paren - 1)
            elif t.value == "[":
                bracket += 1
            elif t.value == "]":
                bracket = max(0, bracket - 1)
            elif t.value == "<":
                angle += 1
            elif t.value == ">":
                angle = max(0, angle - 1)
            elif t.value == "=" and paren == 0 and bracket == 0 and angle == 0:
                break
        out.append(t)
    return out


def _strip_ctor_base_init(toks: Sequence[Token]) -> List[Token]:
    out: List[Token] = []
    paren = bracket = angle = 0
    for idx, t in enumerate(toks):
        if t.kind == "SYM":
            if t.value == "(":
                paren += 1
            elif t.value == ")":
                paren = max(0, paren - 1)
            elif t.value == "[":
                bracket += 1
            elif t.value == "]":
                bracket = max(0, bracket - 1)
            elif t.value == "<":
                angle += 1
            elif t.value == ">":
                angle = max(0, angle - 1)
            elif t.value == ":" and paren == 0 and bracket == 0 and angle == 0:
                if idx + 1 < len(toks) and toks[idx + 1].value in ("base", "this"):
                    break
        out.append(t)
    return out


def _find_top_level_sym(toks: Sequence[Token], sym: str) -> int:
    paren = bracket = angle = 0
    for idx, t in enumerate(toks):
        if t.kind == "SYM":
            if t.value == "(":
                paren += 1
            elif t.value == ")":
                paren = max(0, paren - 1)
            elif t.value == "[":
                bracket += 1
            elif t.value == "]":
                bracket = max(0, bracket - 1)
            elif t.value == "<":
                angle += 1
            elif t.value == ">":
                angle = max(0, angle - 1)
            elif t.value == sym and paren == 0 and bracket == 0 and angle == 0:
                return idx
    return -1


def _find_top_level_paren(toks: Sequence[Token]) -> int:
    bracket = angle = 0
    for idx, t in enumerate(toks):
        if t.kind == "SYM":
            if t.value == "[":
                bracket += 1
            elif t.value == "]":
                bracket = max(0, bracket - 1)
            elif t.value == "<":
                angle += 1
            elif t.value == ">":
                angle = max(0, angle - 1)
            elif t.value == "(" and bracket == 0 and angle == 0:
                return idx
    return -1


def _find_last_top_level_paren(toks: Sequence[Token]) -> int:
    """Finds the opening `(` of the method/ctor parameter list (skipping tuple return types)."""
    bracket = angle = paren = 0
    last = -1
    for idx, t in enumerate(toks):
        if t.kind == "IDENT" and t.value == "where" and paren == 0 and bracket == 0:
            break
        if t.kind == "SYM":
            if t.value == ":" and paren == 0 and bracket == 0 and angle == 0:
                if idx + 1 < len(toks) and toks[idx + 1].value in ("base", "this"):
                    break
            elif t.value == "[":
                bracket += 1
            elif t.value == "]":
                bracket = max(0, bracket - 1)
            elif t.value == "<" and paren == 0:
                angle += 1
            elif t.value == ">" and paren == 0 and angle > 0:
                angle -= 1
            elif t.value == "(":
                if paren == 0 and bracket == 0 and angle == 0:
                    last = idx
                paren += 1
            elif t.value == ")":
                paren = max(0, paren - 1)
    return last


def _last_ident(toks: Sequence[Token]) -> str:
    for t in reversed(toks):
        if t.kind == "IDENT":
            return t.value.lstrip("@")
    return ""


def _parse_type_decl(
    tokens: Sequence[Token],
    decl_start: int,
    limit: int,
    docs: Sequence[str],
    attrs: Sequence[str],
    assembly: str,
    namespace: str,
    rel_path: str,
    page_slug: str,
    enclosing_name: Optional[str],
    nested_types_out: List[TypeInfo],
) -> Tuple[Optional[TypeInfo], int]:
    decl_line = tokens[decl_start].line
    mods: List[str] = []
    j = decl_start
    while j < limit and tokens[j].kind == "IDENT" and tokens[j].value in MODIFIER_KEYWORDS:
        mods.append(tokens[j].value)
        j += 1

    if j >= limit or tokens[j].kind != "IDENT" or tokens[j].value not in TYPE_KEYWORDS:
        return None, j + 1

    kw = tokens[j].value
    j += 1
    if kw == "record" and j < limit and tokens[j].kind == "IDENT" and tokens[j].value in ("struct", "class"):
        sub = tokens[j].value
        kw = f"record {sub}" if sub == "struct" else "record"
        j += 1

    # Delegate declaration ends at `;`
    if kw == "delegate":
        k = j
        while k < limit and not (tokens[k].kind == "SYM" and tokens[k].value == ";"):
            k += 1
        sig_toks = [t for t in tokens[decl_start:k] if t.kind != "DOC"]
        sig = _format_tokens(sig_toks)
        # Name is identifier before `(` or `<`
        paren_i = _find_top_level_paren(sig_toks)
        name_toks = sig_toks[:paren_i] if paren_i != -1 else sig_toks
        name = _last_ident(name_toks)
        full_name = f"{enclosing_name}.{name}" if enclosing_name else name
        tinfo = TypeInfo(
            assembly=assembly,
            namespace=namespace,
            source_rel_path=rel_path,
            kind="delegate",
            name=full_name,
            full_name=f"{namespace}.{full_name}" if namespace else full_name,
            signature=sig,
            attributes=list(attrs),
            doc=parse_xml_doc_lines(docs),
            primary_params=[],
            members=[],
            line=decl_line,
            page_slug=page_slug,
        )
        return (tinfo if "public" in mods else None), min(k + 1, limit)

    # Type name + optional generic args
    if j >= limit:
        return None, limit
    name_parts: List[Token] = [tokens[j]]
    j += 1
    if j < limit and tokens[j].kind == "SYM" and tokens[j].value == "<":
        close_a = _find_matching(tokens, j, "<", ">")
        name_parts.extend(tokens[j:close_a + 1])
        j = close_a + 1
    raw_name = _format_tokens(name_parts)
    full_short_name = f"{enclosing_name}.{raw_name}" if enclosing_name else raw_name

    # Optional primary constructor `( ... )`
    primary_params: List[Tuple[str, str]] = []
    if j < limit and tokens[j].kind == "SYM" and tokens[j].value == "(":
        close_p = _find_matching(tokens, j, "(", ")")
        for ptoks in _split_top_level(tokens[j + 1:close_p], ","):
            pdecl = _format_tokens(ptoks)
            pname = _extract_param_name(ptoks)
            if pdecl:
                primary_params.append((pdecl, pname))
        j = close_p + 1

    # Scan until `{` or `;` at depth 0
    k = j
    paren = bracket = angle = 0
    term = ""
    while k < limit:
        tk = tokens[k]
        if tk.kind == "SYM":
            if tk.value == "(":
                paren += 1
            elif tk.value == ")":
                paren = max(0, paren - 1)
            elif tk.value == "[":
                bracket += 1
            elif tk.value == "]":
                bracket = max(0, bracket - 1)
            elif tk.value == "<":
                angle += 1
            elif tk.value == ">":
                angle = max(0, angle - 1)
            elif paren == 0 and bracket == 0 and angle == 0 and tk.value in ("{", ";"):
                term = tk.value
                break
        k += 1

    header_toks = [t for t in tokens[decl_start:k] if t.kind != "DOC"]
    sig = _format_tokens(header_toks)

    tinfo = TypeInfo(
        assembly=assembly,
        namespace=namespace,
        source_rel_path=rel_path,
        kind=kw,
        name=full_short_name,
        full_name=f"{namespace}.{full_short_name}" if namespace else full_short_name,
        signature=sig,
        attributes=list(attrs),
        doc=parse_xml_doc_lines(docs),
        primary_params=primary_params,
        members=[],
        line=decl_line,
        page_slug=page_slug,
    )

    if term == ";":
        next_i = k + 1
    elif term == "{":
        close_b = _find_matching(tokens, k, "{", "}")
        next_i = close_b + 1
        if "public" in mods:
            if kw == "enum":
                tinfo.members = _parse_enum_body(tokens, k + 1, close_b)
            else:
                tinfo.members = _parse_type_members(
                    tokens, k + 1, close_b, tinfo,
                    assembly, namespace, rel_path, page_slug,
                    nested_types_out,
                )
    else:
        next_i = limit

    return (tinfo if "public" in mods else None), next_i


def parse_cs_file(assembly: str, root_dir: Path, cs_path: Path) -> List[TypeInfo]:
    source = cs_path.read_text(encoding="utf-8-sig")
    rel_path = cs_path.relative_to(root_dir).as_posix()
    page_slug = classify_file_to_page(assembly, rel_path)
    tokens = tokenize_cs(source)

    types: List[TypeInfo] = []
    current_ns = ""
    i = 0
    n = len(tokens)

    def parse_scope(start: int, end: int, ns: str) -> None:
        idx = start
        cur_ns = ns
        while idx < end:
            idx, docs, attrs = _consume_docs_and_attrs(tokens, idx, end)
            if idx >= end:
                break

            tk = tokens[idx]
            # using / global using
            if tk.kind == "IDENT" and tk.value in ("using", "global"):
                while idx < end and not (tokens[idx].kind == "SYM" and tokens[idx].value == ";"):
                    idx += 1
                idx += 1
                continue

            # namespace Foo.Bar; or namespace Foo.Bar { ... }
            if tk.kind == "IDENT" and tk.value == "namespace":
                idx += 1
                ns_parts: List[str] = []
                while idx < end and not (tokens[idx].kind == "SYM" and tokens[idx].value in (";", "{")):
                    ns_parts.append(tokens[idx].value)
                    idx += 1
                new_ns = "".join(ns_parts)
                if idx < end and tokens[idx].value == ";":
                    cur_ns = new_ns
                    idx += 1
                    continue
                if idx < end and tokens[idx].value == "{":
                    close_b = _find_matching(tokens, idx, "{", "}")
                    parse_scope(idx + 1, close_b, new_ns)
                    idx = close_b + 1
                    continue

            # Type declaration
            look = idx
            while look < end and tokens[look].kind == "IDENT" and tokens[look].value in MODIFIER_KEYWORDS:
                look += 1
            if look < end and tokens[look].kind == "IDENT" and tokens[look].value in TYPE_KEYWORDS:
                nested: List[TypeInfo] = []
                tinfo, next_i = _parse_type_decl(
                    tokens, idx, end, docs, attrs,
                    assembly, cur_ns, rel_path, page_slug,
                    enclosing_name=None,
                    nested_types_out=nested,
                )
                if tinfo is not None:
                    types.append(tinfo)
                    types.extend(nested)
                idx = next_i
                continue

            idx += 1

    parse_scope(i, n, current_ns)
    return types


# ---------------------------------------------------------------------------
# Markdown Rendering
# ---------------------------------------------------------------------------

def _anchor_slug(text: str) -> str:
    """GitHub / MkDocs compatible heading anchor slug."""
    s = text.lower()
    s = re.sub(r"[^a-z0-9\s-]", "", s)
    s = re.sub(r"\s+", "-", s).strip("-")
    return s


def _escape_table_cell(text: str) -> str:
    if not text:
        return "—"
    return text.replace("|", "\\|").replace("\n", " ").strip()


def _enrich_with_xml_docs(types: Sequence[TypeInfo], xml_docs: Dict[str, DocComment]) -> None:
    if not xml_docs:
        return
    for t in types:
        t_key = f"T:{t.full_name}"
        if not t.doc.summary and t_key in xml_docs:
            t.doc = xml_docs[t_key]
        for m in t.members:
            if m.doc.summary:
                continue
            prefix_map = {
                "property": "P",
                "field": "F",
                "constant": "F",
                "enum_value": "F",
                "event": "E",
                "method": "M",
                "constructor": "M",
            }
            pref = prefix_map.get(m.kind, "M")
            exact = f"{pref}:{t.full_name}.{m.name}"
            if exact in xml_docs:
                m.doc = xml_docs[exact]


def render_type_section(t: TypeInfo) -> List[str]:
    lines: List[str] = []
    heading = f"{t.name} ({t.kind})"
    lines.append(f"### `{t.name}`")
    lines.append("")
    meta_parts = [f"**Kind:** `{t.kind}`", f"**Namespace:** `{t.namespace}`", f"**Source:** `{t.assembly}/{t.source_rel_path}`"]
    if t.attributes:
        meta_parts.append("**Attributes:** " + ", ".join(f"`{a}`" for a in t.attributes))
    lines.append(" · ".join(meta_parts))
    lines.append("")
    if t.doc.summary:
        lines.append(t.doc.summary)
        lines.append("")
    if t.doc.remarks:
        lines.append(f"> {t.doc.remarks}")
        lines.append("")

    # Signature code block
    lines.append("```csharp")
    lines.append(t.signature)
    lines.append("```")
    lines.append("")

    # Primary constructor / record parameters
    if t.primary_params:
        lines.append("**Parameters / Properties:**")
        lines.append("")
        lines.append("| Parameter | Description |")
        lines.append("|---|---|")
        for pdecl, pname in t.primary_params:
            pdesc = t.doc.params.get(pname, "")
            lines.append(f"| `{_escape_table_cell(pdecl)}` | {_escape_table_cell(pdesc)} |")
        lines.append("")

    # Enum values
    enum_vals = [m for m in t.members if m.kind == "enum_value"]
    if enum_vals:
        lines.append("**Values:**")
        lines.append("")
        lines.append("| Value | Summary |")
        lines.append("|---|---|")
        for m in enum_vals:
            lines.append(f"| `{_escape_table_cell(m.signature)}` | {_escape_table_cell(m.doc.summary)} |")
        lines.append("")

    # Constants & Fields
    const_fields = [m for m in t.members if m.kind in ("constant", "field")]
    if const_fields:
        lines.append("**Constants & Fields:**")
        lines.append("")
        lines.append("| Member | Summary |")
        lines.append("|---|---|")
        for m in const_fields:
            lines.append(f"| `{_escape_table_cell(m.signature)}` | {_escape_table_cell(m.doc.summary)} |")
        lines.append("")

    # Constructors
    ctors = [m for m in t.members if m.kind == "constructor"]
    if ctors:
        lines.append("**Constructors:**")
        lines.append("")
        lines.append("| Constructor | Summary |")
        lines.append("|---|---|")
        for m in ctors:
            lines.append(f"| `{_escape_table_cell(m.signature)}` | {_escape_table_cell(m.doc.summary)} |")
        lines.append("")

    # Properties
    props = [m for m in t.members if m.kind == "property"]
    if props:
        lines.append("**Properties:**")
        lines.append("")
        lines.append("| Property | Summary |")
        lines.append("|---|---|")
        for m in props:
            lines.append(f"| `{_escape_table_cell(m.signature)}` | {_escape_table_cell(m.doc.summary)} |")
        lines.append("")

    # Events
    events = [m for m in t.members if m.kind == "event"]
    if events:
        lines.append("**Events:**")
        lines.append("")
        lines.append("| Event | Summary |")
        lines.append("|---|---|")
        for m in events:
            lines.append(f"| `{_escape_table_cell(m.signature)}` | {_escape_table_cell(m.doc.summary)} |")
        lines.append("")

    # Methods
    methods = [m for m in t.members if m.kind == "method"]
    if methods:
        lines.append("**Methods:**")
        lines.append("")
        lines.append("| Method | Summary |")
        lines.append("|---|---|")
        for m in methods:
            summary_parts = []
            if m.doc.summary:
                summary_parts.append(m.doc.summary)
            if m.doc.returns:
                summary_parts.append(f"Returns: {m.doc.returns}")
            summary_txt = " ".join(summary_parts)
            lines.append(f"| `{_escape_table_cell(m.signature)}` | {_escape_table_cell(summary_txt)} |")
        lines.append("")

    lines.append("---")
    lines.append("")
    return lines


def render_page(spec: PageSpec, types: Sequence[TypeInfo]) -> str:
    lines: List[str] = [
        "<!-- AUTO-GENERATED by scripts/generate-api-docs.py — DO NOT EDIT BY HAND. -->",
        "<!-- Regenerate with: python3 scripts/generate-api-docs.py -->",
        "",
        f"# {spec.title}",
        "",
        f"> **Assembly:** `{spec.assembly}` · **Public types:** `{len(types)}`",
        ">",
        f"> {spec.description}",
        "",
    ]
    nav_links = ["[← API Reference Index](README.md)"]
    if spec.guide_link and spec.guide_label:
        nav_links.append(f"[{spec.guide_label}]({spec.guide_link})")
    lines.append(" · ".join(nav_links))
    lines.append("")

    # Group types by namespace (preserving deterministic sorted namespace order)
    by_ns: Dict[str, List[TypeInfo]] = {}
    for t in types:
        by_ns.setdefault(t.namespace, []).append(t)

    # Quick table of contents for this page
    lines.append("## Type Summary")
    lines.append("")
    lines.append("| Type | Kind | Namespace | Summary |")
    lines.append("|---|---|---|---|")
    for ns in sorted(by_ns.keys()):
        for t in sorted(by_ns[ns], key=lambda x: (x.source_rel_path, x.line, x.name)):
            anchor = _anchor_slug(t.name)
            lines.append(
                f"| [`{t.name}`](#{anchor}) | `{t.kind}` | `{t.namespace}` | {_escape_table_cell(t.doc.summary)} |"
            )
    lines.append("")

    # Detailed sections per namespace
    for ns in sorted(by_ns.keys()):
        lines.append(f"## Namespace `{ns}`")
        lines.append("")
        for t in sorted(by_ns[ns], key=lambda x: (x.source_rel_path, x.line, x.name)):
            lines.extend(render_type_section(t))

    return "\n".join(lines).rstrip() + "\n"


def render_index(page_types: Dict[str, List[TypeInfo]], all_types: Sequence[TypeInfo]) -> str:
    total_types = len(all_types)
    total_members = sum(len(t.members) + len(t.primary_params) for t in all_types)
    namespaces = sorted({t.namespace for t in all_types})

    lines: List[str] = [
        "<!-- AUTO-GENERATED by scripts/generate-api-docs.py — DO NOT EDIT BY HAND. -->",
        "<!-- Regenerate with: python3 scripts/generate-api-docs.py -->",
        "",
        "# Autogenerated API Reference",
        "",
        f"Complete deterministic API reference generated directly from the C# public declarations and `///` XML documentation comments across all **4 public NativeMods assemblies** (`{total_types}` public types, `{total_members}` public members/parameters, `{len(namespaces)}` namespaces).",
        "",
        "[← Documentation Home](../README.md) · [Capabilities & Limits](../guides/capabilities-and-limits.md) · [Getting Started](../guides/getting-started.md)",
        "",
        "> **Regenerate / Verify in CI:**",
        "> ```bash",
        "> python3 scripts/generate-api-docs.py          # Regenerate docs/api/*.md",
        "> python3 scripts/generate-api-docs.py --check  # Fail if docs/api/*.md is stale",
        "> ```",
        "",
        "## Reference Pages",
        "",
        "| Page | Assembly | Public Types | Covers |",
        "|---|---|---:|---|",
    ]
    for spec in PAGE_SPECS:
        tlist = page_types.get(spec.slug, [])
        lines.append(
            f"| [{spec.title}]({spec.slug}.md) | `{spec.assembly}` | {len(tlist)} | {spec.description} |"
        )
    lines.append("")

    lines.append("## Namespaces")
    lines.append("")
    lines.append("| Namespace | Assembly | Public Types |")
    lines.append("|---|---|---:|")
    for ns in namespaces:
        ns_types = [t for t in all_types if t.namespace == ns]
        asm = ns_types[0].assembly
        lines.append(f"| `{ns}` | `{asm}` | {len(ns_types)} |")
    lines.append("")

    lines.append("## All Public Types (A–Z)")
    lines.append("")
    lines.append("| Type | Kind | Namespace | Page | Summary |")
    lines.append("|---|---|---|---|---|")
    for t in sorted(all_types, key=lambda x: (x.name.lower(), x.namespace.lower())):
        anchor = _anchor_slug(t.name)
        lines.append(
            f"| [`{t.name}`]({t.page_slug}.md#{anchor}) | `{t.kind}` | `{t.namespace}` | [{t.page_slug}.md]({t.page_slug}.md) | {_escape_table_cell(t.doc.summary)} |"
        )
    lines.append("")

    return "\n".join(lines).rstrip() + "\n"


def generate_all(xml_dir: Optional[Path] = None) -> Dict[Path, str]:
    search_roots = [xml_dir] if xml_dir else [p / "bin" for _, p in ASSEMBLIES]
    xml_docs = load_compiled_xml_docs(search_roots)

    all_types: List[TypeInfo] = []
    page_types: Dict[str, List[TypeInfo]] = {spec.slug: [] for spec in PAGE_SPECS}

    for asm_name, asm_dir in ASSEMBLIES:
        if not asm_dir.exists():
            raise FileNotFoundError(f"Expected assembly directory not found: {asm_dir}")
        cs_files = sorted(
            p for p in asm_dir.rglob("*.cs")
            if "obj" not in p.parts and "bin" not in p.parts
        )
        for cs_path in cs_files:
            parsed = parse_cs_file(asm_name, asm_dir, cs_path)
            for t in parsed:
                all_types.append(t)
                page_types[t.page_slug].append(t)

    _enrich_with_xml_docs(all_types, xml_docs)

    outputs: Dict[Path, str] = {}
    outputs[API_OUT_DIR / "README.md"] = render_index(page_types, all_types)
    for spec in PAGE_SPECS:
        outputs[API_OUT_DIR / f"{spec.slug}.md"] = render_page(spec, page_types[spec.slug])

    return outputs


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Generate deterministic Markdown API docs for NativeMods.")
    parser.add_argument(
        "--check",
        action="store_true",
        help="Check whether docs/api/*.md are up-to-date without modifying files; exit non-zero on drift.",
    )
    parser.add_argument(
        "--xml-dir",
        type=Path,
        default=None,
        help="Optional directory to search for compiled LocalExile.Client.*.xml documentation files.",
    )
    args = parser.parse_args(argv)

    expected = generate_all(xml_dir=args.xml_dir)

    if args.check:
        stale: List[str] = []
        for path, content in sorted(expected.items()):
            rel = path.relative_to(REPO_ROOT).as_posix()
            if not path.exists():
                stale.append(f"MISSING: {rel}")
            else:
                actual = path.read_text(encoding="utf-8")
                if actual != content:
                    stale.append(f"STALE:   {rel}")
        # Also detect stray generated files in docs/api/
        if API_OUT_DIR.exists():
            expected_names = {p.name for p in expected}
            for existing in sorted(API_OUT_DIR.glob("*.md")):
                if existing.name not in expected_names:
                    stale.append(f"EXTRA:   {existing.relative_to(REPO_ROOT).as_posix()}")

        if stale:
            print("API documentation drift detected:", file=sys.stderr)
            for line in stale:
                print(f"  {line}", file=sys.stderr)
            print("Run `python3 scripts/generate-api-docs.py` to regenerate.", file=sys.stderr)
            return 1

        print(f"API docs check OK: {len(expected)} files up-to-date.")
        return 0

    API_OUT_DIR.mkdir(parents=True, exist_ok=True)
    for path, content in sorted(expected.items()):
        path.write_text(content, encoding="utf-8", newline="\n")

    print(f"Generated {len(expected)} API reference pages under {API_OUT_DIR.relative_to(REPO_ROOT)}/.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
