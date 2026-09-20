# -*- coding: utf-8 -*-
"""判据资产：生成**程序化特效贴图**（枪口火焰 / 弹痕 / 火星）及其 Unity Sprite 导入元数据。

## 为什么是程序化生成（而不是取原版素材）
原版 CS 1.6 的枪口火焰是 `sprites/muzzleflash1..4.spr`、弹痕是 `decals.wad` 里的 `{shot1..5`。
本工程 `原版资源/` 目录（含那两个载体）**已不在仓库里**，而 archive.org（原版客户端 ISO）在这台机器上
**连不上**（实测 20s 超时，见 `策划/素材调研.md` 的载体表）。⇒ 按 skill 的载体降级链退一格：**程序化生成**，
并在 `client/资源欠缺清单.md` 登记为「本项目新增（原版载体不可得）」，拿到原版 sprite/decal 后**只换文件**。

## 产物（幂等，重跑覆盖）
  Assets/Resources/UI/Art/fx_muzzleflash.png (64x64) + .meta
  Assets/Resources/UI/Art/fx_bullethole.png  (32x32) + .meta
  Assets/Resources/UI/Art/fx_spark.png       (16x16) + .meta

用法：python tools/probes/make-fx-sprites.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'UI', 'Art')


def write_png(path, w, h, rgba):
    """最小 PNG 写入器（RGBA8，无隔行）。rgba = 逐行 bytes（每像素 4 字节）。"""
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        raw.append(0)                      # filter type 0
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        return c + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(bytes(raw), 9))
    png += chunk(b'IEND', b'')
    with open(path, 'wb') as f:
        f.write(png)


def px(buf, w, x, y, r, g, b, a):
    i = (y * w + x) * 4
    buf[i] = max(0, min(255, int(r)))
    buf[i + 1] = max(0, min(255, int(g)))
    buf[i + 2] = max(0, min(255, int(b)))
    buf[i + 3] = max(0, min(255, int(a)))


def make_muzzleflash():
    """枪口火焰：中心白热 + 一圈黄橙 + 六根尖刺（原版 spr 观感就是"星形亮斑"）。"""
    w = h = 64
    buf = bytearray(w * h * 4)
    cx = cy = (w - 1) / 2.0
    spikes = 6
    import math
    for y in range(h):
        for x in range(w):
            dx = (x - cx) / (w / 2.0); dy = (y - cy) / (h / 2.0)
            r = math.hypot(dx, dy)
            ang = math.atan2(dy, dx)
            # 星形：半径随角度起伏（尖刺）
            star_r = 0.42 + 0.42 * abs(math.cos(spikes * ang / 2.0)) ** 2.0
            v = max(0.0, 1.0 - r / max(1e-6, star_r))
            core = max(0.0, 1.0 - r / 0.34)
            a = min(1.0, v * 1.5 + core)
            if a <= 0.011:
                continue
            rr = 255
            gg = 190 + 60 * core
            bb = 90 + 150 * core * core
            px(buf, w, x, y, rr, gg, bb, a * 255)
    write_png(os.path.join(OUT, 'fx_muzzleflash.png'), w, h, buf)


def make_bullethole():
    """弹痕：不规则暗孔 + 一圈浅灰碎屑（原版是灰度贴图，靠映射缩放上墙）。"""
    w = h = 32
    buf = bytearray(w * h * 4)
    cx = cy = (w - 1) / 2.0
    import math
    for y in range(h):
        for x in range(w):
            dx = (x - cx) / (w / 2.0); dy = (y - cy) / (h / 2.0)
            ang = math.atan2(dy, dx)
            r = math.hypot(dx, dy)
            # 孔：半径带角度抖动（像弹孔边缘）
            hole = 0.30 + 0.10 * math.sin(5 * ang) + 0.06 * math.sin(11 * ang + 1.7)
            ring = hole + 0.34 + 0.10 * math.sin(7 * ang + 0.6)
            if r <= hole:
                t = r / max(1e-6, hole)
                px(buf, w, x, y, 12 + 30 * t, 10 + 26 * t, 8 + 22 * t, 235)
            elif r <= ring:
                t = (r - hole) / max(1e-6, (ring - hole))
                a = (1.0 - t) * 165
                px(buf, w, x, y, 96 + 60 * (1 - t), 90 + 56 * (1 - t), 80 + 50 * (1 - t), a)
    write_png(os.path.join(OUT, 'fx_bullethole.png'), w, h, buf)


def make_spark():
    """击中火星：中心白、边缘黄的小圆点。"""
    w = h = 16
    buf = bytearray(w * h * 4)
    import math
    cx = cy = (w - 1) / 2.0
    for y in range(h):
        for x in range(w):
            r = math.hypot((x - cx) / (w / 2.0), (y - cy) / (h / 2.0))
            a = max(0.0, 1.0 - r)
            if a <= 0.02:
                continue
            px(buf, w, x, y, 255, 235 - 60 * r, 150 - 110 * r, a * 230)
    write_png(os.path.join(OUT, 'fx_spark.png'), w, h, buf)


META = """fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable:
  - first:
      213: {iid}
    second: {name}_0
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 0
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 50
  spriteMode: 1
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 0
  alphaUsage: 1
  alphaIsTransparency: 1
  spriteTessellationMethod: 0
  spriteTessellationDetail: -1
  spriteGeometrySubdivision: -1
  textureType: 8
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites:
    - serializedVersion: 2
      name: {name}_0
      rect:
        serializedVersion: 2
        x: 0
        y: 0
        width: {w}
        height: {h}
      alignment: 0
      pivot: {{x: 0.5, y: 0.5}}
      border: {{x: 0, y: 0, z: 0, w: 0}}
      customData: 
      outline: []
      physicsShape: []
      tessellationDetail: -1
      bones: []
      spriteID: {sprite_guid}
      internalID: {iid}
      vertices: []
      indices: 
      edges: []
      weights: []
    outline: []
    customData: 
    physicsShape: []
    bones: []
    spriteID: {sheet_guid}
    internalID: 0
    vertices: []
    indices: 
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable: {{}}
  mipmapLimitGroupName: 
  pSDRemoveMatte: 0
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"""


def write_meta(png, name, w, h, seed):
    guid = ('%032x' % (0x7f3b0000 + seed))[:32]
    iid = -(0x4c00000000000000 + seed)
    sprite_guid = ('%032x' % (0x5e970000 + seed))[:32]
    sheet_guid = ('%032x' % (0x9a120000 + seed))[:32]
    with open(png + '.meta', 'w', encoding='utf-8', newline='\n') as f:
        f.write(META.format(guid=guid, name=name, w=w, h=h, iid=iid,
                            sprite_guid=sprite_guid + '0800000000000000',
                            sheet_guid=sheet_guid + '0800000000000000'))


def main():
    os.makedirs(OUT, exist_ok=True)
    make_muzzleflash()
    make_bullethole()
    make_spark()
    write_meta(os.path.join(OUT, 'fx_muzzleflash.png'), 'fx_muzzleflash', 64, 64, 0x11)
    write_meta(os.path.join(OUT, 'fx_bullethole.png'), 'fx_bullethole', 32, 32, 0x22)
    write_meta(os.path.join(OUT, 'fx_spark.png'), 'fx_spark', 16, 16, 0x33)
    print("已生成 3 张特效贴图 + .meta → %s" % OUT)
    for n in ('fx_muzzleflash.png', 'fx_bullethole.png', 'fx_spark.png'):
        p = os.path.join(OUT, n)
        print("   %-20s %d 字节" % (n, os.path.getsize(p)))


if __name__ == '__main__':
    main()
