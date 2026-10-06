import UnityPy, collections, struct, sys

DATA = r"C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\ThreeKingdom_Data"
env = UnityPy.load(DATA + r"\resources.assets", DATA + r"\globalgamemanagers.assets")

# map (file, path_id) -> class name for every MonoScript
scripts = {}
for fname, fobj in env.files.items():
    if not hasattr(fobj, "objects"):
        continue
    for pid, o in fobj.objects.items():
        if o.type.name == "MonoScript":
            try:
                d = o.read_typetree()
            except Exception:
                continue
            scripts[(o.assets_file.name, pid)] = (d.get("m_ClassName"), d.get("m_Namespace"))
print("MonoScripts resolved:", len(scripts))

# externals of resources.assets
res = None
for fname, fobj in env.files.items():
    if fname.endswith("resources.assets") or getattr(fobj, "name", "") == "resources.assets":
        res = fobj
if res is None:
    res = [f for n, f in env.files.items() if hasattr(f, "externals") and any("resources" in "" for _ in [])]
    # fallback: pick by name
    for n, f in env.files.items():
        if "resources.assets" in n:
            res = f
ext = [e.path for e in res.externals]
print("externals:", ext)

by_pid = {}
for pid, o in res.objects.items():
    by_pid[pid] = o

def resolve_script(file_id, path_id, curfile):
    if file_id == 0:
        target = curfile
    else:
        target = ext[file_id - 1]
    key = (target, path_id)
    if key in scripts:
        return scripts[key]
    # basename fallback
    for (f, p), v in scripts.items():
        if p == path_id and f.endswith(target.split("/")[-1]):
            return v
    return (None, None)

groups = collections.Counter()
sample = {}
for pid, o in res.objects.items():
    if o.type.name != "MonoBehaviour":
        continue
    raw = o.get_raw_data()
    file_id, path_id = struct.unpack_from("<iq", raw, 16)
    cls, ns = resolve_script(file_id, path_id, "resources.assets")
    nlen = struct.unpack_from("<i", raw, 28)[0]
    name = raw[32:32 + nlen].decode("utf8", "replace") if 0 <= nlen < 200 else "<bad>"
    key = (cls, ns, path_id, file_id, name if name else "")
    groups[(cls, ns, path_id, file_id)] += 1
    sample.setdefault((cls, ns, path_id, file_id), (pid, name))

print("\n=== MonoBehaviour class groups in resources.assets ===")
for k, v in groups.most_common():
    s = sample[k]
    print(f"{v:4d} | {k[0]} ({k[1]}) | script file_id={k[3]} path_id={k[2]} | sample obj path_id={s[0]} name={s[1]!r}")
