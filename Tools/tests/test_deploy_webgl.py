# -*- coding: utf-8 -*-
"""Tests for the deploy gate's pure logic: console triage and JSON sanity.

Run from the repo root:  python -m unittest discover -s Tools/tests -v
No build, no Chrome, no network required.
"""
import io
import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import deploy_webgl  # noqa: E402

# The seeded allowlist, mirrored here so the tests do not depend on the file's presence
# (the file itself is exercised separately in AllowlistFileTests).
ALLOWLIST = [
    "Hidden/CoreSRP/CoreCopy",
    "Hidden/Universal Render Pipeline/StencilDitherMaskSeed",
    "Hidden/Universal/HDRDebugView",
    "A second DialogueRunner arrived",
    "[EncounterDirector] A second one arrived",
    "There can be only one active Event System",
    "Trying to get length of sound which is not loaded yet",
]

# A trimmed copy of the real Temp/web_console.txt shape: startup noise, the three benign
# two-line shader errors (with the blank lines the real dump has between entries), the
# sound-length spam, and the duplicate-host guard notices.
CLEAN_FIXTURE = """\
You can reduce startup time if you configure your web server to add "Content-Encoding: br" response header when serving "Build/WebGL.data.unityweb" file.
[UnityCache] 'http://localhost:8765/Build/WebGL.data.unityweb' successfully downloaded and stored in the browser cache
[UnityMemory] Configuration Parameters - Can be set up in boot.config

    "memorysetup-allocator-temp-initial-block-size-main=262144"

Loading player data from data.unity3d
Initialize engine version: 6000.3.6f1 (bbb010bdb8a3)
Creating WebGL 2.0 context.
ERROR: Shader
Hidden/CoreSRP/CoreCopy shader is not supported on this GPU (none of subshaders/fallbacks are suitable)

ERROR: Shader
Hidden/Universal Render Pipeline/StencilDitherMaskSeed shader is not supported on this GPU (none of subshaders/fallbacks are suitable)

ERROR: Shader
Hidden/Universal/HDRDebugView shader is not supported on this GPU (none of subshaders/fallbacks are suitable)

Input System initialize...
UnloadTime: 1.099999 ms
Trying to get length of sound which is not loaded yet.
Trying to get length of sound which is not loaded yet.
There can be only one active Event System.

[EncounterDirector] A second one arrived with another scene; destroying the duplicate.

[Dialogue] A second DialogueRunner arrived on 'GameHosts'. The first one is still in charge and this copy has been removed; two of them means half the game talks to the wrong one.

[GameBoot] Services ready in 7470 ms — 721 species loaded.
"""


def triage_text(text, allowlist=ALLOWLIST):
    return deploy_webgl.triage_console(text.splitlines(), allowlist)


class TriageCleanRunTests(unittest.TestCase):
    def test_real_shape_clean_run_has_no_findings(self):
        findings, allowlisted = triage_text(CLEAN_FIXTURE)
        self.assertEqual(findings, [])
        # exactly the three "ERROR: Shader" lines are signature hits that get allowlisted
        self.assertEqual(allowlisted, 3)

    def test_empty_console(self):
        findings, allowlisted = deploy_webgl.triage_console([], ALLOWLIST)
        self.assertEqual((findings, allowlisted), ([], 0))


class TriageSignatureTests(unittest.TestCase):
    def assert_caught(self, line):
        findings, _ = triage_text(CLEAN_FIXTURE + line + "\n")
        self.assertEqual(len(findings), 1, "expected exactly one finding for: %r" % line)
        self.assertEqual(findings[0][1], line)

    def test_nullreference(self):
        self.assert_caught("NullReferenceException: Object reference not set to an instance of an object")

    def test_indexoutof(self):
        self.assert_caught("IndexOutOfRangeException: Index was outside the bounds of the array.")

    def test_capture_web_exception_prefix(self):
        # capture_web.py writes page exceptions as "EXCEPTION: <text> <description>"
        self.assert_caught("EXCEPTION: Uncaught abort(both async and sync fetching of the wasm failed)")

    def test_uncaught_is_case_insensitive(self):
        self.assert_caught("uncaught TypeError: Cannot read properties of null")
        self.assert_caught("Uncaught ReferenceError: unityInstance is not defined")

    def test_error_cs(self):
        self.assert_caught("Assets/Game/Scripts/Foo.cs(12,3): error CS1002: ; expected")

    def test_failed_to(self):
        self.assert_caught("Failed to load resource: the server responded with a status of 404 ()")

    def test_would_not_parse(self):
        # the JsonUtility-degrades-silently class this gate exists for
        self.assert_caught("[Species] StreamingAssets/pokelab/species.json would not parse; running with an empty table")

    def test_line_numbers_are_one_based_and_correct(self):
        text = "fine\nfine too\nNullReferenceException: boom\n"
        findings, _ = triage_text(text)
        self.assertEqual(findings, [(3, "NullReferenceException: boom")])


class TriageShaderShapeTests(unittest.TestCase):
    def test_unknown_shader_error_is_a_finding(self):
        text = CLEAN_FIXTURE + "ERROR: Shader \nCustom/Billboard shader is not supported on this GPU\n"
        findings, allowlisted = triage_text(text)
        self.assertEqual(len(findings), 1)
        self.assertEqual(findings[0][1], "ERROR: Shader")
        self.assertEqual(allowlisted, 3)

    def test_shader_name_on_next_line_after_blank_still_allowlists(self):
        # tolerate a blank line sneaking between the ERROR line and the name
        text = "ERROR: Shader \n\nHidden/Universal/HDRDebugView shader is not supported\n"
        findings, allowlisted = triage_text(text)
        self.assertEqual(findings, [])
        self.assertEqual(allowlisted, 1)

    def test_shader_error_at_end_of_dump_with_no_next_line(self):
        findings, _ = triage_text("ERROR: Shader ")
        self.assertEqual(len(findings), 1)


class TriageAllowlistBehaviorTests(unittest.TestCase):
    def test_empty_allowlist_fails_the_shader_lines(self):
        findings, allowlisted = triage_text(CLEAN_FIXTURE, allowlist=[])
        self.assertEqual(len(findings), 3)
        self.assertEqual(allowlisted, 0)

    def test_allowlist_does_not_mute_unrelated_errors(self):
        text = CLEAN_FIXTURE + "NullReferenceException in EncounterDirector.Tick\n"
        findings, _ = triage_text(text)
        self.assertEqual(len(findings), 1)

    def test_allowlisted_exception_line_is_counted_not_reported(self):
        allow = ALLOWLIST + ["KnownHarmlessException"]
        findings, allowlisted = deploy_webgl.triage_console(
            ["KnownHarmlessException: seen and accepted"], allow)
        self.assertEqual(findings, [])
        self.assertEqual(allowlisted, 1)


class AllowlistFileTests(unittest.TestCase):
    def test_loader_skips_comments_and_blanks(self):
        tmp = tempfile.mkdtemp()
        try:
            path = os.path.join(tmp, "allow.txt")
            with io.open(path, "w", encoding="utf-8") as f:
                f.write("# a comment\n\nHidden/CoreSRP/CoreCopy\n  spaced entry  \n")
            self.assertEqual(deploy_webgl.load_allowlist(path),
                             ["Hidden/CoreSRP/CoreCopy", "spaced entry"])
        finally:
            shutil.rmtree(tmp)

    def test_missing_file_is_empty_allowlist(self):
        self.assertEqual(deploy_webgl.load_allowlist(os.path.join("no", "such", "file.txt")), [])

    def test_shipped_allowlist_covers_the_clean_fixture(self):
        shipped = deploy_webgl.load_allowlist(deploy_webgl.ALLOWLIST_PATH)
        self.assertTrue(shipped, "seeded allowlist file is missing or empty")
        findings, allowlisted = triage_text(CLEAN_FIXTURE, allowlist=shipped)
        self.assertEqual(findings, [])
        self.assertEqual(allowlisted, 3)


class JsonSanityTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.build = os.path.join(self.tmp, "WebGL")
        self.sa = os.path.join(self.build, "StreamingAssets", "pokelab")
        os.makedirs(self.sa)

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def write(self, name, text):
        with io.open(os.path.join(self.sa, name), "w", encoding="utf-8") as f:
            f.write(text)

    def test_all_good_json(self):
        self.write("species.json", json.dumps({"species": [1, 2, 3]}))
        self.write("dialogue.json", json.dumps({"lines": []}))
        checked, errors = deploy_webgl.check_streaming_assets_json(self.build)
        self.assertEqual(checked, 2)
        self.assertEqual(errors, [])

    def test_truncated_json_is_reported_with_relative_path(self):
        self.write("species.json", '{"species": [1, 2')  # the class that shipped once
        checked, errors = deploy_webgl.check_streaming_assets_json(self.build)
        self.assertEqual(checked, 1)
        self.assertEqual(len(errors), 1)
        self.assertIn("species.json", errors[0][0])

    def test_utf8_bom_is_tolerated(self):
        # Unity/Windows tooling loves to prepend a BOM; that is not a data error
        with io.open(os.path.join(self.sa, "bom.json"), "w", encoding="utf-8-sig") as f:
            f.write('{"ok": true}')
        checked, errors = deploy_webgl.check_streaming_assets_json(self.build)
        self.assertEqual((checked, errors), (1, []))

    def test_non_json_files_are_ignored(self):
        self.write("notes.txt", "not json {")
        checked, errors = deploy_webgl.check_streaming_assets_json(self.build)
        self.assertEqual((checked, errors), (0, []))

    def test_missing_streaming_assets_is_zero_files_not_an_error(self):
        empty_build = os.path.join(self.tmp, "EmptyBuild")
        os.makedirs(empty_build)
        checked, errors = deploy_webgl.check_streaming_assets_json(empty_build)
        self.assertEqual((checked, errors), (0, []))


class PreflightMissingBuildTests(unittest.TestCase):
    def test_missing_build_dir_fails_with_clear_message(self):
        with self.assertRaises(deploy_webgl.GateFailure) as ctx:
            deploy_webgl.step_preflight(os.path.join(self.id(), "nope", "Build", "WebGL"), force=False)
        self.assertIn("Build/WebGL not found", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()
