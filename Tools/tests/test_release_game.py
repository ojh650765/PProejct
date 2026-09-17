import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import deploy_webgl
import release_game


class ReleaseSafetyTests(unittest.TestCase):
    def test_other_unity_project_cannot_be_built(self):
        response = {"contents": [{"text": json.dumps({"data": {"projectRoot": "C:/wrong-project"}})}]}
        with patch.object(release_game, "UnityMCP") as factory:
            factory.return_value.rpc.return_value = response
            with self.assertRaisesRegex(RuntimeError, "another project"):
                release_game.build()
            factory.return_value.call.assert_not_called()

    def test_git_publish_preserves_history_and_uses_named_remote_branch(self):
        calls = []
        def git(directory, *args):
            calls.append(args)
            return subprocess.CompletedProcess(args, 0, " M index.html\n" if args[0] == "status" else "abc123\n", "")
        with patch.object(deploy_webgl, "_git", side_effect=git):
            self.assertTrue(deploy_webgl.step_deploy("unused", False))
        self.assertIn(("push", "origin", "HEAD:gh-pages"), calls)
        self.assertFalse(any("--force" in c or "reset" in c or "commit-tree" in c for c in calls))

    def test_failed_commit_prevents_push(self):
        calls = []
        def git(directory, *args):
            calls.append(args)
            return subprocess.CompletedProcess(args, 1 if args[0] == "commit" else 0, " M index.html", "failure")
        with patch.object(deploy_webgl, "_git", side_effect=git):
            with self.assertRaises(deploy_webgl.GateFailure):
                deploy_webgl.step_deploy("unused", False)
        self.assertFalse(any(c[0] == "push" for c in calls))


if __name__ == "__main__":
    unittest.main()
