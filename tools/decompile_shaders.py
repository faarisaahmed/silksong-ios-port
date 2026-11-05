"""Rebuild the game's shaders from the PC build's compiled Direct3D 11 code.

AssetRipper (free) exports shaders as empty placeholders, and gen_shaders.py used to stand in for each one with an
approximation guessed from its properties. This reads the real compiled shaders from the PC game instead and turns
each one back into ShaderLab + HLSL that Unity can compile for Metal:

  - render state (blend, depth, culling, stencil, colour mask, tags, GrabPass passes) comes from the serialized pass
  - every vertex/fragment program is a D3D11 (SM4/SM5) bytecode program; it's translated instruction by instruction
    into HLSL (registers become uint4 variables holding raw bits, so integer and float instructions both stay exact)
  - constant buffer slots and texture/sampler registers get their names back from Unity's parameter tables
  - each keyword variant becomes an #if branch, so materials and scripts toggle the same keywords as on PC

Usage: decompile_shaders.py <game _Data dir> <Unity CGIncludes dir> <out dir>
Writes <out dir>/<shader name, / replaced by _>.shaderlab: the shader's SubShader blocks (without Properties, which
the ripped project already has). gen_shaders.py uses these in place of its approximations. Needs UnityPy.
"""
import glob
import os
import re
import struct
import sys
import warnings

D3D11 = 4  # ShaderCompilerPlatform
SKIP_KEYWORDS = {"INSTANCING_ON", "PROCEDURAL_INSTANCING_ON", "STEREO_INSTANCING_ON", "UNITY_SINGLE_PASS_STEREO",
                 "STEREO_MULTIVIEW_ON", "STEREO_CUBEMAP_RENDER_ON", "DOTS_INSTANCING_ON"}


class Unsupported(Exception):
    pass


# ------------------------------------------------------------------------------------------------ DXBC reading

OPCODES = ("add and break breakc call callc case continue continuec cut default deriv_rtx deriv_rty discard div dp2 "
           "dp3 dp4 else emit emitthencut endif endloop endswitch eq exp frc ftoi ftou ge iadd if ieq ige ilt imad imax "
           "imin imul ine ineg ishl ishr itof label ld ld_ms log loop lt mad min max customdata mov movc mul ne nop not "
           "or resinfo ret retc round_ne round_ni round_pi round_z rsq sample sample_c sample_c_lz sample_l sample_d "
           "sample_b sqrt switch sincos udiv ult uge umul umad umax umin ushr utof xor dcl_resource dcl_constantbuffer "
           "dcl_sampler dcl_index_range dcl_gs_output_topology dcl_gs_input_primitive dcl_max_output_vertex_count "
           "dcl_input dcl_input_sgv dcl_input_siv dcl_input_ps dcl_input_ps_sgv dcl_input_ps_siv dcl_output "
           "dcl_output_sgv dcl_output_siv dcl_temps dcl_indexable_temp dcl_global_flags").split()
OPCODES += [None] * (128 - len(OPCODES))
OPCODES[107:] = [None] * 21
SM5 = {108: "lod", 109: "gather4", 110: "sample_pos", 111: "sample_info", 113: "hs_decls", 123: "deriv_rtx_coarse",
       124: "deriv_rtx_fine", 125: "deriv_rty_coarse", 126: "deriv_rty_fine", 127: "gather4_c", 128: "gather4_po",
       129: "gather4_po_c", 130: "rcp", 131: "f32tof16", 132: "f16tof32", 133: "uaddc", 134: "usubb",
       135: "countbits", 136: "firstbit_hi", 137: "firstbit_lo", 138: "firstbit_shi", 139: "ubfe", 140: "ibfe",
       141: "bfi", 142: "bfrev", 143: "swapc", 162: "ld_uav_typed", 163: "store_uav_typed",
       165: "ld_raw", 166: "store_raw", 167: "ld_structured", 168: "store_structured", 196: "dcl_gs_instances",
       160: "dcl_uav_typed", 161: "dcl_uav_raw", 164: "dcl_resource_raw", 162 + 100: None, 157: "dcl_function_body",
       158: "dcl_function_table", 159: "dcl_interface", 154: "dcl_thread_group", 155: "dcl_uav_structured",
       153: "dcl_stream", 106 + 100: None}


def opname(op):
    if op < 107:
        return OPCODES[op]
    return SM5.get(op, "op%d" % op)


class Operand:
    __slots__ = ("type", "ncomp", "mode", "comps", "index", "mod", "imm")

    def __repr__(self):
        return "Operand(%s %s %s %s)" % (self.type, self.comps, self.index, self.imm)


def read_operand(tok, i):
    t = tok[i]
    i += 1
    o = Operand()
    o.ncomp = t & 3
    o.mode = (t >> 2) & 3
    o.type = (t >> 12) & 0xff
    idxdim = (t >> 20) & 3
    o.mod = 0
    ext = (t >> 31) & 1
    while ext:
        e = tok[i]
        i += 1
        if (e & 0x3f) == 1:
            o.mod = (e >> 6) & 0xff
        ext = (e >> 31) & 1
    o.comps = None
    if o.ncomp == 2:
        if o.mode == 0:      # mask
            mask = (t >> 4) & 0xf
            o.comps = [k for k in range(4) if mask & (1 << k)]
        elif o.mode == 1:    # swizzle
            sw = (t >> 4) & 0xff
            o.comps = [(sw >> (2 * k)) & 3 for k in range(4)]
        else:                # select 1
            o.comps = [(t >> 4) & 3] * 4
    elif o.ncomp == 1:
        o.comps = [0, 0, 0, 0]
    o.imm = None
    if o.type in (4, 5):
        if o.type == 5:
            raise Unsupported("64-bit immediate")
        n = 4 if o.ncomp == 2 else 1
        o.imm = list(tok[i:i + n])
        i += n
        if n == 1:
            o.imm = o.imm * 4
        o.comps = [0, 1, 2, 3]
    o.index = []
    for d in range(idxdim):
        rep = (t >> (22 + 3 * d)) & 7
        if rep == 0:
            o.index.append((tok[i], None))
            i += 1
        elif rep == 2:
            sub, i = read_operand(tok, i)
            o.index.append((0, sub))
        elif rep == 3:
            base = tok[i]
            i += 1
            sub, i = read_operand(tok, i)
            o.index.append((base, sub))
        else:
            raise Unsupported("operand index representation %d" % rep)
    return o, i


class Instr:
    __slots__ = ("op", "name", "tok", "ops", "sat", "nz", "offsets", "extra")


def parse_program(code):
    tok = struct.unpack("<%dI" % (len(code) // 4), code)
    kind = tok[0] >> 16
    i = 2
    instrs = []
    while i < len(tok):
        t = tok[i]
        op = t & 0x7ff
        if op == 53:  # customdata
            length = tok[i + 1]
            cls = t >> 11
            ins = Instr()
            ins.op, ins.name, ins.tok, ins.ops = op, "customdata", t, []
            ins.extra = (cls, tok[i + 2:i + length])
            instrs.append(ins)
            i += length
            continue
        length = (t >> 24) & 0x7f
        if length == 0:
            raise Unsupported("zero-length instruction %d" % op)
        end = i + length
        j = i + 1
        ins = Instr()
        ins.op, ins.name, ins.tok = op, opname(op), t
        ins.sat = bool((t >> 13) & 1)
        ins.nz = bool((t >> 18) & 1)
        ins.offsets = None
        ins.extra = None
        ext = (t >> 31) & 1
        while ext:
            e = tok[j]
            j += 1
            if (e & 0x3f) == 1:  # sample controls (immediate texel offset)
                def s4(v):
                    return v - 16 if v >= 8 else v
                ins.offsets = (s4((e >> 9) & 15), s4((e >> 13) & 15), s4((e >> 17) & 15))
            ext = (e >> 31) & 1
        ops = []
        if ins.name == "dcl_temps":
            ins.extra = tok[j]
        elif ins.name == "dcl_indexable_temp":
            ins.extra = tok[j:j + 3]
        elif ins.name in ("dcl_global_flags", None):
            pass
        elif ins.name == "dcl_resource":
            o, j = read_operand(tok, j)
            ops.append(o)
            ins.extra = (t >> 11) & 0x1f
        elif ins.name in ("dcl_input_sgv", "dcl_input_siv", "dcl_input_ps_sgv", "dcl_input_ps_siv",
                          "dcl_output_sgv", "dcl_output_siv"):
            o, j = read_operand(tok, j)
            ops.append(o)
            ins.extra = tok[j]
        elif ins.name and ins.name.startswith("dcl_") or ins.name in ("dcl_index_range",):
            if j < end:
                o, j = read_operand(tok, j)
                ops.append(o)
        else:
            while j < end:
                o, j = read_operand(tok, j)
                ops.append(o)
        ins.ops = ops
        instrs.append(ins)
        i = end
    return kind, instrs


def dxbc_chunks(data):
    if data[:4] != b"DXBC":
        raise Unsupported("not a DXBC container")
    n = struct.unpack_from("<I", data, 28)[0]
    res = {}
    for o in struct.unpack_from("<%dI" % n, data, 32):
        res[data[o:o + 4].decode("latin1")] = data[o + 8:o + 8 + struct.unpack_from("<I", data, o + 4)[0]]
    return res


def signature(data, osg5=False):
    cnt = struct.unpack_from("<I", data, 0)[0]
    out = []
    for e in range(cnt):
        base = 8 + e * (28 if osg5 else 24)
        if osg5:
            base += 4
        no, si, sv, ct, reg, mask = struct.unpack_from("<IIIIIB", data, base)
        name = data[no:data.index(b"\0", no)].decode()
        if name.upper().startswith("SV_"):
            name = name.upper()
        out.append((name, si, sv, ct, reg, mask))
    return out


# ------------------------------------------------------------------------------------------------ Unity data

def unpack_blob(t):
    """Per-segment decompressed blob for the D3D11 platform, and its (offset, length, segment) table."""
    import lz4.block
    plats = list(t["platforms"])
    if D3D11 not in plats:
        raise Unsupported("no D3D11 programs")
    p = plats.index(D3D11)
    blob = bytes(t["compressedBlob"])
    segs = []
    for o, c, d in zip(t["offsets"][p], t["compressedLengths"][p], t["decompressedLengths"][p]):
        segs.append(lz4.block.decompress(blob[o:o + c], uncompressed_size=d))
    first = segs[0]
    n = struct.unpack_from("<I", first, 0)[0]
    table = [struct.unpack_from("<III", first, 4 + 12 * i) for i in range(n)]
    return segs, table


class Reader:
    def __init__(self, data):
        self.d = data
        self.p = 0

    def u32(self):
        v = struct.unpack_from("<I", self.d, self.p)[0]
        self.p += 4
        return v

    def i32(self):
        v = struct.unpack_from("<i", self.d, self.p)[0]
        self.p += 4
        return v

    def string(self):
        n = self.u32()
        s = self.d[self.p:self.p + n].decode("utf-8", "replace")
        self.p += (n + 3) & ~3
        return s

    def bytes(self):
        n = self.u32()
        b = self.d[self.p:self.p + n]
        self.p += (n + 3) & ~3
        return b


def read_program_blob(data):
    r = Reader(data)
    r.u32()  # version
    r.u32()  # program type
    r.p += 16
    keywords = [r.string() for _ in range(r.u32())]
    code = r.bytes()
    k = code.find(b"DXBC")
    if k < 0:
        raise Unsupported("program without DXBC")
    return keywords, code[k:]


class Params:
    """Names for a program's constant buffer slots and resource registers."""

    def __init__(self):
        self.cbs = {}       # cb name -> {"size": n, "vars": [(name, type, rows, cols, is_matrix, array, offset)]}
        self.bindings = {}  # register -> cb name
        self.textures = {}  # t register -> (name, sampler register)

    def add_cb(self, name, size, var):
        cb = self.cbs.setdefault(name, {"size": size, "vars": []})
        cb["size"] = max(cb["size"], size)
        if var[0] not in [v[0] for v in cb["vars"]]:
            cb["vars"].append(var)


def read_param_blob(data, params):
    r = Reader(data)
    r.u32()  # version

    def var():
        return (r.string(), r.u32(), r.u32(), r.u32(), r.u32(), r.u32(), r.u32())

    for _ in range(r.u32()):
        name, size = r.string(), r.u32()
        vs = [var() for _ in range(r.u32())]
        for _ in range(r.u32()):  # structs (instancing arrays): skip
            r.string(), r.u32(), r.u32(), r.u32()
            for _ in range(r.u32()):
                var()
        params.cbs.setdefault(name, {"size": size, "vars": []})
        for v in vs:
            params.add_cb(name, size, v)
    for _ in range(r.u32()):
        name, kind = r.string(), r.u32()
        if kind == 0:
            index, sampler, _dim = r.u32(), r.i32(), r.u32()
            params.textures[index] = (name, sampler)
        elif kind == 1:
            index, _ = r.u32(), r.u32()
            params.bindings[index] = name
        else:
            raise Unsupported("resource kind %d (%s)" % (kind, name))


def read_common_params(cp, names, params):
    for cb in cp["m_ConstantBuffers"]:
        cbname = names[cb["m_NameIndex"]]
        params.cbs.setdefault(cbname, {"size": cb["m_Size"], "vars": []})
        for v in cb["m_VectorParams"]:
            params.add_cb(cbname, cb["m_Size"], (names[v["m_NameIndex"]], v["m_Type"], 1, v["m_Dim"], 0, v["m_ArraySize"], v["m_Index"]))
        for v in cb["m_MatrixParams"]:
            params.add_cb(cbname, cb["m_Size"], (names[v["m_NameIndex"]], v["m_Type"], v["m_RowCount"], v["m_RowCount"], 1, v["m_ArraySize"], v["m_Index"]))
        if cb.get("m_StructParams"):
            pass  # instancing arrays, only in skipped variants
    for b in cp["m_ConstantBufferBindings"]:
        params.bindings[b["m_Index"]] = names[b["m_NameIndex"]]
    for tx in cp["m_TextureParams"]:
        params.textures[tx["m_Index"]] = (names[tx["m_NameIndex"]], tx["m_SamplerIndex"])
    if cp["m_BufferParams"] or cp["m_UAVParams"]:
        raise Unsupported("buffers/UAVs")


# ------------------------------------------------------------------------------------------------ translation

def hexu(v):
    return "0x%xu" % v if v > 9 else "%du" % v


def float_literal(v):
    f = struct.unpack("<f", struct.pack("<I", v))[0]
    if v == 0:
        return "0.0"
    if f != f or abs(f) == float("inf") or abs(f) < 1e-30 or abs(f) > 1e30:
        return None
    s = repr(f)
    if struct.unpack("<I", struct.pack("<f", float(s)))[0] != v:
        return None
    return s


CMP = "xyzw"


class Program:
    """One translated vertex or fragment program (one keyword variant)."""

    def __init__(self, stage, code, params, builtins, uniforms, textures):
        self.stage = stage
        self.params = params
        self.builtins = builtins
        self.uniforms = uniforms        # shared across the pass: name -> declaration line
        self.textures = textures        # shared: name -> (dim, has sampler)
        c = dxbc_chunks(code)
        prog = c.get("SHEX") or c.get("SHDR")
        if prog is None:
            raise Unsupported("no shader code chunk")
        self.inputs = signature(c["ISGN"]) if "ISGN" in c else []
        self.outputs = signature(c["OSG5"], True) if "OSG5" in c else signature(c["OSGN"]) if "OSGN" in c else []
        self.kind, self.instrs = parse_program(prog)
        self.temps = 0
        self.indexable = {}
        self.icb = None
        self.resources = {}  # t register -> dim
        self.lines = []

    # ---- operand helpers

    def cb_slot(self, cbreg, slot):
        """uint4 expression for constant buffer register cbreg[slot]."""
        cbname = self.params.bindings.get(cbreg)
        if cbname is None:
            raise Unsupported("unbound constant buffer cb%d" % cbreg)
        cb = self.params.cbs.get(cbname)
        if cb is None:
            raise Unsupported("unknown constant buffer %s" % cbname)
        comps = ["0u"] * 4
        for (name, typ, rows, cols, ismat, arr, off) in cb["vars"]:
            count = max(arr, 1)
            regs = cols if ismat else 1
            start = off // 16
            end = start + regs * count
            if not (start <= slot < end):
                continue
            self.declare_uniform(cbname, name, typ, rows, cols, ismat, arr, off)
            rel = slot - start
            elem, col = divmod(rel, regs)
            # Unity's shader upgrader rewrites this name on import (which would make gen_shaders.py see a change)
            ref = {"glstate_matrix_projection": "UNITY_MATRIX_P"}.get(name, name) + ("[%d]" % elem if arr else "")
            if ismat:
                for r_ in range(rows):
                    comps[r_] = "asuint(float(%s[%d][%d]))" % (ref, r_, col)
            else:
                base = (off % 16) // 4
                for k in range(cols):
                    c = base + k
                    if c < 4:
                        v = ref if cols == 1 else "%s.%s" % (ref, CMP[k])
                        if typ == 2:
                            v = "(%s ? 0xffffffffu : 0u)" % v
                            comps[c] = v
                        else:  # (some built-ins are half4, which asuint doesn't take)
                            comps[c] = "asuint(%s(%s))" % ("float" if typ == 0 else "int" if typ == 1 else "uint", v)
        return "uint4(%s)" % ", ".join(comps)

    def cb_dynamic(self, cbreg, base, idxexpr):
        cbname = self.params.bindings.get(cbreg)
        cb = self.params.cbs.get(cbname) if cbname else None
        if cb is None:
            raise Unsupported("unbound constant buffer cb%d" % cbreg)
        for (name, typ, rows, cols, ismat, arr, off) in cb["vars"]:
            start = off // 16
            regs = cols if ismat else 1
            if arr and start <= base < start + regs * arr:
                self.declare_uniform(cbname, name, typ, rows, cols, ismat, arr, off)
                if ismat:
                    rel = base - start
                    return ("asuint(transpose(%s[(%s + %d) / %d])[(%s + %d) %% %d])"
                            % (name, idxexpr, rel, regs, idxexpr, rel, regs)) if rows == 4 and cols == 4 else self._fail("matrix array")
                if cols != 4:
                    v = "%s[%s + %d]" % (name, idxexpr, base - start)
                    pad = ", ".join(["asuint(%s)" % v] + ["0u"] * (4 - cols)) if cols == 1 else "asuint(%s), %s" % (v, ", ".join(["0u"] * (4 - cols)))
                    return "uint4(%s)" % pad
                return "asuint(%s4(%s[%s + %d]))" % ("float" if typ == 0 else "int", name, idxexpr, base - start)
        raise Unsupported("dynamic constant index outside an array (cb%d[%d])" % (cbreg, base))

    def _fail(self, why):
        raise Unsupported(why)

    def declare_uniform(self, cbname, name, typ, rows, cols, ismat, arr, off):
        if name in self.builtins.conditional:
            raise Unsupported("built-in %s that Unity only declares on some platforms" % name)
        if name in self.builtins:
            return
        base = {0: "float", 1: "int", 2: "bool", 3: "uint"}.get(typ)
        if base is None:
            raise Unsupported("constant type %d" % typ)
        t = base + ("%dx%d" % (rows, cols) if ismat else str(cols) if cols > 1 else "")
        decl = "%s %s%s;" % (t, name, "[%d]" % arr if arr else "")
        # Only Unity's own buffers (UnityPerDrawSprite...) have to keep their layout; the rest are bound by name, and
        # their members move between variants as unused ones are stripped.
        off = off if cbname.startswith("Unity") else None
        prev = self.uniforms.get(name)
        if prev and (prev[1] != decl or prev[2] != off):
            raise Unsupported("uniform %s declared two ways" % name)
        self.uniforms[name] = (cbname, decl, off)

    def index_expr(self, idx):
        base, sub = idx
        if sub is None:
            return str(base), base, None
        e = "int(%s)" % self.src(sub, "i", scalar=True)
        return ("%s + %d" % (e, base) if base else e), base, e

    def raw(self, o):
        """uint4 expression of an operand without swizzle."""
        t = o.type
        if t == 0:
            return "r%d" % o.index[0][0]
        if t == 1:
            if len(o.index) != 1 or o.index[0][1] is not None:
                raise Unsupported("indexed input")
            return "v%d" % o.index[0][0]
        if t == 2:
            return "o%d" % o.index[0][0]
        if t == 3:
            e, _, _ = self.index_expr(o.index[1])
            return "x%d[%s]" % (o.index[0][0], e)
        if t == 4:
            return "uint4(%s)" % ", ".join(hexu(v) for v in o.imm)
        if t == 8:
            cbreg = o.index[0][0]
            base, sub = o.index[1]
            if sub is None:
                return self.cb_slot(cbreg, base)
            e = "int(%s)" % self.src(sub, "i", scalar=True)
            return self.cb_dynamic(cbreg, base, e)
        if t == 9:
            e, _, _ = self.index_expr(o.index[0])
            return "icb[%s]" % e
        if t == 12:
            return "oDepth"
        raise Unsupported("operand type %d" % t)

    def src(self, o, kind, scalar=False):
        """Source operand as an expression of the given type: f float4, i int4, u uint4 (swizzled to 4 comps)."""
        if o.type == 4 and kind == "f":
            lits = [float_literal(v) for v in o.imm]
            if all(l is not None for l in lits):
                vals = [lits[c] for c in o.comps]
                e = "float4(%s)" % ", ".join(vals)
                return self.apply_mod(e, o, kind, scalar)
        sw = "".join(CMP[c] for c in o.comps) if o.comps else "xxxx"
        r = self.raw(o)
        e = "%s.%s" % (r if not r.startswith("uint4(") else "(%s)" % r, sw[0] if scalar else sw)
        if kind == "f":
            e = "asfloat(%s)" % e
        elif kind == "i":
            e = "asint(%s)" % e
        return self.apply_mod(e, o, kind, scalar)

    def apply_mod(self, e, o, kind, scalar):
        if scalar and e.startswith("float4("):
            e = "(%s).x" % e
        if o.mod & 2:
            e = "abs(%s)" % e
        if o.mod & 1:
            e = "(-%s)" % e
        return e

    def dest(self, o):
        r = self.raw(o)
        mask = "".join(CMP[c] for c in o.comps) if o.comps else "x"
        return r, mask

    def emit(self, s):
        self.lines.append("\t" * self.depth + s)

    def assign(self, o, expr, kind, sat=False):
        """dest = expr (expr is a 4-wide expression of type kind)."""
        if o.type == 13:  # null
            return
        if sat:
            if kind != "f":
                raise Unsupported("saturate on non-float")
            expr = "saturate(%s)" % expr
        r, mask = self.dest(o)
        if o.type == 12:
            self.emit("oDepth = %s.x;" % (expr if kind == "f" else "asfloat(%s)" % expr))
            return
        conv = {"f": "asuint", "i": "asuint", "u": ""}[kind]
        self.emit("%s.%s = %s(%s).%s;" % (r, mask, conv, expr, mask))

    # ---- translation

    def translate(self):
        self.depth = 1
        B = {"add": "{0} + {1}", "mul": "{0} * {1}", "div": "{0} / {1}", "mad": "{0} * {1} + {2}",
             "min": "min({0}, {1})", "max": "max({0}, {1})", "frc": "frac({0})", "exp": "exp2({0})",
             "log": "log2({0})", "sqrt": "sqrt({0})", "rsq": "rsqrt({0})", "rcp": "rcp({0})",
             "round_ne": "round({0})", "round_ni": "floor({0})", "round_pi": "ceil({0})", "round_z": "trunc({0})",
             "deriv_rtx": "ddx({0})", "deriv_rty": "ddy({0})", "deriv_rtx_coarse": "ddx({0})",
             "deriv_rtx_fine": "ddx_fine({0})", "deriv_rty_coarse": "ddy({0})", "deriv_rty_fine": "ddy_fine({0})"}
        I = {"iadd": "{0} + {1}", "imul": None, "imad": "{0} * {1} + {2}", "imax": "max({0}, {1})",
             "imin": "min({0}, {1})", "ineg": "-{0}", "ishl": "{0} << ({1} & 31)", "ishr": "{0} >> ({1} & 31)"}
        U = {"and": "{0} & {1}", "or": "{0} | {1}", "xor": "{0} ^ {1}", "not": "~{0}", "ushr": "{0} >> ({1} & 31)",
             "umax": "max({0}, {1})", "umin": "min({0}, {1})", "umad": "{0} * {1} + {2}",
             "countbits": "countbits({0})", "bfrev": "reversebits({0})", "firstbit_lo": "firstbitlow({0})"}
        CMPF = {"eq": "==", "ne": "!=", "lt": "<", "ge": ">="}
        CMPI = {"ieq": "==", "ine": "!=", "ilt": "<", "ige": ">="}
        CMPU = {"ult": "<", "uge": ">="}
        for ins in self.instrs:
            n = ins.name
            ops = ins.ops
            if n is None:
                raise Unsupported("opcode %d" % ins.op)
            if n == "customdata":
                cls, data = ins.extra
                if cls == 3:
                    vals = list(data)
                    self.icb = ["uint4(%s)" % ", ".join(hexu(v) for v in vals[k:k + 4]) for k in range(0, len(vals), 4)]
                continue
            if n == "dcl_temps":
                self.temps = ins.extra
                continue
            if n == "dcl_indexable_temp":
                reg, size, _ = ins.extra
                self.indexable[reg] = size
                continue
            if n == "dcl_resource":
                o = ops[0]
                dim = ins.extra
                if dim not in (3, 5, 6, 8):
                    raise Unsupported("resource dimension %d" % dim)
                self.resources[o.index[0][0]] = dim
                continue
            if n.startswith("dcl_") or n in ("nop", "label"):
                continue
            if n in B:
                if n in ("deriv_rtx", "deriv_rty", "deriv_rtx_coarse", "deriv_rty_coarse", "deriv_rtx_fine",
                         "deriv_rty_fine") and self.stage != "frag":
                    raise Unsupported("derivative outside a fragment program")
                args = [self.src(o, "f") for o in ops[1:]]
                self.assign(ops[0], "(" + B[n].format(*args) + ")", "f", ins.sat)
            elif n in ("dp2", "dp3", "dp4"):
                k = int(n[2])
                a, b = self.src(ops[1], "f"), self.src(ops[2], "f")
                sw = "xyzw"[:k]
                self.assign(ops[0], "dot((%s).%s, (%s).%s).xxxx" % (a, sw, b, sw), "f", ins.sat)
            elif n == "mov":
                if ins.sat or ops[1].mod:
                    self.assign(ops[0], self.src(ops[1], "f"), "f", ins.sat)
                else:
                    self.assign(ops[0], self.src(ops[1], "u"), "u")
            elif n == "movc":
                c = self.src(ops[1], "u")
                a, b = (self.src(ops[2], "f"), self.src(ops[3], "f")) if ins.sat else (self.src(ops[2], "u"), self.src(ops[3], "u"))
                e = "((%s != 0u) ? %s : %s)" % (c, a, b)
                self.assign(ops[0], e, "f" if ins.sat else "u", ins.sat)
            elif n == "swapc":
                raise Unsupported("swapc")
            elif n in CMPF:
                e = "asuint(-(int4)(%s %s %s))" % (self.src(ops[1], "f"), CMPF[n], self.src(ops[2], "f"))
                self.assign(ops[0], e, "u")
            elif n in CMPI:
                e = "asuint(-(int4)(%s %s %s))" % (self.src(ops[1], "i"), CMPI[n], self.src(ops[2], "i"))
                self.assign(ops[0], e, "u")
            elif n in CMPU:
                e = "asuint(-(int4)(%s %s %s))" % (self.src(ops[1], "u"), CMPU[n], self.src(ops[2], "u"))
                self.assign(ops[0], e, "u")
            elif n in I and I[n]:
                args = [self.src(o, "i") for o in ops[1:]]
                self.assign(ops[0], "(" + I[n].format(*args) + ")", "i")
            elif n in U:
                args = [self.src(o, "u") for o in ops[1:]]
                self.assign(ops[0], "(" + U[n].format(*args) + ")", "u")
            elif n in ("imul", "umul"):
                k = "i" if n == "imul" else "u"
                if ops[0].type != 13:
                    raise Unsupported("high half of a multiply")
                self.assign(ops[1], "(%s * %s)" % (self.src(ops[2], k), self.src(ops[3], k)), k)
            elif n == "udiv":
                a, b = self.src(ops[2], "u"), self.src(ops[3], "u")
                self.assign(ops[0], "(%s / %s)" % (a, b), "u")
                self.assign(ops[1], "(%s %% %s)" % (a, b), "u")
            elif n == "ftoi":
                self.assign(ops[0], "int4(%s)" % self.src(ops[1], "f"), "i")
            elif n == "ftou":
                self.assign(ops[0], "uint4(%s)" % self.src(ops[1], "f"), "u")
            elif n == "itof":
                self.assign(ops[0], "float4(%s)" % self.src(ops[1], "i"), "f", ins.sat)
            elif n == "utof":
                self.assign(ops[0], "float4(%s)" % self.src(ops[1], "u"), "f", ins.sat)
            elif n == "sincos":
                a = self.src(ops[2], "f")
                if ops[0].type != 13:
                    self.assign(ops[0], "sin(%s)" % a, "f", ins.sat)
                if ops[1].type != 13:
                    self.assign(ops[1], "cos(%s)" % a, "f", ins.sat)
            elif n in ("ubfe", "ibfe"):
                k = "u" if n == "ubfe" else "i"
                w, off, v = self.src(ops[1], "u"), self.src(ops[2], "u"), self.src(ops[3], k)
                # ((v << (32 - w - off)) >> (32 - w)), w == 0 gives 0
                e = "((%s & 31u) == 0u ? (%s)0 : (%s << ((32u - (%s & 31u) - (%s & 31u)) & 31u)) >> (32u - (%s & 31u)))" % (
                    w, "int4" if k == "i" else "uint4", v, w, off, w)
                self.assign(ops[0], e, k)
            elif n == "bfi":
                w, off, a, b = (self.src(o, "u") for o in ops[1:5])
                mask = "((((1u << (%s & 31u)) - 1u) << (%s & 31u)))" % (w, off)
                e = "(((%s << (%s & 31u)) & %s) | (%s & ~%s))" % (a, off, mask, b, mask)
                self.assign(ops[0], e, "u")
            elif n in ("sample", "sample_l", "sample_b", "sample_d", "sample_c", "sample_c_lz", "ld", "resinfo",
                       "lod", "gather4"):
                self.sample(ins)
            elif n == "if":
                self.emit("if (%s %s 0u) {" % (self.src(ops[0], "u", scalar=True), "!=" if ins.nz else "=="))
                self.depth += 1
            elif n == "else":
                self.depth -= 1
                self.emit("} else {")
                self.depth += 1
            elif n == "endif" or n == "endloop" or n == "endswitch":
                self.depth -= 1
                self.emit("}")
            elif n == "loop":
                self.emit("[loop] for (;;) {")
                self.depth += 1
            elif n == "break":
                self.emit("break;")
            elif n == "continue":
                self.emit("continue;")
            elif n in ("breakc", "continuec", "retc"):
                what = {"breakc": "break;", "continuec": "continue;", "retc": "return;"}[n]
                self.emit("if (%s %s 0u) %s" % (self.src(ops[0], "u", scalar=True), "!=" if ins.nz else "==", what))
            elif n == "discard":
                # clip() rather than discard: Metal's compiler fails on some discards inside helper functions
                self.emit("clip(%s %s 0u ? -1.0 : 1.0);" % (self.src(ops[0], "u", scalar=True), "!=" if ins.nz else "=="))
            elif n == "ret":
                self.emit("return;")
            elif n == "switch":
                self.emit("switch (%s) {" % self.src(ops[0], "i", scalar=True))
                self.depth += 1
            elif n == "case":
                self.emit("case %s:" % self.src(ops[0], "i", scalar=True))
            elif n == "default":
                self.emit("default:")
            else:
                raise Unsupported("instruction " + n)

    def texture(self, reg):
        tex = self.params.textures.get(reg)
        if tex is None:
            raise Unsupported("unnamed texture t%d" % reg)
        name = tex[0]
        dim = self.resources.get(reg, 3)
        prev = self.textures.get(name)
        if prev and prev[0] != dim:
            raise Unsupported("texture %s used with two dimensions" % name)
        self.textures[name] = (dim, prev[1] if prev else False)
        return name, dim

    def sampler(self, reg):
        for treg, (name, s) in self.params.textures.items():
            if s == reg:
                dim, _ = self.textures.get(name, (self.resources.get(treg, 3), False))
                self.textures[name] = (dim, True)
                return "sampler" + name
        raise Unsupported("sampler s%d without a texture" % reg)

    def sample(self, ins):
        n = ins.name
        ops = ins.ops
        if n == "resinfo":
            raise Unsupported("resinfo")
        if n == "lod" or n == "gather4":
            raise Unsupported(n)
        res = ops[2]
        name, dim = self.texture(res.index[0][0])
        ncoord = {3: 2, 5: 3, 6: 3, 8: 3}[dim]
        sw = "xyz"[:ncoord]
        addr = "(%s).%s" % (self.src(ops[1], "f"), sw)
        off = ""
        if ins.offsets and any(ins.offsets):
            if dim == 6:
                raise Unsupported("offset on cube sample")
            off = ", int%d(%s)" % (2 if dim in (3, 8) else 3, ", ".join(str(x) for x in ins.offsets[:2 if dim in (3, 8) else 3]))
        if n == "ld":
            if dim not in (3, 8):
                raise Unsupported("ld on dimension %d" % dim)
            a = self.src(ops[1], "i")
            loc = "int3((%s).xy, (%s).w)" % (a, a) if dim == 3 else "int4((%s).xyz, (%s).w)" % (a, a)
            e = "%s.Load(%s%s)" % (name, loc, off)
        else:
            s = self.sampler(ops[3].index[0][0])
            if n == "sample":
                if self.stage != "frag":
                    e = "%s.SampleLevel(%s, %s, 0%s)" % (name, s, addr, off)
                else:
                    e = "%s.Sample(%s, %s%s)" % (name, s, addr, off)
            elif n == "sample_l":
                e = "%s.SampleLevel(%s, %s, %s%s)" % (name, s, addr, self.src(ops[4], "f", scalar=True), off)
            elif n == "sample_b":
                e = "%s.SampleBias(%s, %s, %s%s)" % (name, s, addr, self.src(ops[4], "f", scalar=True), off)
            elif n == "sample_d":
                e = "%s.SampleGrad(%s, %s, (%s).%s, (%s).%s%s)" % (name, s, addr, self.src(ops[4], "f"), sw,
                                                                  self.src(ops[5], "f"), sw, off)
            else:
                raise Unsupported("comparison sampling")
        # The resource operand's swizzle picks components of the result.
        rs = "".join(CMP[c] for c in res.comps) if res.comps else "xyzw"
        self.assign(ops[0], "(%s).%s" % (e, rs), "f")

    # ---- function text

    def function(self, fname):
        lines = ["void %s(%s)" % (fname, ", ".join(
            ["uint4 v%d" % r for r in sorted(set(s[4] for s in self.inputs))] +
            ["inout uint4 o%d" % r for r in sorted(set(s[4] for s in self.outputs if s[0] != "SV_Depth"))] +
            (["inout float oDepth"] if any(s[0] == "SV_Depth" for s in self.outputs) else [])))]
        lines.append("{")
        for r in range(self.temps):
            lines.append("\tuint4 r%d = 0u;" % r)
        for reg, size in self.indexable.items():
            lines.append("\tuint4 x%d[%d];" % (reg, size))
            lines.append("\t[unroll] for (int xi%d = 0; xi%d < %d; xi%d++) x%d[xi%d] = 0u;" % (reg, reg, size, reg, reg, reg))
        if self.icb:
            lines.append("\tconst uint4 icb[%d] = { %s };" % (len(self.icb), ", ".join(self.icb)))
        lines += self.lines
        lines.append("}")
        return "\n".join(lines)


SEMANTIC_SV = {"SV_POSITION": "SV_POSITION", "SV_Position": "SV_POSITION"}


def sem_field(name, idx):
    return "%s%d" % (re.sub(r"\W", "_", name), idx)


def keyword_test(kws, all_kws):
    if not kws:
        return None
    return " && ".join("defined(%s)" % k for k in kws)


def build_pass(p, names, keyword_names, segs, table, builtins):
    """HLSL program text for one pass, or None if it has no programs."""
    stages = {}
    uniforms = {}
    textures = {}
    keywords_seen = set()
    for stage, key in (("vert", "progVertex"), ("frag", "progFragment")):
        sp = p[key]
        progs_by_tier = sp["m_PlayerSubPrograms"]
        blobs_by_tier = sp["m_ParameterBlobIndices"]
        tier = next((i for i, x in enumerate(progs_by_tier) if x), None)
        if tier is None:
            raise Unsupported("pass without a %s program" % stage)
        common = Params()
        read_common_params(sp["m_CommonParameters"], names, common)
        variants = []
        for prog, pblob in zip(progs_by_tier[tier], blobs_by_tier[tier]):
            kws = [keyword_names[k] for k in prog["m_KeywordIndices"]]
            if any(k in SKIP_KEYWORDS for k in kws):
                continue
            params = Params()
            params.cbs = {k: {"size": v["size"], "vars": list(v["vars"])} for k, v in common.cbs.items()}
            params.bindings = dict(common.bindings)
            params.textures = dict(common.textures)
            o, l, s = table[pblob]
            read_param_blob(segs[s][o:o + l], params)
            o, l, s = table[prog["m_BlobIndex"]]
            _, code = read_program_blob(segs[s][o:o + l])
            pr = Program(stage, code, params, builtins, uniforms, textures)
            pr.translate()
            variants.append((sorted(kws), pr))
            keywords_seen.update(kws)
        if not variants:
            raise Unsupported("no usable %s variants" % stage)
        stages[stage] = variants
    return stages, uniforms, textures, keywords_seen


def decl_size(decl):
    """Bytes a constant declaration takes, and whether it has to start on a 16-byte register."""
    m = re.match(r"(float|int|bool|uint)(\d)?(?:x(\d))? \w+(?:\[(\d+)\])?;", decl)
    base, n, m2, arr = m.groups()
    if m2:  # matrix: one register per column
        return 16 * int(m2) * max(int(arr or 1), 1), True
    if arr:
        return 16 * int(arr), True
    return 4 * int(n or 1), False


def padded_layout(cb, members):
    out = []
    pos = 0
    pad = 0
    for off, decl in members:
        if off < pos:
            raise Unsupported("overlapping constants in %s" % cb)
        while pos < off:
            out.append("float _pad_%s_%d;" % (re.sub(r"\W", "", cb), pad))
            pad += 1
            pos += 4
        size, aligned = decl_size(decl)
        if aligned and off % 16 or not aligned and off // 16 != (off + size - 1) // 16:
            raise Unsupported("constant %s at an offset HLSL can't place" % decl)
        out.append(decl)
        pos = off + size
    return out


def emit_program(stages, uniforms, textures, keywords, builtins):
    out = []
    out.append("CGPROGRAM")
    out.append("#pragma vertex vert")
    out.append("#pragma fragment frag")
    out.append("#pragma target 3.5")
    for k in sorted(keywords):
        out.append("#pragma multi_compile __ %s" % k)
    # uniforms, grouped by constant buffer
    groups = {}
    for name, (cb, decl, off) in uniforms.items():
        groups.setdefault(cb, []).append((off, decl))
    for cb in sorted(groups, key=lambda c: (c != "$Globals", c)):
        if cb in ("$Globals", ""):
            out += sorted(d for _, d in groups[cb])
        else:
            # Unity fills some constant buffers (UnityPerDrawSprite and the like) as raw blocks with a fixed layout, so
            # every member goes at its original offset, with padding for the members this shader doesn't use.
            out.append("CBUFFER_START(%s)" % cb)
            if cb.startswith("Unity"):
                out += ["\t" + d for d in padded_layout(cb, sorted(groups[cb]))]
            else:
                out += ["\t" + d for _, d in sorted(groups[cb], key=lambda x: x[1])]
            out.append("CBUFFER_END")
    for name, (dim, has_sampler) in sorted(textures.items()):
        if name in builtins:
            continue
        t = {3: "Texture2D", 5: "Texture3D", 6: "TextureCube", 8: "Texture2DArray"}[dim]
        out.append("%s %s;" % (t, name))
        if has_sampler:
            out.append("SamplerState sampler%s;" % name)

    # interface structs: union of every variant's signature, one float4 per semantic
    def fields(sigs, skip_sv=True):
        f = {}
        for name, idx, sv, ct, reg, mask in sigs:
            f[(name, idx)] = (sv, ct)
        return f

    vin = {}
    v2f = {}
    fout = {}
    for _, pr in stages["vert"]:
        vin.update(fields(pr.inputs))
        v2f.update(fields(pr.outputs))
    for _, pr in stages["frag"]:
        for k, val in fields(pr.inputs).items():
            v2f.setdefault(k, val)
        fout.update(fields(pr.outputs))
    special_in = {}

    def decl_struct(sname, f, stage_in):
        lines = ["struct %s {" % sname]
        for (name, idx), (sv, ct) in sorted(f.items(), key=lambda x: (x[0][0] != "SV_POSITION" and x[0][0] != "SV_Position", x[0])):
            up = name.upper()
            if up in ("SV_VERTEXID", "SV_INSTANCEID", "SV_ISFRONTFACE", "SV_PRIMITIVEID", "SV_SAMPLEINDEX"):
                special_in[(name, idx)] = up
                continue
            typ = {0: "float4", 1: "uint4", 2: "int4", 3: "float4"}.get(ct, "float4")
            if up == "SV_DEPTH":
                lines.append("\tfloat %s : SV_Depth;" % sem_field(name, idx))
                continue
            sem = "SV_POSITION" if up == "SV_POSITION" else ("SV_Target%d" % idx if up == "SV_TARGET" else "%s%d" % (name, idx))
            lines.append("\t%s %s : %s;" % (typ, sem_field(name, idx), sem))
        lines.append("};")
        return lines

    out += decl_struct("appdata_d", vin, True)
    out += decl_struct("v2f_d", v2f, False)
    out += decl_struct("fout_d", fout, False)

    def unpack_inputs(pr, src):
        """uint4 v# locals from the struct fields."""
        lines = []
        regs = sorted(set(s[4] for s in pr.inputs))
        for r in regs:
            lines.append("\tuint4 v%d = 0u;" % r)
        for name, idx, sv, ct, reg, mask in pr.inputs:
            up = name.upper()
            comps = [CMP[k] for k in range(4) if mask & (1 << k)]
            if up == "SV_VERTEXID":
                lines.append("\tv%d.%s = vid;" % (reg, comps[0]))
                continue
            if up == "SV_INSTANCEID":
                lines.append("\tv%d.%s = iid;" % (reg, comps[0]))
                continue
            if up == "SV_ISFRONTFACE":
                lines.append("\tv%d.%s = face ? 0xffffffffu : 0u;" % (reg, comps[0]))
                continue
            if up in ("SV_PRIMITIVEID", "SV_SAMPLEINDEX"):
                raise Unsupported(name)
            fld = "%s.%s" % (src, sem_field(name, idx))
            for k, c in enumerate(comps):
                lines.append("\tv%d.%s = asuint(%s.%s);" % (reg, c, fld, CMP[k]))
        return lines

    def pack_outputs(pr, dst):
        lines = []
        for name, idx, sv, ct, reg, mask in pr.outputs:
            up = name.upper()
            comps = [CMP[k] for k in range(4) if mask & (1 << k)]
            fld = "%s.%s" % (dst, sem_field(name, idx))
            if up == "SV_DEPTH":
                lines.append("\t%s = oDepth;" % fld)
                continue
            conv = {1: "", 2: "asint"}.get(ct, "asfloat")
            for k, c in enumerate(comps):
                lines.append("\t%s.%s = %s(o%d.%s);" % (fld, CMP[k], conv, reg, c))
        return lines

    def entry(stage, variants, sname_in, sname_out, extra_params):
        """The stage's entry body: picks the variant for the keywords that are on.

        The PC build only has the variants its materials used, so some keyword combinations have no exact variant
        (e.g. the darkness vignette exists only with PIXELSNAP_ON or DITHERING_NOISE also on). Like Unity, those use
        the closest variant: the most keywords in common, then the fewest keywords that aren't on."""
        funcs = ["" for _ in variants]
        calls = {}
        for n, (kws, pr) in enumerate(variants):
            fname = "%s_%d" % (stage, n)
            funcs[n] = pr.function(fname)
            calls[n] = call(pr, fname)
        kwset = sorted(set(k for kws, _ in variants for k in kws))
        if len(variants) == 1:
            return funcs, [calls[0]]

        def best(on):
            return max(range(len(variants)), key=lambda n: (len(on & set(variants[n][0])),
                                                            -len(set(variants[n][0]) - on), -n))
        if len(kwset) <= 8:
            choice = {}
            for mask in range(1 << len(kwset)):
                on = frozenset(k for i, k in enumerate(kwset) if mask & (1 << i))
                choice.setdefault(best(on), []).append(on)
            body = []
            order = sorted(choice, key=lambda n: -len(choice[n]))
            for i, n in enumerate(order):
                if i == len(order) - 1:
                    body.append("#else")
                else:
                    tests = ["(%s)" % " && ".join(("defined(%s)" if k in on else "!defined(%s)") % k for k in kwset)
                             for on in choice[n]]
                    body.append("#%s %s" % ("if" if i == 0 else "elif", " || ".join(tests)))
                body.append(calls[n])
            body.append("#endif")
            return funcs, body
        # Too many keywords to list every combination: the most specific variant whose keywords are all on.
        ordered = sorted(range(len(variants)), key=lambda n: -len(variants[n][0]))
        body = []
        first = True
        for n in ordered:
            if not variants[n][0]:
                continue
            body.append("#%s %s" % ("if" if first else "elif", keyword_test(variants[n][0], keywords)))
            first = False
            body.append(calls[n])
        body.append("#else")
        body.append(calls[best(frozenset())])
        body.append("#endif")
        return funcs, body

    def call(pr, fname):
        lines = ["\t{"]
        lines += ["\t" + l for l in unpack_inputs(pr, "i")]
        oregs = sorted(set(s[4] for s in pr.outputs if s[0].upper() != "SV_DEPTH"))
        for r in oregs:
            lines.append("\t\tuint4 o%d = 0u;" % r)
        has_depth = any(s[0].upper() == "SV_DEPTH" for s in pr.outputs)
        if has_depth:
            lines.append("\t\tfloat oDepth = 0;")
        args = ["v%d" % r for r in sorted(set(s[4] for s in pr.inputs))] + ["o%d" % r for r in oregs] + (["oDepth"] if has_depth else [])
        lines.append("\t\t%s(%s);" % (fname, ", ".join(args)))
        lines += ["\t" + l for l in pack_outputs(pr, "o")]
        lines.append("\t}")
        return "\n".join(lines)

    vfuncs, vbody = entry("vert", stages["vert"], "appdata_d", "v2f_d", "")
    ffuncs, fbody = entry("frag", stages["frag"], "v2f_d", "fout_d", "")
    out += vfuncs
    vparams = ["appdata_d i"]
    if ("SV_VertexID", 0) in special_in or any(k[0].upper() == "SV_VERTEXID" for k in special_in):
        vparams.append("uint vid : SV_VertexID")
    if any(k[0].upper() == "SV_INSTANCEID" for k in special_in):
        vparams.append("uint iid : SV_InstanceID")
    out.append("v2f_d vert(%s)" % ", ".join(vparams))
    out.append("{")
    out.append("\tv2f_d o = (v2f_d)0;")
    out += vbody
    out.append("\treturn o;")
    out.append("}")
    out += ffuncs
    fparams = ["v2f_d i"]
    if any(k[0].upper() == "SV_ISFRONTFACE" for k in special_in):
        fparams.append("bool face : SV_IsFrontFace")
    out.append("fout_d frag(%s)" % ", ".join(fparams))
    out.append("{")
    out.append("\tfout_d o = (fout_d)0;")
    out += fbody
    out.append("\treturn o;")
    out.append("}")
    out.append("ENDCG")
    return out


# ------------------------------------------------------------------------------------------------ ShaderLab state

BLEND = ["Zero", "One", "DstColor", "SrcColor", "OneMinusDstColor", "SrcAlpha", "OneMinusSrcColor", "DstAlpha",
         "OneMinusDstAlpha", "SrcAlphaSaturate", "OneMinusSrcAlpha"]
BLENDOP = ["Add", "Sub", "RevSub", "Min", "Max", "LogicalClear", "LogicalSet", "LogicalCopy", "LogicalCopyInverted",
           "LogicalNoop", "LogicalInvert", "LogicalAnd", "LogicalNand", "LogicalOr", "LogicalNor", "LogicalXor",
           "LogicalEquiv", "LogicalAndReverse", "LogicalAndInverted", "LogicalOrReverse", "LogicalOrInverted",
           "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn", "HardLight", "SoftLight",
           "Difference", "Exclusion", "HSLHue", "HSLSaturation", "HSLColor", "HSLLuminosity"]
COMPARE = ["Never", "Never", "Less", "Equal", "LEqual", "Greater", "NotEqual", "GEqual", "Always"]
STENCILOP = ["Keep", "Zero", "Replace", "IncrSat", "DecrSat", "Invert", "IncrWrap", "DecrWrap"]
CULL = ["Off", "Front", "Back"]
TAG_NAMES = {"QUEUE": "Queue", "IGNOREPROJECTOR": "IgnoreProjector", "LIGHTMODE": "LightMode",
             "RENDERTYPE": "RenderType", "PREVIEWTYPE": "PreviewType", "CANUSESPRITEATLAS": "CanUseSpriteAtlas",
             "DISABLEBATCHING": "DisableBatching", "FORCENOSHADOWCASTING": "ForceNoShadowCasting"}


def prop(v, table=None):
    """A serialized state value: [_Property] if it comes from a material property, else its value."""
    if v["name"] and v["name"] != "<noninit>":
        return "[%s]" % v["name"]
    x = v["val"]
    if table is not None:
        return table[int(x)]
    return ("%d" % x) if float(x).is_integer() else ("%g" % x)


def tags(t):
    items = t["tags"] if isinstance(t, dict) else t
    if not items:
        return None
    return "Tags { %s }" % " ".join('"%s"="%s"' % (TAG_NAMES.get(k.upper(), k), v) for k, v in items)


def colmask(v):
    if v["name"] and v["name"] != "<noninit>":
        return "[%s]" % v["name"]
    m = int(v["val"])
    if m == 0:
        return "0"
    return "".join(c for c, bit in (("R", 8), ("G", 4), ("B", 2), ("A", 1)) if m & bit)


def is_default(v, val):
    return (not v["name"] or v["name"] == "<noninit>") and v["val"] == val


def state_lines(st):
    out = []
    if st["m_Name"]:
        out.append('Name "%s"' % st["m_Name"])
    tg = tags(st["m_Tags"])
    if tg:
        out.append(tg)
    if st["m_LOD"]:
        out.append("LOD %d" % st["m_LOD"])
    rt = st["rtBlend0"]
    if st["rtSeparateBlend"]:
        raise Unsupported("per-target blending")
    if not (is_default(rt["srcBlend"], 1) and is_default(rt["destBlend"], 0) and is_default(rt["srcBlendAlpha"], 1)
            and is_default(rt["destBlendAlpha"], 0)):
        line = "Blend %s %s" % (prop(rt["srcBlend"], BLEND), prop(rt["destBlend"], BLEND))
        if not (rt["srcBlendAlpha"] == rt["srcBlend"] and rt["destBlendAlpha"] == rt["destBlend"]):
            line += ", %s %s" % (prop(rt["srcBlendAlpha"], BLEND), prop(rt["destBlendAlpha"], BLEND))
        out.append(line)
    if not (is_default(rt["blendOp"], 0) and is_default(rt["blendOpAlpha"], 0)):
        line = "BlendOp %s" % prop(rt["blendOp"], BLENDOP)
        if rt["blendOpAlpha"] != rt["blendOp"]:
            line += ", %s" % prop(rt["blendOpAlpha"], BLENDOP)
        out.append(line)
    if not is_default(rt["colMask"], 15):
        out.append("ColorMask %s" % colmask(rt["colMask"]))
    zw = st["zWrite"]
    out.append("ZWrite %s" % (prop(zw) if prop(zw).startswith("[") else "On" if zw["val"] else "Off"))
    zt = st["zTest"]
    out.append("ZTest %s" % (prop(zt) if prop(zt).startswith("[") else COMPARE[int(zt["val"])] if int(zt["val"]) else "Always"))
    out.append("Cull %s" % prop(st["culling"], CULL))
    if not (is_default(st["offsetFactor"], 0) and is_default(st["offsetUnits"], 0)):
        out.append("Offset %s, %s" % (prop(st["offsetFactor"]), prop(st["offsetUnits"])))
    if not is_default(st["alphaToMask"], 0):
        out.append("AlphaToMask %s" % ("On" if is_default(st["alphaToMask"], 1) else prop(st["alphaToMask"])))
    if not is_default(st["zClip"], 1):
        out.append("ZClip %s" % ("False" if is_default(st["zClip"], 0) else prop(st["zClip"])))
    so = st["stencilOp"]
    sf, sb = st["stencilOpFront"], st["stencilOpBack"]
    stencil_default = all(is_default(x[k], d) for x in (so, sf, sb) for k, d in (("pass", 0), ("fail", 0), ("zFail", 0), ("comp", 8)))
    if not (stencil_default and is_default(st["stencilRef"], 0) and is_default(st["stencilReadMask"], 255)
            and is_default(st["stencilWriteMask"], 255)):
        s = ["Stencil {", "\tRef %s" % prop(st["stencilRef"]), "\tReadMask %s" % prop(st["stencilReadMask"]),
             "\tWriteMask %s" % prop(st["stencilWriteMask"])]
        same = all(sf[k] == sb[k] for k in ("pass", "fail", "zFail", "comp"))
        if same:
            src = sf if not all(is_default(sf[k], d) for k, d in (("pass", 0), ("fail", 0), ("zFail", 0), ("comp", 8))) else so
            s += ["\tComp %s" % prop(src["comp"], COMPARE), "\tPass %s" % prop(src["pass"], STENCILOP),
                  "\tFail %s" % prop(src["fail"], STENCILOP), "\tZFail %s" % prop(src["zFail"], STENCILOP)]
        else:
            for side, x in (("Front", sf), ("Back", sb)):
                s += ["\tComp%s %s" % (side, prop(x["comp"], COMPARE)), "\tPass%s %s" % (side, prop(x["pass"], STENCILOP)),
                      "\tFail%s %s" % (side, prop(x["fail"], STENCILOP)), "\tZFail%s %s" % (side, prop(x["zFail"], STENCILOP))]
        s.append("}")
        out += s
    return out


class Builtins(set):
    """Names Unity declares for every shader (HLSLSupport/UnityShaderVariables are included automatically).
    conditional: ones it declares only on some platforms (light probe proxy volumes aren't on mobile)."""
    conditional = set()


def builtin_names(cginc_dir):
    names = Builtins()
    for f in ("UnityShaderVariables.cginc", "HLSLSupport.cginc", "UnityInstancing.cginc"):
        text = open(os.path.join(cginc_dir, f), encoding="utf-8", errors="ignore").read()
        for m in re.finditer(r"^\s*(?:uniform\s+|static\s+)?(?:float|half|fixed|int|uint|bool)[0-9x]*\s+(\w+)\s*(?:\[[^\]]*\])?\s*;", text, re.M):
            names.add(m.group(1))
        for m in re.finditer(r"UNITY_DECLARE_\w+\(\s*(\w+)", text):
            names.add(m.group(1))
    names.conditional = {n for n in names if n.startswith("unity_ProbeVolume")}
    return names


def decompile(t, builtins):
    pf = t["m_ParsedForm"]
    segs, table = unpack_blob(t)
    out = []
    for ss in pf["m_SubShaders"]:
        sub = ["\tSubShader {"]
        tg = tags(ss["m_Tags"])
        if tg:
            sub.append("\t\t" + tg)
        if ss["m_LOD"]:
            sub.append("\t\tLOD %d" % ss["m_LOD"])
        for p in ss["m_Passes"]:
            if p["m_Type"] == 2:
                sub.append('\t\tGrabPass { %s}' % ('"%s" ' % p["m_TextureName"] if p["m_TextureName"] else ""))
                continue
            if p["m_Type"] == 1:
                sub.append('\t\tUsePass "%s"' % p["m_UseName"])
                continue
            for st in ("progGeometry", "progHull", "progDomain"):
                if any(x for x in (p[st]["m_PlayerSubPrograms"] or [])):
                    raise Unsupported(st)
            names = {v: k for k, v in p["m_NameIndices"]}
            stages, uniforms, textures, keywords = build_pass(p, names, pf["m_KeywordNames"], segs, table, builtins)
            body = ["\t\tPass {"]
            body += ["\t\t\t" + l for l in state_lines(p["m_State"])]
            body += [("\t\t\t" + l) if not l.startswith("#") else l for l in emit_program(stages, uniforms, textures, keywords, builtins)]
            body.append("\t\t}")
            sub += body
        sub.append("\t}")
        out += sub
    return "\n".join(out)


def load_shaders(data_dir):
    """Every shader in the game: the shared shader bundle, the bundles that carry their own, and the player data."""
    import UnityPy
    UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.50f1"
    files = sorted(glob.glob(os.path.join(data_dir, "StreamingAssets", "aa", "*", "*.bundle")))
    files += [os.path.join(data_dir, f) for f in ("resources.assets", "globalgamemanagers.assets")]
    shaders = {}
    for f in files:
        try:
            env = UnityPy.load(f)
        except Exception:
            continue
        for o in env.objects:
            if o.type.name != "Shader":
                continue
            t = o.read_typetree()
            shaders.setdefault(t["m_ParsedForm"]["m_Name"], t)
    return shaders


def main():
    data_dir, cginc, out_dir = sys.argv[1:4]
    only = set(sys.argv[4:])
    warnings.filterwarnings("ignore")
    builtins = builtin_names(cginc)
    os.makedirs(out_dir, exist_ok=True)
    ok, failed = 0, []
    for name, t in sorted(load_shaders(data_dir).items()):
        if only and name not in only:
            continue
        try:
            text = decompile(t, builtins)
        except Exception as e:  # anything it can't translate keeps gen_shaders.py's version
            failed.append("%s (%s%s)" % (name, "" if isinstance(e, Unsupported) else type(e).__name__ + ": ", e))
            continue
        with open(os.path.join(out_dir, name.replace("/", "_") + ".shaderlab"), "w", encoding="utf-8") as f:
            f.write(text + "\n")
        ok += 1
    print("decompiled %d shaders" % ok)
    for f in failed:
        print("  kept the approximation for", f)


if __name__ == "__main__":
    main()
