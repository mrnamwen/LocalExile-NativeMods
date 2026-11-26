#!/usr/bin/env python3
"""
Deterministic Address Library code generator for LocalExile NativeMods.

Reads data/addresses/3.29.3d.json and data/addresses/3.29.3c.json, validates
both against data/addresses/address-library.schema.json constraints, and generates
src/native-shim/src/address_table.cpp.

Usage:
    python3 scripts/generate-address-table.py          # regenerate if changed
    python3 scripts/generate-address-table.py --check  # verify in-sync (CI / CMake)
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parent.parent
ADDR_DIR = REPO_ROOT / "data" / "addresses"
SCHEMA_PATH = ADDR_DIR / "address-library.schema.json"
JSON_3293D_PATH = ADDR_DIR / "3.29.3d.json"
JSON_3293C_PATH = ADDR_DIR / "3.29.3c.json"
OUTPUT_CPP_PATH = REPO_ROOT / "src" / "native-shim" / "src" / "address_table.cpp"

BUILD_ID_MAP = {
    327984: "NM_BUILD_3_29_3C_327984",
    328126: "NM_BUILD_3_29_3C_328126",
    328228: "NM_BUILD_3_29_3D_328228",
}

KIND_MAP = {
    "code": "NM_SYM_CODE",
    "data": "NM_SYM_DATA",
    "vtable": "NM_SYM_VTABLE",
    "import_slot": "NM_SYM_IMPORT_SLOT",
    "global_ref": "NM_SYM_GLOBAL_REF",
}

STATUS_MAP = {
    "unverified_3_29_3c": "NM_SYM_STATUS_UNVERIFIED",
    "static_verified": "NM_SYM_STATUS_STATIC_VERIFIED",
    "live_verified": "NM_SYM_STATUS_LIVE_VERIFIED",
    "doc_verified": "NM_SYM_STATUS_DOC_VERIFIED",
    "code_shared_3_29_3c": "NM_SYM_STATUS_CODE_SHARED",
}


def c_escape(value: str) -> str:
    escaped = value.replace("\\", "\\\\").replace('"', '\\"')
    return f'"{escaped}"'


def validate_against_schema(doc: dict[str, Any], schema: dict[str, Any], path: Path) -> None:
    required_top = set(schema["required"])
    props_top = set(schema["properties"].keys())
    doc_keys = set(doc.keys())
    missing = required_top - doc_keys
    if missing:
        raise ValueError(f"{path}: missing required top-level keys: {sorted(missing)}")
    extra = doc_keys - props_top
    if extra:
        raise ValueError(f"{path}: unexpected top-level keys: {sorted(extra)}")
    if doc["schemaVersion"] != schema["properties"]["schemaVersion"]["const"]:
        raise ValueError(f"{path}: invalid schemaVersion {doc['schemaVersion']}")
    if doc["gameVersion"] not in schema["properties"]["gameVersion"]["enum"]:
        raise ValueError(f"{path}: invalid gameVersion {doc['gameVersion']}")

    sym_schema = schema["properties"]["symbols"]["items"]
    sym_req = set(sym_schema["required"])
    sym_props = set(sym_schema["properties"].keys())
    valid_categories = set(sym_schema["properties"]["category"]["enum"])
    valid_kinds = set(sym_schema["properties"]["kind"]["enum"])
    valid_statuses = set(sym_schema["properties"]["status"]["enum"])

    name_re = re.compile(sym_schema["properties"]["name"]["pattern"])
    hex_re = re.compile(sym_schema["properties"]["rva"]["pattern"])
    bytes_re = re.compile(sym_schema["properties"]["expectedBytes"]["pattern"])
    sha_re = re.compile(schema["properties"]["builds"]["items"]["properties"]["executableSha256"]["pattern"])

    seen_builds: set[int] = set()
    for b in doc["builds"]:
        bn = b["buildNumber"]
        if bn in seen_builds:
            raise ValueError(f"{path}: duplicate buildNumber {bn}")
        seen_builds.add(bn)
        if bn not in BUILD_ID_MAP:
            raise ValueError(f"{path}: unknown buildNumber {bn} (not mapped to nm_build_id_t)")
        if not sha_re.match(b["executableSha256"]):
            raise ValueError(f"{path}: invalid executableSha256 for build {bn}")

    seen_syms: set[str] = set()
    for s in doc["symbols"]:
        sk = set(s.keys())
        if sym_req - sk:
            raise ValueError(f"{path}: symbol {s.get('name')} missing keys {sorted(sym_req - sk)}")
        if sk - sym_props:
            raise ValueError(f"{path}: symbol {s.get('name')} unexpected keys {sorted(sk - sym_props)}")
        name = s["name"]
        if name in seen_syms:
            raise ValueError(f"{path}: duplicate symbol '{name}'")
        seen_syms.add(name)
        if not name_re.match(name):
            raise ValueError(f"{path}: invalid symbol name '{name}'")
        if s["category"] not in valid_categories:
            raise ValueError(f"{path}: invalid category '{s['category']}' on '{name}'")
        if s["kind"] not in valid_kinds:
            raise ValueError(f"{path}: invalid kind '{s['kind']}' on '{name}'")
        if s["status"] not in valid_statuses:
            raise ValueError(f"{path}: invalid status '{s['status']}' on '{name}'")
        if s["rva"] is not None and not hex_re.match(s["rva"]):
            raise ValueError(f"{path}: invalid rva '{s['rva']}' on '{name}'")
        if s["va"] is not None and not hex_re.match(s["va"]):
            raise ValueError(f"{path}: invalid va '{s['va']}' on '{name}'")
        if s["expectedBytes"] is not None and not bytes_re.match(s["expectedBytes"]):
            raise ValueError(f"{path}: invalid expectedBytes '{s['expectedBytes']}' on '{name}'")

    off_schema = schema["properties"]["offsets"]["items"]
    off_req = set(off_schema["required"])
    off_props = set(off_schema["properties"].keys())
    off_re = re.compile(off_schema["properties"]["offset"]["pattern"])
    seen_offs: set[str] = set()
    for o in doc["offsets"]:
        ok = set(o.keys())
        if off_req - ok:
            raise ValueError(f"{path}: offset {o.get('name')} missing keys {sorted(off_req - ok)}")
        if ok - off_props:
            raise ValueError(f"{path}: offset {o.get('name')} unexpected keys {sorted(ok - off_props)}")
        if o["name"] in seen_offs:
            raise ValueError(f"{path}: duplicate offset '{o['name']}'")
        seen_offs.add(o["name"])
        if not off_re.match(o["offset"]):
            raise ValueError(f"{path}: invalid offset '{o['offset']}' on '{o['name']}'")

    const_req = set(schema["properties"]["constants"]["required"])
    const_keys = set(doc["constants"].keys())
    if const_req - const_keys:
        raise ValueError(f"{path}: missing constants {sorted(const_req - const_keys)}")


def format_symbol_row(sym: dict[str, Any]) -> str:
    name_lit = c_escape(sym["name"]) + ","
    cat_lit = c_escape(sym["category"]) + ","
    kind_enum = KIND_MAP[sym["kind"]] + ","
    status_enum = STATUS_MAP[sym["status"]] + ","

    rva_raw = sym["rva"]
    if rva_raw is None or sym["status"] == "unverified_3_29_3c":
        rva_lit = "0ull,"
    else:
        rva_val = int(rva_raw, 16)
        rva_lit = f"0x{rva_val:07x}ull,"

    exp = sym["expectedBytes"]
    exp_lit = c_escape(exp.lower()) if exp else "nullptr"

    return (
        f"    {{{name_lit:<37} {cat_lit:<15} {kind_enum:<20} "
        f"{status_enum:<31} {rva_lit:<14} {exp_lit}}},"
    )


def generate_cpp(doc_3d: dict[str, Any], doc_c: dict[str, Any]) -> str:
    builds: list[dict[str, Any]] = []
    for b in doc_c["builds"] + doc_3d["builds"]:
        builds.append({
            "enum": BUILD_ID_MAP[b["buildNumber"]],
            "version": doc_c["gameVersion"] if b in doc_c["builds"] else doc_3d["gameVersion"],
            "sha256": b["executableSha256"].lower(),
            "build_number": b["buildNumber"],
        })
    builds.sort(key=lambda x: x["build_number"])

    syms_3d = doc_3d["symbols"]
    syms_3c = doc_c["symbols"]

    lines: list[str] = [
        "// GENERATED FILE — DO NOT EDIT MANUALLY.",
        "// Generated by scripts/generate-address-table.py from:",
        "//   - data/addresses/3.29.3d.json",
        "//   - data/addresses/3.29.3c.json",
        "",
        '#include "address_table.hpp"',
        "",
        "#include <algorithm>",
        "#include <array>",
        "#include <cctype>",
        "#include <cstring>",
        "",
        "namespace nativemods {",
        "",
        "namespace {",
        "",
        f"constexpr std::array<BuildDescriptor, {len(builds)}> kKnownBuilds = {{{{",
    ]
    for b in builds:
        lines.append(
            f'    {{{b["enum"]}, {c_escape(b["version"])}, {c_escape(b["sha256"])}, {b["build_number"]}}},'
        )
    lines.extend([
        "}};",
        "",
        f"constexpr std::array<EmbeddedSymbol, {len(syms_3d)}> kSymbols3293d = {{{{",
    ])
    for sym in syms_3d:
        lines.append(format_symbol_row(sym))
    lines.extend([
        "}};",
        "",
        f"constexpr std::array<EmbeddedSymbol, {len(syms_3c)}> kSymbols3293c = {{{{",
    ])
    for sym in syms_3c:
        lines.append(format_symbol_row(sym))
    lines.extend([
        "}};",
        "",
        "int hex_nibble(char c) {",
        "    if (c >= '0' && c <= '9') return c - '0';",
        "    if (c >= 'a' && c <= 'f') return c - 'a' + 10;",
        "    if (c >= 'A' && c <= 'F') return c - 'A' + 10;",
        "    return -1;",
        "}",
        "",
        "} // namespace",
        "",
        "nm_build_id_t identify_build_by_sha256(std::string_view sha256_hex) {",
        "    if (sha256_hex.size() != 64) {",
        "        return NM_BUILD_UNKNOWN;",
        "    }",
        "    for (const auto& b : kKnownBuilds) {",
        "        bool eq = true;",
        "        for (size_t i = 0; i < 64; ++i) {",
        "            if (std::tolower(static_cast<unsigned char>(sha256_hex[i])) != b.sha256[i]) {",
        "                eq = false;",
        "                break;",
        "            }",
        "        }",
        "        if (eq) {",
        "            return b.build_id;",
        "        }",
        "    }",
        "    return NM_BUILD_UNKNOWN;",
        "}",
        "",
        "const char* build_version_string(nm_build_id_t build_id) {",
        "    switch (build_id) {",
        "        case NM_BUILD_3_29_3C_327984:",
        "        case NM_BUILD_3_29_3C_328126:",
        '            return "3.29.3c";',
        "        case NM_BUILD_3_29_3D_328228:",
        '            return "3.29.3d";',
        "        case NM_BUILD_SYNTHETIC:",
        '            return "synthetic";',
        "        default:",
        '            return "unknown";',
        "    }",
        "}",
        "",
        "const EmbeddedSymbol* get_symbol_table(nm_build_id_t build_id, uint32_t& out_count) {",
        "    switch (build_id) {",
        "        case NM_BUILD_3_29_3D_328228:",
        "        case NM_BUILD_SYNTHETIC:",
        "            out_count = static_cast<uint32_t>(kSymbols3293d.size());",
        "            return kSymbols3293d.data();",
        "        case NM_BUILD_3_29_3C_327984:",
        "        case NM_BUILD_3_29_3C_328126:",
        "            out_count = static_cast<uint32_t>(kSymbols3293c.size());",
        "            return kSymbols3293c.data();",
        "        default:",
        "            out_count = 0;",
        "            return nullptr;",
        "    }",
        "}",
        "",
        "const EmbeddedSymbol* find_symbol(nm_build_id_t build_id, std::string_view name) {",
        "    uint32_t count = 0;",
        "    const EmbeddedSymbol* table = get_symbol_table(build_id, count);",
        "    if (!table) {",
        "        return nullptr;",
        "    }",
        "    for (uint32_t i = 0; i < count; ++i) {",
        "        if (name == table[i].name) {",
        "            return &table[i];",
        "        }",
        "    }",
        "    return nullptr;",
        "}",
        "",
        "nm_status_t verify_memory_bytes(uint64_t address, const char* expected_bytes_hex) {",
        "    if (address == 0 || expected_bytes_hex == nullptr) {",
        "        return NM_ERR_INVALID_ARG;",
        "    }",
        "    const size_t hex_len = std::strlen(expected_bytes_hex);",
        "    if (hex_len == 0 || (hex_len % 2) != 0) {",
        "        return NM_ERR_INVALID_ARG;",
        "    }",
        "",
        "    const auto* mem = reinterpret_cast<const uint8_t*>(static_cast<uintptr_t>(address));",
        "    const size_t byte_len = hex_len / 2;",
        "    for (size_t i = 0; i < byte_len; ++i) {",
        "        const char hi = expected_bytes_hex[i * 2];",
        "        const char lo = expected_bytes_hex[i * 2 + 1];",
        "        if (hi == '?' && lo == '?') {",
        "            continue;",
        "        }",
        "        const int hi_v = hex_nibble(hi);",
        "        const int lo_v = hex_nibble(lo);",
        "        if (hi_v < 0 || lo_v < 0) {",
        "            return NM_ERR_INVALID_ARG;",
        "        }",
        "        const uint8_t expected = static_cast<uint8_t>((hi_v << 4) | lo_v);",
        "        if (mem[i] != expected) {",
        "            return NM_ERR_BYTE_MISMATCH;",
        "        }",
        "    }",
        "    return NM_OK;",
        "}",
        "",
        "} // namespace nativemods",
        "",
    ])
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate or verify src/native-shim/src/address_table.cpp.")
    parser.add_argument("--check", action="store_true", help="Exit non-zero if address_table.cpp is out of sync.")
    args = parser.parse_args()

    with SCHEMA_PATH.open("r", encoding="utf-8") as f:
        schema = json.load(f)
    with JSON_3293D_PATH.open("r", encoding="utf-8") as f:
        doc_3d = json.load(f)
    with JSON_3293C_PATH.open("r", encoding="utf-8") as f:
        doc_c = json.load(f)

    validate_against_schema(doc_3d, schema, JSON_3293D_PATH)
    validate_against_schema(doc_c, schema, JSON_3293C_PATH)

    generated = generate_cpp(doc_3d, doc_c)

    if args.check:
        if not OUTPUT_CPP_PATH.exists():
            print(f"ERROR: {OUTPUT_CPP_PATH} does not exist.", file=sys.stderr)
            return 1
        current = OUTPUT_CPP_PATH.read_text(encoding="utf-8")
        if current != generated:
            print(
                f"ERROR: {OUTPUT_CPP_PATH} is out of sync with data/addresses/*.json. "
                "Run: python3 scripts/generate-address-table.py",
                file=sys.stderr,
            )
            return 1
        print(
            f"OK: {OUTPUT_CPP_PATH.relative_to(REPO_ROOT)} matches "
            f"3.29.3d ({len(doc_3d['symbols'])} symbols) & 3.29.3c ({len(doc_c['symbols'])} symbols)."
        )
        return 0

    existing = OUTPUT_CPP_PATH.read_text(encoding="utf-8") if OUTPUT_CPP_PATH.exists() else None
    if existing != generated:
        OUTPUT_CPP_PATH.write_text(generated, encoding="utf-8")
        print(
            f"Generated {OUTPUT_CPP_PATH.relative_to(REPO_ROOT)} "
            f"(3.29.3d={len(doc_3d['symbols'])} symbols, 3.29.3c={len(doc_c['symbols'])} symbols)."
        )
    else:
        print(
            f"Up-to-date {OUTPUT_CPP_PATH.relative_to(REPO_ROOT)} "
            f"(3.29.3d={len(doc_3d['symbols'])} symbols, 3.29.3c={len(doc_c['symbols'])} symbols)."
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
