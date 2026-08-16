"""Texture import settings for the human sprite sheets, written as .meta files.

Unity writes a .meta the first time it sees a texture, using its defaults, and
its defaults are wrong for this art in four ways.  Three of them look fine in
the editor and only show up later; the fourth resamples the artwork outright:

  enableMipMap: 1      The character dissolves into mush as the HD-2D camera
                       pulls back.  There is no camera distance at which a
                       mipmap of a 32 px sprite is what you want.
  textureCompression   BC/DXT on Standalone and WebGL.  A 14-colour indexed
    : 1                palette shatters into gradients, and the flat blocks
                       of Gen 4 field art are the worst possible input for a
                       block-truncation codec.
  filterMode: 1        Bilinear smears a 1 px keyline into the background.
    (bilinear)         (Unity happened to pick Point here, but it is set
                       explicitly rather than left to luck.)
  nPOTScale: 1         "Scale to nearest power of two".  The people sheets are
                       256x96 and 256x64; 96 is not a power of two, so this
                       setting invites the importer to rescale the sheet to
                       256x128 -- an actual resampling of the artwork, applied
                       after the pipeline went to some trouble not to.

So the .meta files are authored rather than inherited.  Both places the sheets
live get one: the authoring folder (Assets/Game/Art/Sprites/People) and the
staged copy under Resources.

An existing .meta is rewritten in place with its **GUID preserved**.  A GUID is
the identity Unity uses for every reference to the asset; regenerating one
silently breaks every prefab, scene and serialized field pointing at it.
"""

from __future__ import annotations

import hashlib
import os
import re

TEMPLATE = """fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
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
    filterMode: 0
    aniso: 0
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 100
  spriteMode: 0
  spriteExtrude: 0
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: {ppu}
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 0
  alphaUsage: 1
  alphaIsTransparency: 1
  spriteTessellationDetail: -1
  textureType: 0
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
    textureCompression: 0
    compressionQuality: 100
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 1
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  - serializedVersion: 4
    buildTarget: Standalone
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 0
    compressionQuality: 100
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 1
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  - serializedVersion: 4
    buildTarget: WebGL
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 0
    compressionQuality: 100
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 1
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    customData:
    physicsShape: []
    bones: []
    spriteID:
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

GUID_RE = re.compile(r"^guid:\s*([0-9a-fA-F]{32})\s*$", re.M)


def stable_guid(key: str) -> str:
    """A deterministic GUID, so a sheet built from scratch twice keeps its id."""
    return hashlib.md5(("pokelab/people/" + key).encode("utf-8")).hexdigest()


def existing_guid(meta_path: str) -> str | None:
    try:
        with open(meta_path, encoding="utf-8") as fh:
            m = GUID_RE.search(fh.read())
        return m.group(1) if m else None
    except OSError:
        return None


def ensure_meta(png_path: str, key: str, ppu: float) -> str:
    """Write correct import settings beside a PNG.

    Returns one of "created", "updated", "unchanged".  An existing GUID always
    wins over the derived one: Unity may already have assigned an id and
    something may already reference it.
    """
    meta = png_path + ".meta"
    guid = existing_guid(meta) or stable_guid(key)
    want = TEMPLATE.format(guid=guid, ppu=round(ppu, 4))
    if os.path.exists(meta):
        with open(meta, encoding="utf-8") as fh:
            if fh.read() == want:
                return "unchanged"
        with open(meta, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(want)
        return "updated"
    with open(meta, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(want)
    return "created"
