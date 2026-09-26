#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 LOLModelViewer 的 .lmesh 转成 Wavefront OBJ + MTL（静态绑定姿态网格）。

格式来源：viewer.js 的 ZamModelViewer.Lol.Model.prototype.loadMesh / Lol.Vertex
（little-endian）

  u32 magic = 604210091
  u32 version
  str animFile        (u16 len + ASCII bytes)
  str textureFile
  u32 numMeshes
    x numMeshes: str name, u32 vStart, u32 vCount, u32 iStart, u32 iCount
  u32 numVerts
    x numVerts: f32 pos[3], f32 nrm[3], f32 u, f32 v, u8 boneIdx[4], f32 weights[4]   # 52 字节
  u32 numIndices
    x numIndices: u16          (绝对顶点下标，见 drawElements 的 iStart*2 偏移)
  u32 numBones                 # 之后是骨骼块，静态导出不需要，直接不解析

用法: python lmesh2obj.py <in.lmesh> <out.obj> [texture.png] [target_height]

target_height 给定时做归一化：按包围盒 Y 高度缩放到该值、脚底(Ymin)归零、X/Z 居中。
不传则原样导出（LoL 原始单位，1 单位 ≈ 1 厘米量级）。
"""
import struct
import sys
import os

MAGIC = 604210091


class Reader:
    def __init__(self, data):
        self.d = data
        self.p = 0

    def u8(self):
        v = self.d[self.p]
        self.p += 1
        return v

    def u16(self):
        v = struct.unpack_from('<H', self.d, self.p)[0]
        self.p += 2
        return v

    def i32(self):
        v = struct.unpack_from('<i', self.d, self.p)[0]
        self.p += 4
        return v

    def u32(self):
        v = struct.unpack_from('<I', self.d, self.p)[0]
        self.p += 4
        return v

    def f32(self):
        v = struct.unpack_from('<f', self.d, self.p)[0]
        self.p += 4
        return v

    def s(self):
        n = self.u16()
        v = self.d[self.p:self.p + n].decode('ascii', 'replace')
        self.p += n
        return v


def main():
    src, dst = sys.argv[1], sys.argv[2]
    tex = sys.argv[3] if len(sys.argv) > 3 and sys.argv[3] else None
    target_height = float(sys.argv[4]) if len(sys.argv) > 4 else 0.0

    r = Reader(open(src, 'rb').read())

    magic = r.u32()
    if magic != MAGIC:
        raise SystemExit('bad magic: %d (expect %d)' % (magic, MAGIC))

    version = r.u32()
    anim_file = r.s()
    texture_file = r.s()

    meshes = []
    for _ in range(r.u32()):
        meshes.append({
            'name': r.s().lower(),
            'vStart': r.u32(), 'vCount': r.u32(),
            'iStart': r.u32(), 'iCount': r.u32(),
        })

    verts = []
    for _ in range(r.u32()):
        pos = (r.f32(), r.f32(), r.f32())
        nrm = (r.f32(), r.f32(), r.f32())
        u, v = r.f32(), r.f32()
        for _i in range(4):
            r.u8()
        for _i in range(4):
            r.f32()
        verts.append((pos, nrm, u, v))

    indices = [r.u16() for _ in range(r.u32())]

    num_bones = r.u32()
    leftover = len(r.d) - r.p

    # ---- 边界（用于判断朝向/单位） ----
    xs = [v[0][0] for v in verts]
    ys = [v[0][1] for v in verts]
    zs = [v[0][2] for v in verts]
    size = (max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs))

    print('version=%d anim=%r texture=%r' % (version, anim_file, texture_file))
    print('meshes=%d verts=%d indices=%d bones=%d leftover=%dB' % (
        len(meshes), len(verts), len(indices), num_bones, leftover))
    print('bounds X=%.3f Y=%.3f Z=%.3f  min=(%.3f,%.3f,%.3f) max=(%.3f,%.3f,%.3f)' % (
        size[0], size[1], size[2], min(xs), min(ys), min(zs), max(xs), max(ys), max(zs)))

    # 完整性交叉校验：索引下标必须都在顶点范围内
    bad = [i for i in indices if i >= len(verts)]
    print('out-of-range indices:', len(bad))

    # ---- 归一化（可选）：缩放到目标身高 + 脚底归零 + X/Z 居中 ----
    scale = 1.0
    off_x = off_y = off_z = 0.0
    if target_height > 0.0 and size[1] > 1e-6:
        scale = target_height / size[1]
        off_x = -((min(xs) + max(xs)) * 0.5) * scale
        off_y = -(min(ys)) * scale
        off_z = -((min(zs) + max(zs)) * 0.5) * scale
        print('normalize: scale=%.6f (height %.3f -> %.3f), pivot 归到脚底/中心' % (
            scale, size[1], target_height))

    name = os.path.splitext(os.path.basename(dst))[0]
    mat_name = name + '_mat'

    def xf(p):
        return (p[0] * scale + off_x, p[1] * scale + off_y, p[2] * scale + off_z)

    with open(dst, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# converted from %s by lmesh2obj.py\n' % os.path.basename(src))
        f.write('mtllib %s.mtl\n' % name)
        f.write('o %s\n' % name)

        for pos, _nrm, _u, _v in verts:
            p = xf(pos)
            # Unity 的 OBJ 导入默认是左手/右手无所谓，这里保持原样 + 由 Unity 侧决定是否翻转
            f.write('v %.6f %.6f %.6f\n' % (p[0], p[1], p[2]))
        for _pos, nrm, _u, _v in verts:
            f.write('vn %.6f %.6f %.6f\n' % (nrm[0], nrm[1], nrm[2]))
        for _pos, _nrm, u, v in verts:
            f.write('vt %.6f %.6f\n' % (u, v))

        for m in meshes:
            if m['iCount'] <= 0:
                continue
            f.write('g %s\n' % m['name'])
            f.write('usemtl %s\n' % mat_name)
            start = m['iStart']
            end = min(start + m['iCount'], len(indices))
            for k in range(start, end - 2, 3):
                a, b, c = indices[k], indices[k + 1], indices[k + 2]
                # OBJ 的索引是 1 基
                f.write('f %d/%d/%d %d/%d/%d %d/%d/%d\n' % (
                    a + 1, a + 1, a + 1, b + 1, b + 1, b + 1, c + 1, c + 1, c + 1))

    mtl_path = os.path.splitext(dst)[0] + '.mtl'
    with open(mtl_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('newmtl %s\n' % mat_name)
        f.write('Ka 1.000 1.000 1.000\nKd 1.000 1.000 1.000\nKs 0.000 0.000 0.000\n')
        f.write('d 1.0\nillum 1\n')
        if tex:
            f.write('map_Kd %s\n' % os.path.basename(tex))

    tris = sum(m['iCount'] // 3 for m in meshes)
    print('wrote %s (%d triangles, %d groups) + %s' % (dst, tris, len(meshes), os.path.basename(mtl_path)))


if __name__ == '__main__':
    main()
