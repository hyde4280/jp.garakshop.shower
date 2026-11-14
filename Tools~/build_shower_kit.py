# -*- coding: utf-8 -*-
"""シャワー部品(ヘッド・壁付け混合栓・ヘッド掛け金具)をヘッドレス Blender で作って FBX に書き出す。

使い方(プロジェクトルートで):
  "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Blender\\blender.exe" -b --factory-startup ^
      --python Packages/jp.garakshop.shower/Tools~/build_shower_kit.py [-- --out <dir>] [--save-blend <path>]
出力先(既定): <プロジェクト>/Assets/garakshop/shower_kit/Meshes/ShowerKit.fbx
  プロジェクトのルートはこのスクリプトの位置(<プロジェクト>/Packages/jp.garakshop.shower/Tools~/)の 3 つ上。
  ルートに Packages と Assets が両方無ければエラーで止まる。
  --out <dir>          : FBX を <dir>/ShowerKit.fbx に書く(既定の .../Meshes の代わり)。
  --save-blend <path>  : 作った .blend も保存する(既定では保存しない)。
  パッケージの中(Packages/jp.garakshop.shower/)へは書き込まない(パッケージ更新で上書きされるため)。
  同梱の Runtime/Meshes/ShowerKit.fbx を作り直すのは保守者だけ(Tools~/README.md)。
動作確認: Blender 5.2.2 LTS(Steam版)。外部テクスチャ・外部アセットは一切使わない(全て自作の手続き形状)。
Booth「お風呂 Unit bath」(再配布禁止)のメッシュ・UV・テクスチャは参照していない。

座標は全て Unity のローカル座標(m、+Y 上、+Z = 水の出る向き / 壁から離れる向き)で書き、
`u2b()` で Blender へ変換する(Unity(x, y, z) = (-bx, bz, -by)、FBX 取り込みで X が反転する)。
3 つのオブジェクトは原点を共有して重なって置かれる(組み立て位置は Unity のプレハブ側で決める)。
  ShowerHead   : 原点 = ヘッドの掴む位置(ShowerHeadPickup の原点)。ハンドルは +Y 方向、散水板は +Z を向く。
  ShowerMixer  : 原点 = 壁面上の、ホース出口の真後ろ・出口下端の高さ。壁面 z=0、ホース出口の中心は (0, 0, 0.101)。
  ShowerHolder : 原点 = 壁面上の、掛け金具のクリップ中心の真後ろ。クリップはハンドル軸(+Y を X 周り 11.87°)に合わせる。
マテリアル枠: 0 ShowerChrome(メッキ風)/ 1 ShowerWhiteResin(白樹脂)。
UV1 = メートル単位(1 UV = 1 m)。UV2 = ライトマップ用(smart_project)。
法線: 曲面は滑らか、折れ目(角度 35° 超)はシャープ辺。箱は面ごとフラット + 2〜5 mm の面取り。
FBX: bake_space_transform=True(1 階層・兄弟 3 ノードなので ElevatorSill と同じ。Unity 側で子の回転 0・スケール 1、
  頂点がそのままローカル座標)。apply_unit_scale・global_scale・axis は指定しない。
"""
import bpy, bmesh, math, os, sys, argparse
from mathutils import Vector

PKG_NAME = "jp.garakshop.shower"
HERE = os.path.dirname(os.path.abspath(__file__))
PKG_DIR = os.path.normpath(os.path.join(HERE, ".."))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
DEFAULT_OUT_DIR = os.path.join(ROOT, "Assets", "garakshop", "shower_kit", "Meshes")


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser(prog="build_shower_kit.py")
    ap.add_argument("--out", help="FBX の出力フォルダ(既定: <プロジェクト>/Assets/garakshop/shower_kit/Meshes)")
    ap.add_argument("--save-blend", help="作った .blend の保存先(既定: 保存しない)")
    return ap.parse_args(argv)


def is_inside(path, folder):
    path, folder = os.path.normcase(os.path.abspath(path)), os.path.normcase(os.path.abspath(folder))
    try:
        return os.path.commonpath([path, folder]) == folder
    except ValueError:  # 別ドライブ
        return False


def resolve_outputs(args):
    if args.out:
        out_dir = os.path.abspath(args.out)
    else:
        if not (os.path.isdir(os.path.join(ROOT, "Packages")) and os.path.isdir(os.path.join(ROOT, "Assets"))):
            sys.exit("error: project root not found (expected Packages/ and Assets/ in %s). Use -- --out <dir>." % ROOT)
        out_dir = DEFAULT_OUT_DIR
    out_fbx = os.path.join(out_dir, "ShowerKit.fbx")
    out_blend = os.path.abspath(args.save_blend) if args.save_blend else None
    for p in [out_fbx] + ([out_blend] if out_blend else []):
        if is_inside(p, PKG_DIR):
            sys.exit("error: refusing to write inside the package (%s): %s" % (PKG_DIR, p))
    return out_fbx, out_blend


OUT_FBX, OUT_BLEND = resolve_outputs(parse_args())

CHROME, WHITE = 0, 1
CREASE_DEG = 35.0


def u2b(p):
    """Unity ローカル (x, y, z) → Blender (bx, by, bz)。線形写像(ベクトルにもそのまま使える)。"""
    return Vector((-p[0], -p[2], p[1]))


def b2u(p):
    return Vector((-p[0], p[2], -p[1]))


def V(*a):
    return Vector(a)


class Part:
    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.uvl = self.bm.loops.layers.uv.new("UVMap")
        self.uv2 = self.bm.loops.layers.uv.new("UVMap_Lightmap")

    # ---- 旋盤(回転体)。profile = [(r, h, mat)]。mat はその点から次の点への線分の材質。
    def lathe(self, origin, axis, ref, profile, segs, closed=False, sweep=None, crease=CREASE_DEG):
        bm, uvl = self.bm, self.uvl
        a = Vector(axis).normalized()
        e1 = (Vector(ref) - a * Vector(ref).dot(a)).normalized()
        e2 = a.cross(e1).normalized()
        origin = Vector(origin)
        n = len(profile)
        full = sweep is None
        t0, t1 = (0.0, 2 * math.pi) if full else (math.radians(sweep[0]), math.radians(sweep[1]))
        nj = segs
        thetas = [t0 + (t1 - t0) * j / segs for j in range(segs + (0 if full else 1))]

        def pt(r, h, th):
            return origin + e1 * (r * math.cos(th)) + e2 * (r * math.sin(th)) + a * h

        ring = []
        for (r, h, m) in profile:
            if r < 1e-9:
                v = bm.verts.new(u2b(pt(0, h, 0)))
                ring.append([v] * len(thetas))
            else:
                ring.append([bm.verts.new(u2b(pt(r, h, th))) for th in thetas])
        # 累積弧長(UV の v)
        S = [0.0]
        for i in range(1, n):
            S.append(S[-1] + math.hypot(profile[i][0] - profile[i - 1][0], profile[i][1] - profile[i - 1][1]))
        segs_idx = [(i, (i + 1) % n) for i in range(n)] if closed else [(i, i + 1) for i in range(n - 1)]
        faces = []
        for (i, i2) in segs_idx:
            r1, h1, m = profile[i]
            r2, h2, _ = profile[i2]
            dr, dh = r2 - r1, h2 - h1
            seglen = math.hypot(dr, dh)
            if seglen < 1e-12:
                continue
            is_cap = abs(dh) < 1e-12
            v1 = S[i]
            v2 = S[i] + seglen
            for j in range(nj):
                j2 = (j + 1) % len(thetas) if full else j + 1
                th_a, th_b = thetas[j], (thetas[j2] if not (full and j2 == 0) else t1)
                quad = [(ring[i][j], r1, th_a, v1), (ring[i][j2], r1, th_b, v1),
                        (ring[i2][j2], r2, th_b, v2), (ring[i2][j], r2, th_a, v2)]
                # 重複頂点(極)を除く
                ded = []
                for q in quad:
                    if not ded or ded[-1][0] is not q[0]:
                        ded.append(q)
                if len(ded) > 1 and ded[0][0] is ded[-1][0]:
                    ded.pop()
                if len(ded) < 3:
                    continue
                thm = 0.5 * (th_a + th_b)
                radial = e1 * math.cos(thm) + e2 * math.sin(thm)
                hint = radial * dh - a * dr
                pts = [x[0].co for x in ded]
                nb = (pts[1] - pts[0]).cross(pts[2] - pts[0])
                if nb.dot(u2b(hint)) < 0:
                    ded.reverse()
                f = bm.faces.new([x[0] for x in ded])
                f.material_index = m
                f.smooth = True
                for l, (vv, r, th, vs) in zip(f.loops, ded):
                    if is_cap:
                        p = pt(r, 0, th) - origin
                        l[uvl].uv = (p.dot(e1), p.dot(e2))
                    else:
                        l[uvl].uv = (th * max(r, 1e-6), vs)
                faces.append(f)
        # 端面(部分スイープ)
        if not full:
            for (jj, sgn) in ((0, -1), (len(thetas) - 1, 1)):
                th = thetas[jj]
                tang = (-e1 * math.sin(th) + e2 * math.cos(th)) * sgn
                vs = [ring[i][jj] for i in range(n)]
                pts = [v.co for v in vs]
                nb = (pts[1] - pts[0]).cross(pts[2] - pts[0])
                if nb.dot(u2b(tang)) < 0:
                    vs.reverse()
                f = bm.faces.new(vs)
                f.material_index = profile[0][2]
                f.smooth = True
                for l in f.loops:
                    p = b2u(l.vert.co) - origin
                    l[uvl].uv = (p.dot(e1 if abs(math.sin(th)) < 0.5 else e2), p.dot(a))
        # 折れ目をシャープ辺にする
        for i in range(n):
            if not closed and (i == 0 or i == n - 1):
                continue
            pa, pb, pc = profile[(i - 1) % n], profile[i], profile[(i + 1) % n]
            t_in = Vector((pb[0] - pa[0], pb[1] - pa[1], 0))
            t_out = Vector((pc[0] - pb[0], pc[1] - pb[1], 0))
            if t_in.length < 1e-12 or t_out.length < 1e-12:
                continue
            ang = math.degrees(t_in.angle(t_out))
            if ang > crease:
                for j in range(len(thetas) - (0 if full else 1)):
                    va, vb = ring[i][j], ring[i][(j + 1) % len(thetas)]
                    if va is vb:
                        continue
                    e = bm.edges.get((va, vb))
                    if e:
                        e.smooth = False
        if not full:
            for jj in (0, len(thetas) - 1):
                for i in range(n):
                    va, vb = ring[i][jj], ring[(i + 1) % n][jj]
                    if va is vb:
                        continue
                    e = bm.edges.get((va, vb))
                    if e:
                        e.smooth = False
        return faces

    def cyl(self, origin, axis, ref, r, h0, h1, mat, segs=16):
        return self.lathe(origin, axis, ref, [(0, h0, mat), (r, h0, mat), (r, h1, mat), (0, h1, mat)], segs)

    # ---- 面取り付きの箱(Unity 座標の範囲)。面ごとフラット、UV は面ごとの平面投影。
    def box(self, x0, x1, y0, y1, z0, z1, mat, chamfer=0.004):
        bm, uvl = self.bm, self.uvl
        before = set(bm.faces)
        res = bmesh.ops.create_cube(bm, size=1.0)
        verts = res["verts"]
        for v in verts:
            ux = x0 if v.co.x < 0 else x1
            uy = y0 if v.co.y < 0 else y1
            uz = z0 if v.co.z < 0 else z1
            v.co = u2b((ux, uy, uz))
        edges = list({e for v in verts for e in v.link_edges})
        if chamfer > 0:
            bmesh.ops.bevel(bm, geom=edges, offset=chamfer, segments=1, affect="EDGES")
        center = u2b(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2))
        for f in [f for f in bm.faces if f not in before]:
            f.normal_update()
            if f.normal.dot(f.calc_center_median() - center) < 0:
                f.normal_flip()
            f.normal_update()
            f.material_index = mat
            f.smooth = False
            nu = b2u(f.normal)
            ax = [abs(nu[0]), abs(nu[1]), abs(nu[2])]
            for l in f.loops:
                x, y, z = b2u(l.vert.co)
                if ax[1] >= ax[0] and ax[1] >= ax[2]:
                    l[uvl].uv = (x, z)
                elif ax[0] >= ax[2]:
                    l[uvl].uv = (z, y)
                else:
                    l[uvl].uv = (x, y)

    def finish(self, mat_objs):
        bm = self.bm
        # UV2 は後で smart_project(bpy.ops)。まず UV1 をコピーしておく
        for f in bm.faces:
            for l in f.loops:
                l[self.uv2].uv = l[self.uvl].uv
        bm.normal_update()
        me = bpy.data.meshes.new(self.name)
        bm.to_mesh(me)
        bm.free()
        for m in mat_objs:
            me.materials.append(m)
        ob = bpy.data.objects.new(self.name, me)
        bpy.context.scene.collection.objects.link(ob)
        return ob


def make_materials():
    mats = []
    for name, col, metal, rough in (("ShowerChrome", (0.82, 0.84, 0.86, 1), 0.65, 0.25),
                                    ("ShowerWhiteResin", (0.93, 0.93, 0.92, 1), 0.0, 0.5)):
        m = bpy.data.materials.new(name)
        m.use_nodes = True
        bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
        bsdf.inputs["Base Color"].default_value = col
        bsdf.inputs["Metallic"].default_value = metal
        bsdf.inputs["Roughness"].default_value = rough
        mats.append(m)
    return mats


# ============================================================ ヘッド
HEAD_PLATE_Y = 0.0805          # 散水板の中心の高さ
HEAD_PLATE_R = 0.0294          # 散水板の半径 → 幅 0.0588
HEAD_FRONT_Z = 0.0125          # 散水板の前面(縁)


def build_head(mats):
    p = Part("ShowerHead")
    Y, Z = V(0, 1, 0), V(0, 0, 1)
    # ハンドル(下端 y=-0.109 = ホースの取り付け口。口金だけメッキ、握りは白樹脂。少し絞ったテーパー)
    handle = [
        (0.0000, -0.1090, CHROME),
        (0.0092, -0.1090, CHROME),
        (0.0105, -0.1075, CHROME),
        (0.0105, -0.0900, CHROME),
        (0.0094, -0.0885, WHITE),
        (0.0090, -0.0600, WHITE),
        (0.0080, -0.0200, WHITE),
        (0.0092, 0.0200, WHITE),
        (0.0102, 0.0480, WHITE),
        (0.0102, 0.0560, WHITE),
        (0.0000, 0.0560, WHITE),
    ]
    p.lathe(V(0, 0, 0), Y, V(1, 0, 0), handle, 12)
    # 散水板(円盤。前面は中央へ向かって浅く凹む皿状)
    plate = [
        (0.0000, -0.0125, WHITE),
        (0.0200, -0.0125, WHITE),
        (0.0270, -0.0108, WHITE),
        (0.0294, -0.0075, WHITE),
        (0.0294, 0.0100, WHITE),
        (0.0284, 0.0125, WHITE),
        (0.0262, 0.0125, CHROME),
        (0.0000, 0.0090, CHROME),
    ]
    c = V(0, HEAD_PLATE_Y, 0)
    p.lathe(c, Z, V(1, 0, 0), plate, 20)
    # 散水穴(皿の上の小さな白樹脂の突起 19 個: 中心 1 + 半径 8.5 mm に 6 + 半径 17 mm に 12)
    nubs = [(0.0, 0.0)]
    nubs += [(0.0085 * math.cos(2 * math.pi * k / 6 + 0.26), 0.0085 * math.sin(2 * math.pi * k / 6 + 0.26)) for k in range(6)]
    nubs += [(0.0170 * math.cos(2 * math.pi * k / 12), 0.0170 * math.sin(2 * math.pi * k / 12)) for k in range(12)]
    for (nx, ny) in nubs:
        rr = math.hypot(nx, ny)
        zs = 0.0090 + 0.0035 * rr / 0.0262 - 0.0004
        p.lathe(V(nx, HEAD_PLATE_Y + ny, zs), Z, V(1, 0, 0),
                [(0.0016, 0, WHITE), (0.0011, 0.0014, WHITE), (0, 0.0014, WHITE)], 5)
    return p.finish(mats)


# ============================================================ 混合栓
OUTLET_Z = 0.101


def build_mixer(mats):
    p = Part("ShowerMixer")
    Y, Z = V(0, 1, 0), V(0, 0, 1)
    # 本体(壁付けの横長ボディ)
    p.box(-0.160, 0.060, 0.030, 0.090, 0.000, 0.075, CHROME, 0.005)
    # 吐水管: 壁側から水平に出て、ホース出口へ折れる
    p.cyl(V(0, 0.050, 0.065), Z, V(1, 0, 0), 0.0105, 0.0, OUTLET_Z - 0.065, CHROME, 14)
    # ホースの出口(短い円筒、下端 y=0 が HoseStart の位置。中心 z=0.101)
    outlet = [
        (0.0000, 0.0000, CHROME),
        (0.0100, 0.0000, CHROME),
        (0.0125, 0.0030, CHROME),
        (0.0125, 0.0160, CHROME),
        (0.0110, 0.0180, CHROME),
        (0.0110, 0.0610, CHROME),
        (0.0000, 0.0610, CHROME),
    ]
    p.lathe(V(0, 0, OUTLET_Z), Y, V(1, 0, 0), outlet, 16)
    # レバー(軸 + 横長の白樹脂バー)
    p.cyl(V(-0.100, 0.090, 0.0375), Y, V(1, 0, 0), 0.0065, 0.0, 0.016, CHROME, 12)
    p.box(-0.185, -0.065, 0.104, 0.118, 0.0285, 0.0465, WHITE, 0.003)
    return p.finish(mats)


# ============================================================ 掛け金具
HOLDER_ALPHA = 11.87           # ヘッドの休止姿勢(X 周りの傾き、度)
HOLDER_CLIP_Z = 0.0813         # クリップ中心の壁からの距離
HOLDER_CLIP_HALF_H = 0.011
HOLDER_CLIP_RIN = 0.0098
HOLDER_CLIP_ROUT = 0.0148


def build_holder(mats):
    p = Part("ShowerHolder")
    Z = V(0, 0, 1)
    al = math.radians(HOLDER_ALPHA)
    u = V(0, math.cos(al), math.sin(al))       # ハンドル軸(クリップの軸)
    w = V(0, -math.sin(al), math.cos(al))      # クリップの開口方向(ほぼ +Z)
    # 壁板
    plate = [(0.0, 0.0, CHROME), (0.024, 0.0, CHROME), (0.024, 0.005, CHROME), (0.0225, 0.007, CHROME), (0.0, 0.007, CHROME)]
    p.lathe(V(0, 0, 0), Z, V(1, 0, 0), plate, 20)
    # アーム(壁板から水平にクリップの背面へ)
    p.cyl(V(0, 0, 0.004), Z, V(1, 0, 0), 0.0065, 0.0, 0.066, CHROME, 12)
    # クリップ(前方が開いた C 字。ハンドルが前から入る)
    h = HOLDER_CLIP_HALF_H
    clip = [(HOLDER_CLIP_RIN, -h, WHITE), (HOLDER_CLIP_ROUT, -h, WHITE), (HOLDER_CLIP_ROUT, h, WHITE), (HOLDER_CLIP_RIN, h, WHITE)]
    p.lathe(V(0, 0, HOLDER_CLIP_Z), u, w, clip, 14, closed=True, sweep=(50, 310))
    return p.finish(mats)


def make_uv2(obs):
    for ob in obs:
        bpy.ops.object.select_all(action="DESELECT")
        ob.select_set(True)
        bpy.context.view_layer.objects.active = ob
        me = ob.data
        me.uv_layers.active_index = 1
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.uv.smart_project(angle_limit=math.radians(66.0), island_margin=0.02)
        bpy.ops.object.mode_set(mode="OBJECT")
        me.uv_layers.active_index = 0


def report(ob):
    me = ob.data
    tris = sum(len(p.vertices) - 2 for p in me.polygons)
    us = [b2u(v.co) for v in me.vertices]
    print("%s verts %d tris %d  unity x %.4f..%.4f  y %.4f..%.4f  z %.4f..%.4f  size (%.4f, %.4f, %.4f)  uv=%s" % (
        ob.name, len(me.vertices), tris,
        min(u.x for u in us), max(u.x for u in us), min(u.y for u in us), max(u.y for u in us),
        min(u.z for u in us), max(u.z for u in us),
        max(u.x for u in us) - min(u.x for u in us), max(u.y for u in us) - min(u.y for u in us),
        max(u.z for u in us) - min(u.z for u in us), [l.name for l in me.uv_layers]))


bpy.ops.wm.read_homefile(use_empty=True)
mats = make_materials()
obs = [build_head(mats), build_mixer(mats), build_holder(mats)]
make_uv2(obs)
for ob in obs:
    report(ob)
bpy.ops.object.select_all(action="DESELECT")
for ob in obs:
    ob.select_set(True)
bpy.context.view_layer.objects.active = obs[0]
os.makedirs(os.path.dirname(OUT_FBX), exist_ok=True)
bpy.ops.export_scene.fbx(filepath=OUT_FBX, use_selection=True, bake_space_transform=True)
print("exported", OUT_FBX)
if OUT_BLEND:
    os.makedirs(os.path.dirname(OUT_BLEND), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=OUT_BLEND)
    print("saved", OUT_BLEND)
