import UnityPy, sys

DATA = r"C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\ThreeKingdom_Data"
env = UnityPy.load(DATA + r"\resources.assets")
o = [x for x in env.objects if x.path_id == 6341][0]
t = o.assets_file.types[o.type_id]
print("class_id", t.class_id, "script_id", getattr(t, "script_id", None),
      "script_type_index", getattr(t, "script_type_index", None),
      "m_ClassName", getattr(t, "m_ClassName", None))
nodes = getattr(t, "nodes", None) or getattr(t, "node", None)
print("nodes type:", type(nodes))
if nodes is not None:
    try:
        lst = list(nodes)
        print("node count:", len(lst))
        for n in lst[:80]:
            print(" ", n.level, n.name, n.type, "meta", n.meta, "flags", getattr(n, "flags", None))
    except Exception as e:
        print("iter err", e)
