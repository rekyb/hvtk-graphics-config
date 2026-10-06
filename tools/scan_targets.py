import UnityPy, collections, struct, json, os, glob

DATA = r"C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\ThreeKingdom_Data"

# ---- namespaces / classes of interest ----
WATCH_NS = {
    "UnityEngine.Rendering.PostProcessing", "SCPE", "VolumetricFogAndMist",
    "DynamicFogAndMist", "Aura2API", "VolumetricLights", "GlobalSnowEffect",
    "UnityStandardAssets.ImageEffects", "UnityStandardAssets.Water",
    "Ceto", "AQUAS", "VLB",
}
WATCH_CLASS_EXACT = {"MirrorReflection", "ScreenSpaceCloudShadow", "IVolumetricFogRenderComponent"}

# pass-1 disable targets (settings -> set enabled=false ; components -> m_Enabled=0)
PASS1_SETTINGS = {
    ("UnityEngine.Rendering.PostProcessing", n) for n in
    ["AmbientOcclusion", "ChromaticAberration", "MotionBlur", "DepthOfField",
     "ScreenSpaceReflections", "Fog", "MultiScaleVO", "ScalableAO"]
} | {("SCPE", n) for n in ["AmbientOcclusion2D", "Fog", "CloudShadows", "Blur"]}

PASS1_COMPONENTS = {
    ("VolumetricFogAndMist", n) for n in
    ["VolumetricFog", "VolumetricFogPreT", "VolumetricFogPostT", "FogVolume",
     "VolumetricFogLightParams", "VolumetricFogMaterialIntegration", "ShadowMapCopy",
     "FogAreaCullingManager", "FogOfWarHole", "VolumetricFogDayCycleManager"]
} | {("Aura2API", n) for n in ["Aura", "AuraVolume", "AuraCamera", "AuraLight"]} | {
    ("VolumetricLights", "VolumetricLight"), ("VolumetricLights", "VolumetricLightAnimation"),
    ("UnityStandardAssets.ImageEffects", "MotionBlur"),
    ("UnityStandardAssets.ImageEffects", "GlobalFog"),
    ("Ceto", "PlanarReflection"), ("AQUAS", "AQUAS_Reflection"),
    ("UnityStandardAssets.Water", "PlanarReflection"),
    ("", "MirrorReflection"),
}

# catalog-only (report, do not disable)
CATALOG_EXTRA = {
    ("UnityEngine.Rendering.PostProcessing", n) for n in
    ["PostProcessProfile", "PostProcessLayer", "PostProcessVolume", "PostProcessDebug",
     "Bloom", "ColorGrading", "Vignette", "Grain", "AutoExposure", "LensDistortion",
     "UserLut", "Dithering", "Reflections", "TemporalAntialiasing",
     "FastApproximateAntialiasing", "SubpixelMorphologicalAntialiasing"]
} | {("DynamicFogAndMist", "DynamicFog")}

def interesting(cls, ns):
    if cls is None:
        return False
    return ns in WATCH_NS or cls in WATCH_CLASS_EXACT

# ---- pass 1: build MonoScript map from globalgamemanagers.assets ----
scripts = {}
env = UnityPy.load(os.path.join(DATA, "globalgamemanagers.assets"))
f = [fo for fo in env.files.values() if hasattr(fo, "objects")][0]
for pid, o in f.objects.items():
    if o.type.name == "MonoScript":
        try:
            d = o.read_typetree()
            scripts[("globalgamemanagers.assets", pid)] = (d.get("m_ClassName"), d.get("m_Namespace") or "")
        except Exception:
            pass
del env
print("scripts from ggm:", len(scripts))

files = [os.path.join(DATA, "globalgamemanagers.assets"),
         os.path.join(DATA, "resources.assets")] + sorted(
    p for p in glob.glob(os.path.join(DATA, "sharedassets*.assets"))) + sorted(
    p for p in glob.glob(os.path.join(DATA, "level*")) if not p.endswith(".resS"))

catalog = []          # (file, path_id, cls, ns, name, script_pid)
seen = set()
unresolved = collections.Counter()
per_file = collections.defaultdict(list)

def record(base, pid, cls, ns, name, spid):
    key = (base, pid)
    if key in seen:
        return
    seen.add(key)
    if interesting(cls, ns):
        catalog.append([base, pid, cls, ns, name, spid])
        per_file[base].append((cls, ns, name, pid))

for path in files:
    base = os.path.basename(path)
    if base == "globalgamemanagers.assets":
        continue
    env = UnityPy.load(path)
    f = None
    for fo in env.files.values():
        if hasattr(fo, "externals"):
            f = fo
            break
    if f is None:
        del env
        continue
    ext = [e.path for e in f.externals]

    # local MonoScripts first
    for pid, o in f.objects.items():
        if o.type.name == "MonoScript":
            try:
                d = o.read_typetree()
                scripts[(base, pid)] = (d.get("m_ClassName"), d.get("m_Namespace") or "")
            except Exception:
                pass

    for pid, o in f.objects.items():
        if o.type.name != "MonoBehaviour":
            continue
        raw = o.get_raw_data()
        if len(raw) < 32:
            continue
        file_id, spid = struct.unpack_from("<iq", raw, 16)
        nlen = struct.unpack_from("<i", raw, 28)[0]
        name = raw[32:32 + nlen].decode("utf8", "replace") if 0 <= nlen < 200 else ""
        target = base if file_id == 0 else (ext[file_id - 1] if 0 < file_id <= len(ext) else "?")
        key = None
        for k in ((target, spid), (os.path.basename(target), spid)):
            if k in scripts:
                key = k
                break
        if key is None:
            unresolved[(target, spid)] += 1
            continue
        cls, ns = scripts[key]
        record(base, pid, cls, ns, name, spid)
    del env

json.dump({"catalog": catalog}, open("catalog.json", "w", encoding="utf-8"), ensure_ascii=False, indent=0)

print("\n=== FILES containing watchlist objects ===")
for fn in sorted(per_file, key=lambda x: (x != "resources.assets", x)):
    entries = per_file[fn]
    c = collections.Counter((cls, ns) for cls, ns, _, _ in entries)
    p1 = [(cls, ns) for cls, ns, _, _ in entries if (ns, cls) in PASS1_SETTINGS or (ns, cls) in PASS1_COMPONENTS]
    p1c = collections.Counter(p1)
    print(f"\n{fn}: {len(entries)} watched objects; PASS1 targets: {len(p1)}")
    for (cls, ns), n in sorted(c.items(), key=lambda kv: -kv[1]):
        tag = "  <-- pass1 x%d" % p1c[(cls, ns)] if p1c[(cls, ns)] else ""
        print(f"    {n:4d}  {ns}.{cls}{tag}")

print("\nunresolved script refs (distinct):", len(unresolved))
for k, v in unresolved.most_common(15):
    print("   ", k, v)
