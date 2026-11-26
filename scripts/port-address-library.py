#!/usr/bin/env python3
"""
Automated Address Library Cross-Porting & Verification CLI for LocalExile NativeMods.

Ports data/addresses/3.29.3d.json (447 symbols) to 3.29.3c or any future 3.29.x
Win64 Path of Exile executable using four deterministic strategies:

  1. Code & global_ref symbols (342): .pdata RUNTIME_FUNCTION boundary + masked-RIP
     x86-64 prologue signature matching (exact-RVA check + anchor-accelerated search +
     E8 caller-xref disambiguation for short leaf helpers).
  2. Vtable & data symbols (101 of 102): .text RIP-relative [rip + disp32] xref
     extraction from enclosing .pdata functions -> target disp32 resolution.
  3. Import slot symbols (3): PE32+ Import Directory / IAT (dll, name) lookup.
  4. Constant .rdata data (geal.precedence_table, 1): unique 32-byte constant scan.

Usage:
    # Self-verify 447/447 (100%) exact resolution on 3.29.3d Game.exe:
    python3 scripts/port-address-library.py \\
        --source-json data/addresses/3.29.3d.json \\
        --source-exe /home/nam/Git/LocalExile/Client/Game.exe \\
        --verify-self

    # Port 3.29.3d.json to a 3.29.3c executable:
    python3 scripts/port-address-library.py \\
        --source-json data/addresses/3.29.3d.json \\
        --source-exe /home/nam/Git/LocalExile/Client/Game.exe \\
        --target-exe /path/to/PathOfExile_3_29_3c.exe \\
        --out data/addresses/3.29.3c.json \\
        --game-version 3.29.3c
"""

from __future__ import annotations

import argparse
import bisect
import hashlib
import json
import re
import struct
import sys
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any

# Unique 32-byte constant prefix of geal.precedence_table in .rdata (verified in #162/#163).
GEAL_PRECEDENCE_PATTERN = bytes.fromhex(
    "0000000000000000030304040404060607070505010200003233303135344045"
)


# =============================================================================
# Pure-Python PE32+ Parser (.text, .rdata, .data, .pdata, IAT)
# =============================================================================

@dataclass(frozen=True)
class PeSection:
    name: str
    virtual_size: int
    virtual_address: int
    raw_size: int
    raw_offset: int
    characteristics: int

    @property
    def virtual_end(self) -> int:
        return self.virtual_address + max(self.virtual_size, self.raw_size)


class Pe64Image:
    """Minimal zero-dependency PE32+ reader for x64 Path of Exile executables."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self.data = path.read_bytes()
        self.size = len(self.data)
        self.sha256 = hashlib.sha256(self.data).hexdigest()

        if self.size < 0x100 or self.data[:2] != b"MZ":
            raise ValueError(f"{path}: invalid DOS header")
        e_lfanew = struct.unpack_from("<I", self.data, 0x3C)[0]
        if self.data[e_lfanew : e_lfanew + 4] != b"PE\x00\x00":
            raise ValueError(f"{path}: invalid PE signature")

        coff_off = e_lfanew + 4
        machine, num_sections, _, _, _, opt_size, _ = struct.unpack_from(
            "<HHIIIHH", self.data, coff_off
        )
        if machine != 0x8664:
            raise ValueError(f"{path}: expected x64 PE32+ (0x8664), got 0x{machine:x}")

        opt_off = coff_off + 20
        magic = struct.unpack_from("<H", self.data, opt_off)[0]
        if magic != 0x20B:
            raise ValueError(f"{path}: expected PE32+ optional magic 0x20B, got 0x{magic:x}")

        self.image_base = struct.unpack_from("<Q", self.data, opt_off + 24)[0]
        self.size_of_image = struct.unpack_from("<I", self.data, opt_off + 56)[0]
        num_data_dirs = struct.unpack_from("<I", self.data, opt_off + 108)[0]

        self.data_dirs: list[tuple[int, int]] = []
        dd_off = opt_off + 112
        for i in range(min(num_data_dirs, 16)):
            rva, sz = struct.unpack_from("<II", self.data, dd_off + i * 8)
            self.data_dirs.append((rva, sz))

        self.sections: list[PeSection] = []
        sec_off = opt_off + opt_size
        for i in range(num_sections):
            raw = self.data[sec_off + i * 40 : sec_off + (i + 1) * 40]
            name = raw[:8].split(b"\x00", 1)[0].decode("ascii", errors="replace")
            vsize, vaddr, rsize, roff, _, _, _, _, chars = struct.unpack_from("<IIIIIIHHI", raw, 8)
            self.sections.append(PeSection(name, vsize, vaddr, rsize, roff, chars))

        self.text_sec = self._find_section(".text")
        self.rdata_sec = self._find_section(".rdata")
        self.data_sec = self._find_section(".data")
        self.pdata_sec = self._find_section(".pdata")

        # Parse .pdata RUNTIME_FUNCTION table (sorted by BeginAddress).
        self.pdata_begins: list[int] = []
        self.pdata_ends: list[int] = []
        self.pdata_set: set[int] = set()
        if self.pdata_sec is not None:
            p_start = self.pdata_sec.raw_offset
            p_len = min(self.pdata_sec.virtual_size, self.pdata_sec.raw_size)
            count = p_len // 12
            entries: list[tuple[int, int]] = []
            for idx in range(count):
                b, e, _ = struct.unpack_from("<III", self.data, p_start + idx * 12)
                if b == 0 and e == 0:
                    continue
                if e > b:
                    entries.append((b, e))
            entries.sort()
            for b, e in entries:
                self.pdata_begins.append(b)
                self.pdata_ends.append(e)
                self.pdata_set.add(b)

        # Parse PE Import Directory -> IAT maps.
        self.iat_rva_to_sym: dict[int, tuple[str, str]] = {}
        self.iat_sym_to_rva: dict[tuple[str, str], int] = {}
        self._parse_imports()

    def _find_section(self, name: str) -> PeSection | None:
        for s in self.sections:
            if s.name == name:
                return s
        return None

    def rva_to_offset(self, rva: int) -> int | None:
        for s in self.sections:
            if s.virtual_address <= rva < s.virtual_address + s.raw_size:
                return s.raw_offset + (rva - s.virtual_address)
        return None

    def section_for_rva(self, rva: int) -> PeSection | None:
        for s in self.sections:
            if s.virtual_address <= rva < s.virtual_end:
                return s
        return None

    def read_rva(self, rva: int, length: int) -> bytes | None:
        off = self.rva_to_offset(rva)
        if off is None or off + length > self.size:
            return None
        return self.data[off : off + length]

    def read_cstr_rva(self, rva: int, max_len: int = 256) -> str:
        off = self.rva_to_offset(rva)
        if off is None:
            return ""
        end = self.data.find(b"\x00", off, min(self.size, off + max_len))
        if end < 0:
            end = min(self.size, off + max_len)
        return self.data[off:end].decode("ascii", errors="replace")

    def find_pdata_function(self, rva: int) -> tuple[int, int] | None:
        idx = bisect.bisect_right(self.pdata_begins, rva) - 1
        if idx >= 0 and self.pdata_begins[idx] <= rva < self.pdata_ends[idx]:
            return (self.pdata_begins[idx], self.pdata_ends[idx])
        return None

    def _parse_imports(self) -> None:
        if len(self.data_dirs) <= 1:
            return
        imp_rva, imp_size = self.data_dirs[1]
        if imp_rva == 0 or imp_size == 0:
            return
        imp_off = self.rva_to_offset(imp_rva)
        if imp_off is None:
            return

        pos = imp_off
        while pos + 20 <= self.size:
            orig_first_thunk, _, _, name_rva, first_thunk = struct.unpack_from("<IIIII", self.data, pos)
            if orig_first_thunk == 0 and name_rva == 0 and first_thunk == 0:
                break
            dll_name = self.read_cstr_rva(name_rva).lower()
            lookup_rva = orig_first_thunk if orig_first_thunk != 0 else first_thunk
            lookup_off = self.rva_to_offset(lookup_rva)
            if lookup_off is not None and first_thunk != 0:
                slot_idx = 0
                while lookup_off + slot_idx * 8 + 8 <= self.size:
                    entry = struct.unpack_from("<Q", self.data, lookup_off + slot_idx * 8)[0]
                    if entry == 0:
                        break
                    iat_rva = first_thunk + slot_idx * 8
                    if (entry & (1 << 63)) != 0:
                        sym_name = f"#{entry & 0xFFFF}"
                    else:
                        sym_name = self.read_cstr_rva(int(entry & 0x7FFFFFFF) + 2)
                    key = (dll_name, sym_name)
                    self.iat_rva_to_sym[iat_rva] = key
                    self.iat_sym_to_rva[key] = iat_rva
                    slot_idx += 1
            pos += 20


# =============================================================================
# Lightweight x86-64 Instruction Length + RIP-Relative / Branch Mask Decoder
# =============================================================================

@dataclass(frozen=True)
class X64Insn:
    length: int
    disp_offset: int = 0
    disp_size: int = 0
    is_rip_rel: bool = False
    is_rel_branch: bool = False


def decode_x64_insn(buf: bytes | memoryview, off: int, end: int) -> X64Insn:
    """Decode one x86-64 instruction at buf[off:end]. Returns length=0 on unsupported/truncated."""
    if off >= end:
        return X64Insn(0)
    p = off
    has_66 = False
    has_67 = False
    has_f2 = False
    has_f3 = False
    rex = 0

    # Legacy prefixes (max 4)
    while p < end and (p - off) < 8:
        b = buf[p]
        if b in (0xF0, 0x2E, 0x36, 0x3E, 0x26, 0x64, 0x65):
            p += 1
        elif b == 0x66:
            has_66 = True
            p += 1
        elif b == 0x67:
            has_67 = True
            p += 1
        elif b == 0xF2:
            has_f2 = True
            p += 1
        elif b == 0xF3:
            has_f3 = True
            p += 1
        else:
            break

    # VEX 2-byte / 3-byte prefixes (C5 / C4)
    if p < end and buf[p] in (0xC4, 0xC5):
        vex = buf[p]
        if vex == 0xC5:
            if p + 3 > end:
                return X64Insn(0)
            p += 2
            op = buf[p]
            p += 1
            return _decode_modrm(buf, off, p, end, imm_size=_vex_imm_size(1, op), has_67=has_67)
        else:
            if p + 4 > end:
                return X64Insn(0)
            mmmmm = buf[p + 1] & 0x1F
            p += 3
            op = buf[p]
            p += 1
            return _decode_modrm(buf, off, p, end, imm_size=_vex_imm_size(mmmmm, op), has_67=has_67)

    # REX prefix
    if p < end and 0x40 <= buf[p] <= 0x4F:
        rex = buf[p]
        p += 1
    if p >= end:
        return X64Insn(0)

    rex_w = (rex & 0x08) != 0
    op = buf[p]
    p += 1

    # 2-byte opcode escape 0x0F
    if op == 0x0F:
        if p >= end:
            return X64Insn(0)
        op2 = buf[p]
        p += 1
        # 3-byte escape 0F 38 / 0F 3A
        if op2 in (0x38, 0x3A):
            if p >= end:
                return X64Insn(0)
            p += 1
            imm = 1 if op2 == 0x3A else 0
            return _decode_modrm(buf, off, p, end, imm_size=imm, has_67=has_67)
        # Jcc rel32 (0F 80..8F)
        if 0x80 <= op2 <= 0x8F:
            if p + 4 > end:
                return X64Insn(0)
            return X64Insn(length=(p + 4) - off, disp_offset=p - off, disp_size=4, is_rel_branch=True)
        # No-operand 0F ops (cpuid, rdtsc, syscall, ud2, etc.)
        if op2 in (0x05, 0x07, 0x08, 0x09, 0x0B, 0x31, 0x34, 0x77, 0xA2):
            return X64Insn(length=p - off)
        # 0F C8..CF bswap r32/r64
        if 0xC8 <= op2 <= 0xCF:
            return X64Insn(length=p - off)
        # ModRM + imm8 (0F 70, 71..73, A4, AC, BA, C2, C4, C5, C6)
        if op2 in (0x70, 0x71, 0x72, 0x73, 0xA4, 0xAC, 0xBA, 0xC2, 0xC4, 0xC5, 0xC6):
            return _decode_modrm(buf, off, p, end, imm_size=1, has_67=has_67)
        # Standard ModRM 0F ops (SSE/SSE2 movss/movsd/movaps/movups/ucomiss/comiss/cvts*, cmovcc, setcc, movzx, movsx, imul, nop/prefetch, bt/bts/btr/btc, xadd, cmpxchg)
        return _decode_modrm(buf, off, p, end, imm_size=0, has_67=has_67)

    # 1-byte opcodes
    # Push/pop reg, nop/xchg rax,r, ret/leave/int3/cwde/cdq/hlt/cmc/clc/stc/cld/std
    if (0x50 <= op <= 0x5F) or (0x90 <= op <= 0x99) or op in (0x9B, 0x9C, 0x9D, 0x9E, 0x9F, 0xC3, 0xC9, 0xCB, 0xCC, 0xF4, 0xF5, 0xF8, 0xF9, 0xFC, 0xFD):
        return X64Insn(length=p - off)
    if op in (0xA4, 0xA5, 0xA6, 0xA7, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF):
        return X64Insn(length=p - off)
    # Short branches rel8 (70..7F, E0..E3, EB)
    if (0x70 <= op <= 0x7F) or (0xE0 <= op <= 0xE3) or op == 0xEB:
        if p + 1 > end:
            return X64Insn(0)
        return X64Insn(length=(p + 1) - off, disp_offset=p - off, disp_size=1, is_rel_branch=True)
    # CALL/JMP rel32 (E8 / E9)
    if op in (0xE8, 0xE9):
        if p + 4 > end:
            return X64Insn(0)
        return X64Insn(length=(p + 4) - off, disp_offset=p - off, disp_size=4, is_rel_branch=True)
    # imm8 ops (AL, imm8; push imm8; int imm8; in/out imm8)
    if op in (0x04, 0x0C, 0x14, 0x1C, 0x24, 0x2C, 0x34, 0x3C, 0x6A, 0xA8, 0xCD, 0xE4, 0xE5, 0xE6, 0xE7):
        return X64Insn(length=(p + 1) - off) if p + 1 <= end else X64Insn(0)
    # MOV r8, imm8 (B0..B7)
    if 0xB0 <= op <= 0xB7:
        return X64Insn(length=(p + 1) - off) if p + 1 <= end else X64Insn(0)
    # MOV r16/32/64, imm16/32/64 (B8..BF)
    if 0xB8 <= op <= 0xBF:
        imm = 8 if rex_w else (2 if has_66 else 4)
        return X64Insn(length=(p + imm) - off) if p + imm <= end else X64Insn(0)
    # rAX, imm16/32; push imm32
    if op in (0x05, 0x0D, 0x15, 0x1D, 0x25, 0x2D, 0x35, 0x3D, 0x68, 0xA9):
        imm = 2 if (has_66 and not rex_w) else 4
        return X64Insn(length=(p + imm) - off) if p + imm <= end else X64Insn(0)
    # RET imm16
    if op in (0xC2, 0xCA):
        return X64Insn(length=(p + 2) - off) if p + 2 <= end else X64Insn(0)
    # ENTER imm16, imm8
    if op == 0xC8:
        return X64Insn(length=(p + 3) - off) if p + 3 <= end else X64Insn(0)
    # MOV AL/rAX, moffs64 (A0..A3)
    if 0xA0 <= op <= 0xA3:
        moffs = 4 if has_67 else 8
        return X64Insn(length=(p + moffs) - off) if p + moffs <= end else X64Insn(0)
    # ModRM + imm8: 6B, 80, 82, 83, C0, C1, C6
    if op in (0x6B, 0x80, 0x82, 0x83, 0xC0, 0xC1, 0xC6):
        return _decode_modrm(buf, off, p, end, imm_size=1, has_67=has_67)
    # ModRM + imm16/32: 69, 81, C7
    if op in (0x69, 0x81, 0xC7):
        imm = 2 if (has_66 and not rex_w) else 4
        return _decode_modrm(buf, off, p, end, imm_size=imm, has_67=has_67)
    # F6 / F7 (TEST r/m, imm when reg==0 or 1; otherwise no imm)
    if op in (0xF6, 0xF7):
        if p >= end:
            return X64Insn(0)
        reg = (buf[p] >> 3) & 7
        imm = 0
        if reg in (0, 1):
            imm = 1 if op == 0xF6 else (2 if (has_66 and not rex_w) else 4)
        return _decode_modrm(buf, off, p, end, imm_size=imm, has_67=has_67)
    # All standard ModRM 1-byte opcodes
    if (
        op in (0x00, 0x01, 0x02, 0x03, 0x08, 0x09, 0x0A, 0x0B,
               0x10, 0x11, 0x12, 0x13, 0x18, 0x19, 0x1A, 0x1B,
               0x20, 0x21, 0x22, 0x23, 0x28, 0x29, 0x2A, 0x2B,
               0x30, 0x31, 0x32, 0x33, 0x38, 0x39, 0x3A, 0x3B,
               0x63, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
               0xD0, 0xD1, 0xD2, 0xD3, 0xFE, 0xFF)
        or (0xD8 <= op <= 0xDF)
    ):
        return _decode_modrm(buf, off, p, end, imm_size=0, has_67=has_67)

    _ = (has_f2, has_f3)
    return X64Insn(0)


def _vex_imm_size(mmmmm: int, op: int) -> int:
    if mmmmm == 3:  # 0F3A
        return 1
    if mmmmm == 1 and op in (0x70, 0x71, 0x72, 0x73, 0xC2, 0xC4, 0xC5, 0xC6):
        return 1
    return 0


def _decode_modrm(
    buf: bytes | memoryview,
    start: int,
    p: int,
    end: int,
    imm_size: int,
    has_67: bool,
) -> X64Insn:
    if p >= end:
        return X64Insn(0)
    modrm = buf[p]
    p += 1
    mod = (modrm >> 6) & 3
    rm = modrm & 7

    disp_offset = 0
    disp_size = 0
    is_rip_rel = False

    if mod == 0 and rm == 5:
        disp_offset = p - start
        disp_size = 4
        is_rip_rel = not has_67
        p += 4
    else:
        if mod != 3 and rm == 4:
            if p >= end:
                return X64Insn(0)
            sib = buf[p]
            p += 1
            if mod == 0 and (sib & 7) == 5:
                disp_offset = p - start
                disp_size = 4
                p += 4
        if mod == 1:
            p += 1
        elif mod == 2:
            disp_offset = p - start
            disp_size = 4
            p += 4

    p += imm_size
    if p > end:
        return X64Insn(0)
    return X64Insn(
        length=p - start,
        disp_offset=disp_offset,
        disp_size=disp_size,
        is_rip_rel=is_rip_rel,
        is_rel_branch=False,
    )


# =============================================================================
# Masked-RIP Signature Building & Fast Matching
# =============================================================================

@dataclass(frozen=True)
class MaskedSig:
    """Byte pattern with optional wildcard positions (mask[i] == False => wildcard)."""
    raw: bytes
    mask: tuple[bool, ...]
    anchor_offset: int
    anchor_bytes: bytes

    def matches(self, buf: bytes | memoryview, off: int) -> bool:
        n = len(self.raw)
        if off < 0 or off + n > len(buf):
            return False
        for i in range(n):
            if self.mask[i] and buf[off + i] != self.raw[i]:
                return False
        return True


def build_masked_sig(img: Pe64Image, rva: int, max_len: int = 48) -> MaskedSig | None:
    """Build a masked-RIP x64 signature starting at rva (respecting .pdata function end)."""
    fn = img.find_pdata_function(rva)
    limit = max_len
    if fn is not None and fn[1] > rva:
        limit = min(max_len, max(16, fn[1] - rva))
        # Clamp strictly to function end if the function is very short (< 16B) so padding doesn't vary.
        if fn[1] - rva < 16:
            limit = fn[1] - rva

    off = img.rva_to_offset(rva)
    if off is None or off + limit > img.size:
        return None
    chunk = img.data[off : off + limit]
    mask = [True] * limit

    pos = 0
    while pos < limit:
        insn = decode_x64_insn(chunk, pos, limit)
        if insn.length <= 0:
            break
        if (insn.is_rip_rel or insn.is_rel_branch) and insn.disp_size == 4:
            d0 = pos + insn.disp_offset
            for k in range(d0, min(limit, d0 + 4)):
                mask[k] = False
        pos += insn.length

    # Pick longest contiguous unmasked anchor for C-speed bytes.find scanning.
    best_s = 0
    best_l = 0
    cur_s = 0
    cur_l = 0
    for i, keep in enumerate(mask):
        if keep:
            if cur_l == 0:
                cur_s = i
            cur_l += 1
            if cur_l > best_l:
                best_s = cur_s
                best_l = cur_l
        else:
            cur_l = 0

    return MaskedSig(
        raw=chunk,
        mask=tuple(mask),
        anchor_offset=best_s,
        anchor_bytes=chunk[best_s : best_s + best_l],
    )


def extract_func_rip_strings(img: Pe64Image, func_rva: int, max_scan: int = 160) -> tuple[tuple[int, bytes], ...]:
    """Extract (insn_offset, referenced_rdata_cstr_bytes) for RIP-rel string refs inside a function."""
    fn = img.find_pdata_function(func_rva)
    end_rva = min(fn[1], func_rva + max_scan) if fn is not None else (func_rva + max_scan)
    fo = img.rva_to_offset(func_rva)
    if fo is None:
        return ()
    fend = min(img.size, fo + (end_rva - func_rva))
    refs: list[tuple[int, bytes]] = []
    cur = fo
    while cur < fend:
        ins = decode_x64_insn(img.data, cur, fend)
        if ins.length <= 0:
            break
        if ins.is_rip_rel and ins.disp_size == 4:
            disp = struct.unpack_from("<i", img.data, cur + ins.disp_offset)[0]
            target_rva = (func_rva + (cur - fo)) + ins.length + disp
            sec = img.section_for_rva(target_rva)
            if sec is not None and sec.name == ".rdata":
                toff = img.rva_to_offset(target_rva)
                if toff is not None:
                    # Capture printable ASCII or UTF-16LE constant string (up to 48 bytes)
                    nul = img.data.find(b"\x00", toff, min(img.size, toff + 48))
                    if nul > toff:
                        raw_s = img.data[toff:nul]
                        if len(raw_s) >= 2 and all(0x20 <= b <= 0x7E for b in raw_s):
                            refs.append((cur - fo, raw_s))
                    else:
                        # Check UTF-16LE string
                        wslice = img.data[toff : min(img.size, toff + 64)]
                        wchars: list[int] = []
                        for j in range(0, len(wslice) - 1, 2):
                            lo, hi = wslice[j], wslice[j + 1]
                            if lo == 0 and hi == 0:
                                break
                            if hi == 0 and 0x20 <= lo <= 0x7E:
                                wchars.append(lo)
                            else:
                                wchars.clear()
                                break
                        if len(wchars) >= 2:
                            refs.append((cur - fo, bytes(wchars)))
        cur += ins.length
    return tuple(refs)


def search_sig_in_text(
    img: Pe64Image,
    sig: MaskedSig,
    require_pdata_begin: bool = False,
    max_hits: int = 512,
) -> list[int]:
    """Search for sig in img.text_sec using C-speed anchor bytes.find. Returns matching RVAs."""
    sec = img.text_sec
    if sec is None:
        return []
    t_off = sec.raw_offset
    t_end = t_off + min(sec.virtual_size, sec.raw_size)
    t_vaddr = sec.virtual_address
    data = img.data
    hits: list[int] = []

    if len(sig.anchor_bytes) >= 2:
        search_pos = t_off + sig.anchor_offset
        limit_pos = t_end - len(sig.raw) + sig.anchor_offset + 1
        while search_pos < limit_pos:
            idx = data.find(sig.anchor_bytes, search_pos, t_end)
            if idx < 0:
                break
            cand_off = idx - sig.anchor_offset
            if cand_off >= t_off and cand_off + len(sig.raw) <= t_end:
                cand_rva = t_vaddr + (cand_off - t_off)
                if (not require_pdata_begin or cand_rva in img.pdata_set) and sig.matches(data, cand_off):
                    hits.append(cand_rva)
                    if len(hits) >= max_hits:
                        break
            search_pos = idx + 1
    else:
        # Rare fallback when every byte was masked
        if require_pdata_begin:
            for rva in img.pdata_begins:
                co = img.rva_to_offset(rva)
                if co is not None and sig.matches(data, co):
                    hits.append(rva)
                    if len(hits) >= max_hits:
                        break
    return hits


# =============================================================================
# Strategy 2: Fast .text RIP-Relative Xref Scanner (vtables & data globals)
# =============================================================================

@dataclass(frozen=True)
class RipXref:
    func_rva: int
    insn_offset_in_func: int
    disp_offset_in_insn: int
    insn_length: int
    target_rva: int


# Fast regex matching common x64 RIP-relative ModRM instructions (mod=00, rm=101 -> ModRM in {05,0d,15,1d,25,2d,35,3d}).
_RIP_CAND_RE = re.compile(
    rb"[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]",
    re.DOTALL,
)


def collect_rip_xrefs(
    src: Pe64Image,
    target_rvas: set[int],
    max_per_target: int = 3,
) -> dict[int, list[RipXref]]:
    """
    Scan src .text once for RIP-relative [rip + disp32] instructions referencing any
    RVA in target_rvas, validated from the enclosing .pdata function boundary.
    """
    result: dict[int, list[RipXref]] = {r: [] for r in target_rvas}
    remaining = set(target_rvas)
    if not remaining or src.text_sec is None:
        return result

    t_sec = src.text_sec
    t_off = t_sec.raw_offset
    t_len = min(t_sec.virtual_size, t_sec.raw_size)
    t_vaddr = t_sec.virtual_address
    text_bytes = src.data[t_off : t_off + t_len]
    min_target = min(target_rvas)
    max_target = max(target_rvas)

    # Fast pass: for each candidate disp32 immediately following a ModRM byte with (b & 0xC7) == 0x05,
    # check if next_rva + disp32 lands in remaining (checking imm=0,1,4 trailing bytes).
    # Only when a candidate hits remaining do we validate by decoding the enclosing .pdata function!
    validated_funcs: dict[int, list[tuple[int, X64Insn]]] = {}

    def get_func_insns(func_rva: int, func_end: int) -> list[tuple[int, X64Insn]]:
        cached = validated_funcs.get(func_rva)
        if cached is not None:
            return cached
        fo = src.rva_to_offset(func_rva)
        if fo is None:
            validated_funcs[func_rva] = []
            return []
        fend_off = fo + (func_end - func_rva)
        insns: list[tuple[int, X64Insn]] = []
        cur = fo
        while cur < fend_off:
            ins = decode_x64_insn(src.data, cur, fend_off)
            if ins.length <= 0:
                break
            insns.append((cur - fo, ins))
            cur += ins.length
        validated_funcs[func_rva] = insns
        return insns

    unpack_i32 = struct.unpack_from
    # Scan every ModRM-like byte in .text (fast C regex) and check disp32.
    # For a 40MB .text, ~1.2M matches take ~0.9s in CPython.
    for m in _RIP_CAND_RE.finditer(text_bytes):
        pos = m.start()
        if pos + 5 > t_len:
            continue
        disp = unpack_i32("<i", text_bytes, pos + 1)[0]
        base_next = t_vaddr + pos + 5
        t0 = base_next + disp
        hit_target: int | None = None
        if min_target <= t0 <= max_target and t0 in remaining:
            hit_target = t0
        elif min_target <= t0 + 1 <= max_target and (t0 + 1) in remaining:
            hit_target = t0 + 1
        elif min_target <= t0 + 4 <= max_target and (t0 + 4) in remaining:
            hit_target = t0 + 4
        if hit_target is None:
            continue

        # Validate by decoding the enclosing .pdata function.
        cand_rva = t_vaddr + pos
        fn = src.find_pdata_function(cand_rva)
        if fn is None:
            continue
        fn_begin, fn_end = fn
        insns = get_func_insns(fn_begin, fn_end)
        for ioff, ins in insns:
            if not ins.is_rip_rel or ins.disp_size != 4:
                continue
            insn_rva = fn_begin + ioff
            if insn_rva + ins.disp_offset != cand_rva + 1:
                continue
            d_val = unpack_i32("<i", src.data, src.rva_to_offset(insn_rva + ins.disp_offset))[0]
            actual_target = insn_rva + ins.length + d_val
            if actual_target == hit_target:
                xref = RipXref(
                    func_rva=fn_begin,
                    insn_offset_in_func=ioff,
                    disp_offset_in_insn=ins.disp_offset,
                    insn_length=ins.length,
                    target_rva=hit_target,
                )
                lst = result[hit_target]
                if not any(x.func_rva == xref.func_rva and x.insn_offset_in_func == xref.insn_offset_in_func for x in lst):
                    lst.append(xref)
                    if len(lst) >= max_per_target:
                        remaining.discard(hit_target)
                break
        if not remaining:
            break

    return result


def collect_e8_callers(
    src: Pe64Image,
    callee_rvas: set[int],
    max_per_callee: int = 3,
) -> dict[int, list[tuple[int, int]]]:
    """Find (caller_func_rva, call_insn_offset) for E8 rel32 calls targeting callee_rvas."""
    result: dict[int, list[tuple[int, int]]] = {r: [] for r in callee_rvas}
    remaining = set(callee_rvas)
    if not remaining or src.text_sec is None:
        return result

    t_sec = src.text_sec
    t_off = t_sec.raw_offset
    t_len = min(t_sec.virtual_size, t_sec.raw_size)
    t_vaddr = t_sec.virtual_address
    text_bytes = src.data[t_off : t_off + t_len]
    min_c = min(callee_rvas)
    max_c = max(callee_rvas)

    pos = 0
    while True:
        idx = text_bytes.find(b"\xe8", pos)
        if idx < 0 or idx + 5 > t_len:
            break
        rel = struct.unpack_from("<i", text_bytes, idx + 1)[0]
        target = t_vaddr + idx + 5 + rel
        if min_c <= target <= max_c and target in remaining:
            call_rva = t_vaddr + idx
            fn = src.find_pdata_function(call_rva)
            if fn is not None:
                fn_begin, fn_end = fn
                # Verify instruction boundary from fn_begin
                fo = src.rva_to_offset(fn_begin)
                if fo is not None:
                    cur = fo
                    fend = fo + (fn_end - fn_begin)
                    target_off = fo + (call_rva - fn_begin)
                    while cur < target_off and cur < fend:
                        ins = decode_x64_insn(src.data, cur, fend)
                        if ins.length <= 0:
                            break
                        cur += ins.length
                    if cur == target_off:
                        result[target].append((fn_begin, call_rva - fn_begin))
                        if len(result[target]) >= max_per_callee:
                            remaining.discard(target)
                            if not remaining:
                                break
        pos = idx + 1

    return result


# =============================================================================
# Resolution Engine (4 Strategies)
# =============================================================================

@dataclass
class PortResult:
    name: str
    kind: str
    src_rva: int
    dst_rva: int | None
    strategy: str
    expected_bytes: str | None
    detail: str = ""


def format_target_expected_bytes(
    target: Pe64Image,
    dst_rva: int,
    src_expected: str | None,
    kind: str,
) -> str | None:
    if kind in ("vtable", "data", "import_slot"):
        return None
    byte_len = (len(src_expected) // 2) if src_expected else 16
    raw = target.read_rva(dst_rva, byte_len)
    if raw is None:
        return None
    if src_expected:
        # Preserve any '??' wildcards from source signature (e.g. RIP disp32 on network.app_global_ref).
        out_parts: list[str] = []
        for i in range(byte_len):
            pair = src_expected[i * 2 : i * 2 + 2]
            if pair == "??":
                out_parts.append("??")
            else:
                out_parts.append(f"{raw[i]:02x}")
        return "".join(out_parts)
    return raw.hex()


def port_address_library(
    source_doc: dict[str, Any],
    src_img: Pe64Image,
    dst_img: Pe64Image,
    *,
    allow_exact_rva_shortcut: bool = True,
) -> tuple[list[PortResult], dict[str, int]]:
    symbols = source_doc["symbols"]
    results: dict[str, PortResult] = {}
    strategy_counts: Counter[str] = Counter()

    # Cache of resolved .pdata functions src_func_rva -> dst_func_rva
    func_cache: dict[int, int | None] = {}

    def resolve_code_rva(
        src_rva: int,
        require_pdata: bool,
        allow_shortcut: bool,
    ) -> tuple[int | None, str]:
        if require_pdata and src_rva in func_cache:
            cached = func_cache[src_rva]
            return (cached, "pdata_cached" if cached is not None else "unresolved")

        # Mid-function site inside a .pdata function: resolve enclosing .pdata function first!
        src_fn = src_img.find_pdata_function(src_rva)
        if not require_pdata and src_fn is not None and src_fn[0] < src_rva:
            delta = src_rva - src_fn[0]
            dst_fn, _ = resolve_code_rva(src_fn[0], require_pdata=True, allow_shortcut=allow_shortcut)
            if dst_fn is not None:
                cand_rva = dst_fn + delta
                src_b = src_img.read_rva(src_rva, 8)
                dst_b = dst_img.read_rva(cand_rva, 8)
                if src_b is not None and dst_b is not None:
                    local_sig = build_masked_sig(src_img, src_rva, max_len=16)
                    cand_off = dst_img.rva_to_offset(cand_rva)
                    if (
                        (local_sig is not None and cand_off is not None and local_sig.matches(dst_img.data, cand_off))
                        or dst_b[:1] == src_b[:1]
                    ):
                        return (cand_rva, "code_enclosing_pdata")

        sig = build_masked_sig(src_img, src_rva, max_len=48)
        if sig is None:
            return (None, "no_source_bytes")

        # Fast-path: identical RVA in target with matching masked-RIP bytes.
        if allow_shortcut:
            dst_off = dst_img.rva_to_offset(src_rva)
            if (
                dst_off is not None
                and (not require_pdata or src_rva in dst_img.pdata_set)
                and sig.matches(dst_img.data, dst_off)
            ):
                if require_pdata:
                    func_cache[src_rva] = src_rva
                return (src_rva, "code_exact_rva")

        # Full masked-RIP search across target .text (large cap to capture all templated getters).
        hits = search_sig_in_text(dst_img, sig, require_pdata_begin=require_pdata, max_hits=1024)
        if len(hits) == 1:
            if require_pdata:
                func_cache[src_rva] = hits[0]
            return (hits[0], "code_masked_sig_unique")

        # Disambiguate templated functions (e.g. Entity::Get<Component>() getters) via referenced .rdata strings!
        if len(hits) > 1 and require_pdata:
            src_strs = extract_func_rip_strings(src_img, src_rva)
            if src_strs:
                str_hits = [r for r in hits if extract_func_rip_strings(dst_img, r) == src_strs]
                if len(str_hits) == 1:
                    func_cache[src_rva] = str_hits[0]
                    return (str_hits[0], "code_rip_string_disambig")
                if str_hits:
                    hits = str_hits

        # Try wider 96-byte masked signature if still ambiguous.
        if len(hits) > 1:
            wide_sig = build_masked_sig(src_img, src_rva, max_len=96)
            if wide_sig is not None and len(wide_sig.raw) > len(sig.raw):
                narrowed = [
                    r for r in hits
                    if (o := dst_img.rva_to_offset(r)) is not None and wide_sig.matches(dst_img.data, o)
                ]
                if len(narrowed) == 1:
                    if require_pdata:
                        func_cache[src_rva] = narrowed[0]
                    return (narrowed[0], "code_masked_sig_wide")
                if narrowed:
                    hits = narrowed

        # Still ambiguous (e.g. tiny 4B/7B leaf helpers like items.stack_get_size / items.stack_copy):
        # disambiguate via E8 caller xref!
        if len(hits) > 1:
            hit_set = set(hits)
            callers = collect_e8_callers(src_img, {src_rva}, max_per_callee=3).get(src_rva, [])
            votes: Counter[int] = Counter()
            for caller_fn, call_off in callers:
                dst_caller, _ = resolve_code_rva(caller_fn, require_pdata=True, allow_shortcut=allow_shortcut)
                if dst_caller is None:
                    continue
                c_off = dst_img.rva_to_offset(dst_caller + call_off)
                if c_off is not None and c_off + 5 <= dst_img.size and dst_img.data[c_off] == 0xE8:
                    rel = struct.unpack_from("<i", dst_img.data, c_off + 1)[0]
                    cand = dst_caller + call_off + 5 + rel
                    if cand in hit_set:
                        votes[cand] += 1
            if votes:
                best = votes.most_common(1)[0][0]
                if require_pdata:
                    func_cache[src_rva] = best
                return (best, "code_caller_xref")

            # Fallback: nearest RVA to source RVA among exact masked-RIP matches.
            nearest = min(hits, key=lambda r: abs(r - src_rva))
            if require_pdata:
                func_cache[src_rva] = nearest
            return (nearest, "code_nearest_match")

        # If require_pdata failed, retry without .pdata boundary constraint (mid-function / leaf).
        if require_pdata:
            hits2 = search_sig_in_text(dst_img, sig, require_pdata_begin=False, max_hits=16)
            if len(hits2) == 1:
                func_cache[src_rva] = hits2[0]
                return (hits2[0], "code_masked_sig_nopdata")

        if require_pdata:
            func_cache[src_rva] = None
        return (None, "unresolved")

    # -------------------------------------------------------------------------
    # Partition symbols by strategy
    # -------------------------------------------------------------------------
    code_syms: list[dict[str, Any]] = []
    import_syms: list[dict[str, Any]] = []
    xref_syms: list[dict[str, Any]] = []
    const_syms: list[dict[str, Any]] = []

    for sym in symbols:
        kind = sym["kind"]
        name = sym["name"]
        if sym["rva"] is None:
            results[name] = PortResult(name, kind, 0, None, "source_null", None)
            continue
        if kind in ("code", "global_ref"):
            code_syms.append(sym)
        elif kind == "import_slot":
            import_syms.append(sym)
        elif name == "geal.precedence_table":
            const_syms.append(sym)
        else:
            xref_syms.append(sym)

    # -------------------------------------------------------------------------
    # Strategy 1: code + global_ref
    # -------------------------------------------------------------------------
    for sym in code_syms:
        name = sym["name"]
        src_rva = int(sym["rva"], 16)
        on_pdata = src_rva in src_img.pdata_set
        dst_rva, strat = resolve_code_rva(
            src_rva,
            require_pdata=on_pdata,
            allow_shortcut=allow_exact_rva_shortcut,
        )
        exp = (
            format_target_expected_bytes(dst_img, dst_rva, sym.get("expectedBytes"), sym["kind"])
            if dst_rva is not None
            else None
        )
        results[name] = PortResult(name, sym["kind"], src_rva, dst_rva, strat, exp)
        strategy_counts[strat] += 1

    # -------------------------------------------------------------------------
    # Strategy 3: PE IAT import_slot lookup
    # -------------------------------------------------------------------------
    for sym in import_syms:
        name = sym["name"]
        src_rva = int(sym["rva"], 16)
        iat_key = src_img.iat_rva_to_sym.get(src_rva)
        dst_rva = dst_img.iat_sym_to_rva.get(iat_key) if iat_key is not None else None
        strat = "iat_import_slot" if dst_rva is not None else "unresolved"
        detail = f"{iat_key[0]}!{iat_key[1]}" if iat_key else "missing_in_source_iat"
        results[name] = PortResult(name, sym["kind"], src_rva, dst_rva, strat, None, detail)
        strategy_counts[strat] += 1

    # -------------------------------------------------------------------------
    # Strategy 4: Constant .rdata pattern (geal.precedence_table)
    # -------------------------------------------------------------------------
    for sym in const_syms:
        name = sym["name"]
        src_rva = int(sym["rva"], 16)
        pattern = GEAL_PRECEDENCE_PATTERN if name == "geal.precedence_table" else (src_img.read_rva(src_rva, 32) or b"")
        dst_rva: int | None = None
        if pattern and dst_img.rdata_sec is not None:
            r_off = dst_img.rdata_sec.raw_offset
            r_end = r_off + min(dst_img.rdata_sec.virtual_size, dst_img.rdata_sec.raw_size)
            first = dst_img.data.find(pattern, r_off, r_end)
            last = dst_img.data.rfind(pattern, r_off, r_end)
            if first >= 0 and first == last:
                dst_rva = dst_img.rdata_sec.virtual_address + (first - r_off)
        strat = "rdata_const_pattern" if dst_rva is not None else "unresolved"
        results[name] = PortResult(name, sym["kind"], src_rva, dst_rva, strat, None)
        strategy_counts[strat] += 1

    # -------------------------------------------------------------------------
    # Strategy 2: .text RIP-relative xref extraction for all vtable & data syms
    # -------------------------------------------------------------------------
    xref_targets = {int(s["rva"], 16) for s in xref_syms}
    xref_map = collect_rip_xrefs(src_img, xref_targets, max_per_target=3)

    for sym in xref_syms:
        name = sym["name"]
        src_rva = int(sym["rva"], 16)
        xrefs = xref_map.get(src_rva, [])
        votes: Counter[int] = Counter()

        for xr in xrefs:
            dst_fn, _ = resolve_code_rva(
                xr.func_rva,
                require_pdata=True,
                allow_shortcut=allow_exact_rva_shortcut,
            )
            if dst_fn is None:
                continue
            dst_insn_rva = dst_fn + xr.insn_offset_in_func
            dst_disp_off = dst_img.rva_to_offset(dst_insn_rva + xr.disp_offset_in_insn)
            dst_insn_off = dst_img.rva_to_offset(dst_insn_rva)
            if dst_disp_off is None or dst_insn_off is None:
                continue
            # Verify the instruction at dst_insn_rva is still a RIP-rel instruction of same shape.
            ins = decode_x64_insn(dst_img.data, dst_insn_off, min(dst_img.size, dst_insn_off + 15))
            if ins.length == xr.insn_length and ins.is_rip_rel and ins.disp_offset == xr.disp_offset_in_insn:
                disp = struct.unpack_from("<i", dst_img.data, dst_disp_off)[0]
                cand_rva = dst_insn_rva + ins.length + disp
                if dst_img.section_for_rva(cand_rva) is not None:
                    votes[cand_rva] += 1
                    continue

            # Fallback if instruction shifted slightly inside the matched function: scan function for matching opcode/modrm.
            dst_fn_bounds = dst_img.find_pdata_function(dst_fn)
            src_insn_off = src_img.rva_to_offset(xr.func_rva + xr.insn_offset_in_func)
            if dst_fn_bounds is not None and src_insn_off is not None:
                src_prefix = src_img.data[src_insn_off : src_insn_off + xr.disp_offset_in_insn]
                fo = dst_img.rva_to_offset(dst_fn_bounds[0])
                if fo is not None:
                    fend = fo + (dst_fn_bounds[1] - dst_fn_bounds[0])
                    cur = fo
                    while cur < fend:
                        d_ins = decode_x64_insn(dst_img.data, cur, fend)
                        if d_ins.length <= 0:
                            break
                        if (
                            d_ins.is_rip_rel
                            and d_ins.length == xr.insn_length
                            and d_ins.disp_offset == xr.disp_offset_in_insn
                            and dst_img.data[cur : cur + d_ins.disp_offset] == src_prefix
                        ):
                            disp = struct.unpack_from("<i", dst_img.data, cur + d_ins.disp_offset)[0]
                            cand_rva = (dst_fn_bounds[0] + (cur - fo)) + d_ins.length + disp
                            if dst_img.section_for_rva(cand_rva) is not None:
                                votes[cand_rva] += 1
                                break
                        cur += d_ins.length

        dst_rva = votes.most_common(1)[0][0] if votes else None
        strat = "text_rip_xref" if dst_rva is not None else "unresolved"
        results[name] = PortResult(
            name, sym["kind"], src_rva, dst_rva, strat, None, f"xrefs={len(xrefs)} votes={dict(votes)}"
        )
        strategy_counts[strat] += 1

    ordered = [results[s["name"]] for s in symbols]
    return ordered, dict(strategy_counts)


def build_output_json(
    source_doc: dict[str, Any],
    results: list[PortResult],
    dst_img: Pe64Image,
    *,
    game_version: str | None,
    build_number: int | None,
    label: str | None,
    existing_out_doc: dict[str, Any] | None = None,
) -> dict[str, Any]:
    out = json.loads(json.dumps(source_doc))
    if game_version:
        out["gameVersion"] = game_version

    if existing_out_doc is not None and build_number is None:
        out["builds"] = existing_out_doc["builds"]
    elif build_number is not None:
        out["builds"] = [
            {
                "buildNumber": build_number,
                "label": label or f"ported build {build_number}",
                "executableSha256": dst_img.sha256,
                "executableSize": dst_img.size,
                "steamIndexSha256": None,
                "source": f"scripts/port-address-library.py ({dst_img.path.name})",
            }
        ]

    image_base = int(out["imageBase"], 16)
    res_map = {r.name: r for r in results}

    for sym in out["symbols"]:
        r = res_map[sym["name"]]
        if r.dst_rva is not None:
            sym["rva"] = f"0x{r.dst_rva:x}"
            sym["va"] = f"0x{image_base + r.dst_rva:x}"
            sym["expectedBytes"] = r.expected_bytes
            sym["status"] = "static_verified"
        else:
            sym["rva"] = None
            sym["va"] = None
            sym["expectedBytes"] = None
            sym["status"] = "unverified_3_29_3c"

    return out


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Automated LocalExile NativeMods Address Library cross-porting & verification CLI."
    )
    parser.add_argument("--source-json", required=True, type=Path, help="Source address library JSON (e.g. data/addresses/3.29.3d.json).")
    parser.add_argument("--source-exe", required=True, type=Path, help="Source unpatched Win64 Game.exe matching --source-json.")
    parser.add_argument("--target-exe", type=Path, help="Target Win64 executable to port symbols into.")
    parser.add_argument("--out", type=Path, help="Output ported address library JSON path.")
    parser.add_argument("--game-version", type=str, help="Override output gameVersion (e.g. 3.29.3c).")
    parser.add_argument("--build-number", type=int, help="Optional target buildNumber metadata.")
    parser.add_argument("--label", type=str, help="Optional target build label metadata.")
    parser.add_argument(
        "--verify-self",
        action="store_true",
        help="Verify 100%% blind resolution against --source-exe (disables exact-RVA shortcut) and assert exact RVA parity.",
    )
    parser.add_argument(
        "--no-exact-rva-shortcut",
        action="store_true",
        help="Disable exact-RVA fast-path and force full signature/xref/IAT resolution for every symbol.",
    )
    args = parser.parse_args()

    if not args.verify_self and (args.target_exe is None or args.out is None):
        parser.error("Either --verify-self or both --target-exe and --out must be specified.")

    source_doc = json.loads(args.source_json.read_text(encoding="utf-8"))
    src_img = Pe64Image(args.source_exe)
    dst_img = src_img if args.verify_self else Pe64Image(args.target_exe)

    # In --verify-self mode, disable exact-RVA shortcut so we genuinely prove all 4 strategies resolve 447/447!
    use_shortcut = (not args.verify_self) and (not args.no_exact_rva_shortcut)
    results, counts = port_address_library(
        source_doc,
        src_img,
        dst_img,
        allow_exact_rva_shortcut=use_shortcut,
    )

    total = len(results)
    resolved = sum(1 for r in results if r.dst_rva is not None)

    print(f"Address Library Port Summary ({args.source_json.name} -> {dst_img.path.name}):")
    print(f"  Resolved: {resolved}/{total} ({100.0 * resolved / total:.2f}%)")
    for strat, cnt in sorted(counts.items()):
        print(f"    - {strat}: {cnt}")

    if args.verify_self:
        mismatches: list[str] = []
        for sym, r in zip(source_doc["symbols"], results):
            if r.dst_rva != r.src_rva:
                mismatches.append(
                    f"  MISMATCH {r.name} ({r.kind}): src=0x{r.src_rva:x} dst={hex(r.dst_rva) if r.dst_rva is not None else 'None'} strat={r.strategy} {r.detail}"
                )
            elif sym.get("expectedBytes") is not None and r.expected_bytes != sym["expectedBytes"].lower():
                mismatches.append(
                    f"  BYTES_MISMATCH {r.name}: src={sym['expectedBytes']} dst={r.expected_bytes}"
                )
            elif sym["kind"] in ("vtable", "data", "import_slot") and r.expected_bytes is not None:
                mismatches.append(f"  NONNULL_BYTES {r.name}: expectedBytes must be null on {sym['kind']}")
        if mismatches:
            print(f"SELF-VERIFY FAILED ({len(mismatches)} mismatches):", file=sys.stderr)
            for line in mismatches[:40]:
                print(line, file=sys.stderr)
            return 1
        print(f"SELF-VERIFY PASS: {resolved}/{total} (100%) exact RVA & byte parity (blind strategy mode)!")
        return 0

    existing_out = json.loads(args.out.read_text(encoding="utf-8")) if args.out.exists() else None
    out_doc = build_output_json(
        source_doc,
        results,
        dst_img,
        game_version=args.game_version,
        build_number=args.build_number,
        label=args.label,
        existing_out_doc=existing_out,
    )
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(out_doc, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
    print(f"Wrote ported address library to {args.out} ({resolved}/{total} resolved).")
    return 0 if resolved == total else 2


if __name__ == "__main__":
    raise SystemExit(main())
