"""One-off probe of the trained joblib bundle.

Kept in the repo because the binary layout of forest.bin is derived from the exact
shapes reported here; if the upstream model is ever retrained, re-run this first.
"""
from __future__ import annotations

import sys

from common import REPO, load_bundle


def main() -> int:
    bundle = load_bundle()
    print("bundle keys:", sorted(bundle.keys()))
    print("selected_probability_model:", repr(bundle.get("selected_probability_model")))
    print("calibrated_model:", bundle.get("calibrated_model"))
    print("feature_columns:", tuple(bundle["feature_columns"]))

    forest = bundle["raw_model"]
    print("raw_model:", type(forest).__name__)
    print("n_estimators:", len(forest.estimators_))
    print("classes_:", forest.classes_, forest.classes_.dtype)
    print("n_features_in_:", forest.n_features_in_)
    print("total nodes:", sum(e.tree_.node_count for e in forest.estimators_))
    print("max depth:", max(e.tree_.max_depth for e in forest.estimators_))

    tree = forest.estimators_[0].tree_
    print("tree0 node_count:", tree.node_count)
    print("tree0 value.shape:", tree.value.shape)
    print("tree0 value[0]:", tree.value[0])
    print("dtypes:", tree.feature.dtype, tree.threshold.dtype, tree.children_left.dtype)
    print("repo root:", REPO)
    return 0


if __name__ == "__main__":
    sys.exit(main())
