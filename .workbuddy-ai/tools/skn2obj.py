#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把英雄联盟原生 .skn 网格转成 Wavefront OBJ + MTL（静态绑定姿态，支持多对象）。

格式来源：对 CommunityDragon 的以下四个文件实测反解 + 方程闭合验证：
  sru_orderminionmelee/skins/base/order_minion_melee.skn   (objectCount = 1)
  turret/skins/base/turret_base.skn                        (objectCount = 8)
  nexus/skins/base/nexus.skn                               (objectCount = 2)
  inhibitor/skins/base/inhibitor.skn                       (objectCount = 2)

布局（little-endian）：
  0x00  u32 magic = 0x00112233
  0x04  u16 majorVersion, u16 minorVersion
  0x08  u32 objectCount
  ---- 每个对象一条 80 字节记录 ----
        char name[64]        # NUL 补齐
        u32  vertexStart
        u32  vertexCount
        u32  indexStart
        u32  indexCount
  ---- 60 字节尾部 ----
        u32 ?  |  u32 totalIndexCount  |  u32 totalVertexCount  |  u32 vertexSize  |  u32 ?  |  f32[10]
  ---- 数据块 ----
        u16[totalIndexCount]                  # 索引（绝对顶点下标，三角列表）
        u8[vertexSize × totalVertexCount]     # 顶点块
        12 字节尾记录

顶点（vertexSize = 52）：
  +0   f32[3]  position
  +12  u8[4]   boneIndices
  +16  f32[4]  boneWeights
  +32  f32[3]  normal
  +44  f32[2]  uv

【四条交叉判据 —— 必须同时成立，任一不满足就报错退出（绝不输出疑似垃圾的资产）】
  ① 头部长度方程：12 + objectCount×80 + 60 + totalIndexCount×2 + totalVertexCount×vertexSize + 12 == 文件长度
  ② 索引零越界（全部 < totalVertexCount）
  ③ 全部顶点法线为单位向量（长度 ∈ [0.9, 1.1]）
  ④ 每对象的 vertexStart / indexStart 与前面所有对象的计数累加严格吻合

【对象筛选】默认剔除名字含 rubble / broken / destroyed 的对象 —— 它们是"被摧毁后的残骸"变体，
与同一文件里的完整模型叠在一起会出现重影。用 --all 保留全部。

用法:
  python skn2obj.py <in.skn> <out.obj> [texture.png] [target_height] [--all]
"""
import math
import os
import struct
import sys

MAGIC = 0x00112233
RECORD_SIZE = 80
TRAILER_SIZE = 60
TAIL_SIZE = 12
VERTEX_SIZE = 52

OFF_POS = 0
OFF_NORMAL = 32
OFF_UV = 44

# 被摧毁后的残骸变体：默认不导出（与完整模型叠放会重影）
RUIN_KEYWORDS = ('rubble', 'broken', 'destroyed')


def parse(path, include_ruins=False):
    data = open(path, 'rb').read()
    size = len(data)

    magic = struct.unpack_from('<I', data, 0)[0]
    if magic != MAGIC:
        raise SystemExit('magic 不符: 0x%08X（期望 0x%08X）' % (magic, MAGIC))

    major, minor = struct.unpack_from('<HH', data, 4)
    object_count = struct.unpack_from('<I', data, 8)[0]

    objects = []
    for i in range(object_count):
        base = 0x0C + i * RECORD_SIZE
        name = data[base:base + 64].split(b'\x00')[0].decode('ascii', 'replace')
        v_start, v_count, i_start, i_count = struct.unpack_from('<4I', data, base + 64)
        objects.append({'name': name, 'vStart': v_start, 'vCount': v_count,
                        'iStart': i_start, 'iCount': i_count})

    trailer = 0x0C + object_count * RECORD_SIZE
    _, total_indices, total_vertices, vertex_size, _ = struct.unpack_from('<5I', data, trailer)

    print('version=%d.%d objectCount=%d vertexSize=%d' % (major, minor, object_count, vertex_size))
    for o in objects:
        print('   obj %-24s vStart=%-6d vCount=%-6d iStart=%-6d iCount=%d' % (
            o['name'], o['vStart'], o['vCount'], o['iStart'], o['iCount']))

    if vertex_size != VERTEX_SIZE:
        raise SystemExit('vertexSize=%d，本脚本只实现 %d' % (vertex_size, VERTEX_SIZE))

    index_offset = trailer + TRAILER_SIZE
    vertex_offset = index_offset + total_indices * 2
    end = vertex_offset + total_vertices * vertex_size + TAIL_SIZE

    print('indexBlock=0x%X vertexBlock=0x%X end=0x%X fileSize=0x%X' % (
        index_offset, vertex_offset, end, size))

    # ---- 判据 ①：头部长度方程 ----
    if end != size:
        raise SystemExit('块边界与文件长度不符（差 %d 字节），解析假设有误' % (size - end))

    # ---- 判据 ④：每对象的 start 必须与累加吻合 ----
    exp_v = exp_i = 0
    for o in objects:
        if o['vStart'] != exp_v or o['iStart'] != exp_i:
            raise SystemExit('对象 %s 的 start 与累加不符（期望 v=%d i=%d，实际 v=%d i=%d）' % (
                o['name'], exp_v, exp_i, o['vStart'], o['iStart']))
        exp_v += o['vCount']
        exp_i += o['iCount']
    if exp_v != total_vertices or exp_i != total_indices:
        raise SystemExit('累加计数与尾部总数不符（v %d/%d，i %d/%d）' % (
            exp_v, total_vertices, exp_i, total_indices))

    # ---- 判据 ②：索引零越界 ----
    indices = list(struct.unpack_from('<%dH' % total_indices, data, index_offset))
    out_of_range = sum(1 for i in indices if i >= total_vertices)
    if out_of_range:
        raise SystemExit('索引越界 %d 个（应为 0），解析假设有误' % out_of_range)

    # ---- 判据 ③：法线必须为单位向量 ----
    # 【为什么用比例而不是全等】源数据里可能存在极少量退化法线（实测 turret_base.skn 有 7/22849 个）。
    # 判据的目的是抓"整体错位"——错位会让单位法线比例掉到接近 0，因此 99% 的阈值
    # 既不会漏掉错位，也不会被几个源数据瑕疵挡住。退化法线会被替换成 (0,1,0)，避免渲染成黑块。
    verts = []
    unit = 0
    fixed = 0
    for k in range(total_vertices):
        base = vertex_offset + k * vertex_size
        pos = struct.unpack_from('<3f', data, base + OFF_POS)
        nrm = list(struct.unpack_from('<3f', data, base + OFF_NORMAL))
        uv = struct.unpack_from('<2f', data, base + OFF_UV)
        m = math.sqrt(nrm[0] ** 2 + nrm[1] ** 2 + nrm[2] ** 2)
        if 0.9 < m < 1.1:
            unit += 1
        else:
            nrm = [0.0, 1.0, 0.0]
            fixed += 1
        verts.append((pos, tuple(nrm), uv))

    ratio = unit / float(total_vertices) if total_vertices else 0.0
    print('单位法线 %d/%d (%.2f%%)，修补退化法线 %d 个' % (unit, total_vertices, ratio * 100.0, fixed))
    if ratio < 0.99:
        raise SystemExit('单位法线比例只有 %.1f%%（应 ≥ 99%%），解析假设有误' % (ratio * 100.0))

    kept = []
    for o in objects:
        low = o['name'].lower()
        if not include_ruins and any(k in low for k in RUIN_KEYWORDS):
            print('   skip %s（残骸变体）' % o['name'])
            continue
        kept.append(o)

    if not kept:
        raise SystemExit('筛选后没有任何对象可导出（可加 --all 保留残骸）')

    return verts, indices, kept


def main():
    args = [a for a in sys.argv[1:] if a != '--all']
    include_ruins = '--all' in sys.argv

    src, dst = args[0], args[1]
    tex = args[2] if len(args) > 2 and args[2] else None
    target_height = float(args[3]) if len(args) > 3 else 0.0

    verts, indices, objects = parse(src, include_ruins)

    used = set()
    for o in objects:
        used.update(range(o['vStart'], o['vStart'] + o['vCount']))
    used = sorted(used)

    xs = [verts[i][0][0] for i in used]
    ys = [verts[i][0][1] for i in used]
    zs = [verts[i][0][2] for i in used]
    h = max(ys) - min(ys)
    print('bounds X=%.1f..%.1f Y=%.1f..%.1f Z=%.1f..%.1f (height %.1f)' % (
        min(xs), max(xs), min(ys), max(ys), min(zs), max(zs), h))

    scale = 1.0
    ox = oy = oz = 0.0
    if target_height > 0.0 and h > 1e-6:
        scale = target_height / h
        ox = -((min(xs) + max(xs)) * 0.5) * scale
        oy = -min(ys) * scale
        oz = -((min(zs) + max(zs)) * 0.5) * scale
        print('normalize: scale=%.6f (height %.1f -> %.2f)' % (scale, h, target_height))

    # OBJ 顶点表只写"用到的"顶点，重映射下标
    remap = {old: new + 1 for new, old in enumerate(used)}

    name = os.path.splitext(os.path.basename(dst))[0]
    mat_name = name + '_mat'

    with open(dst, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# converted from %s by skn2obj.py\n' % os.path.basename(src))
        f.write('mtllib %s.mtl\n' % name)
        f.write('o %s\n' % name)

        for old in used:
            pos = verts[old][0]
            f.write('v %.6f %.6f %.6f\n' % (
                pos[0] * scale + ox, pos[1] * scale + oy, pos[2] * scale + oz))
        for old in used:
            n = verts[old][1]
            f.write('vn %.6f %.6f %.6f\n' % (n[0], n[1], n[2]))
        for old in used:
            uv = verts[old][2]
            f.write('vt %.6f %.6f\n' % (uv[0], uv[1]))

        tri_total = 0
        for o in objects:
            f.write('g %s\n' % o['name'])
            f.write('usemtl %s\n' % mat_name)
            for k in range(o['iStart'], o['iStart'] + o['iCount'] - 2, 3):
                a, b, c = remap[indices[k]], remap[indices[k + 1]], remap[indices[k + 2]]
                f.write('f %d/%d/%d %d/%d/%d %d/%d/%d\n' % (a, a, a, b, b, b, c, c, c))
                tri_total += 1

    mtl = os.path.splitext(dst)[0] + '.mtl'
    with open(mtl, 'w', encoding='utf-8', newline='\n') as f:
        f.write('newmtl %s\n' % mat_name)
        f.write('Ka 1.000 1.000 1.000\nKd 1.000 1.000 1.000\nKs 0.000 0.000 0.000\n')
        f.write('d 1.0\nillum 1\n')
        if tex:
            f.write('map_Kd %s\n' % os.path.basename(tex))

    print('wrote %s (%d triangles, %d groups) + %s' % (
        dst, tri_total, len(objects), os.path.basename(mtl)))


if __name__ == '__main__':
    main()
