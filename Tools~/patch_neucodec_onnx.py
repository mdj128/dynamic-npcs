"""Make the neuphonic/neucodec-onnx-decoder importable by Unity Inference Engine / Sentis.

The exported graph implements torch's qkv.unbind(0) in each attention block as
SplitToSequence + SequenceAt, which Unity's ONNX importer does not support
("SplitToSequence not supported"). Both ops together are equivalent to a Gather
with a scalar index on the split axis, which Unity does support. This rewrites
the graph accordingly; the result is numerically identical (verified bit-exact
against onnxruntime).

One-time, dev machine only:

    pip install onnx
    python patch_neucodec_onnx.py path/to/neucodec-decoder.onnx

Overwrites the file in place (pass a second path to write elsewhere). If
onnxruntime is installed, the patched model is verified against the original.
"""
import sys

import onnx
from onnx import helper, numpy_helper


def patch(model):
    g = model.graph
    inits = {i.name: i for i in g.initializer}
    const_nodes = {o: n for n in g.node if n.op_type == "Constant" for o in n.output}

    def const_value(name):
        if name in inits:
            return numpy_helper.to_array(inits[name])
        if name in const_nodes:
            for a in const_nodes[name].attribute:
                if a.name == "value":
                    return numpy_helper.to_array(a.t)
        return None

    # sequence output name -> (data input, axis)
    seq = {}
    for n in g.node:
        if n.op_type != "SplitToSequence":
            continue
        attrs = {a.name: a.i for a in n.attribute}
        if len(n.input) != 1 or attrs.get("keepdims", 1) != 0:
            raise SystemExit(f"{n.name}: unexpected SplitToSequence form, not handled")
        seq[n.output[0]] = (n.input[0], attrs.get("axis", 0))

    new_nodes = []
    replaced = 0
    for n in g.node:
        if n.op_type == "SplitToSequence" and n.output[0] in seq:
            replaced += 1
            continue
        if n.op_type == "SequenceAt" and n.input[0] in seq:
            data, axis = seq[n.input[0]]
            idx = const_value(n.input[1])
            if idx is None or idx.ndim != 0:
                raise SystemExit(f"{n.name}: position {n.input[1]} is not a scalar constant")
            new_nodes.append(helper.make_node(
                "Gather", [data, n.input[1]], list(n.output),
                name=n.name + "_as_gather", axis=axis))
            replaced += 1
            continue
        new_nodes.append(n)

    del g.node[:]
    g.node.extend(new_nodes)
    leftover = [n.op_type for n in g.node if "Sequence" in n.op_type]
    if leftover:
        raise SystemExit(f"leftover sequence ops after patch: {leftover}")
    return replaced


def verify(src, dst):
    try:
        import numpy as np
        import onnxruntime as ort
    except ImportError:
        print("onnxruntime not installed - skipping numerical verification")
        return
    codes = np.random.default_rng(0).integers(0, 65536, size=(1, 1, 75), dtype=np.int64)

    def run(path):
        sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
        inp = sess.get_inputs()[0]
        x = codes.astype(np.int32) if "int32" in inp.type else codes
        return sess.run(None, {inp.name: x})[0]

    diff = float(abs(run(src) - run(dst)).max())
    print(f"max abs diff vs original: {diff}")
    if diff > 1e-5:
        raise SystemExit("verification FAILED - patched output differs from original")


def main():
    if len(sys.argv) not in (2, 3):
        raise SystemExit(f"usage: {sys.argv[0]} model.onnx [patched.onnx]")
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) == 3 else src

    model = onnx.load(src)
    replaced = patch(model)
    onnx.checker.check_model(model, full_check=False)

    if dst == src:
        import shutil
        backup = src + ".orig"
        shutil.copyfile(src, backup)
        print(f"original backed up to {backup}")
    onnx.save(model, dst)
    print(f"replaced {replaced} nodes, saved {dst}")
    verify(src if dst != src else src + ".orig", dst)
    print("done - reimport the asset in Unity and reassign it in settings")


if __name__ == "__main__":
    main()
