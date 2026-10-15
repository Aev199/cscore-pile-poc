"""Prevent a silent mismatch between build lock and runner provenance strings."""
import json
from pathlib import Path
import re
import unittest

ROOT=Path(__file__).resolve().parents[1]

class PinnedUpstreamTests(unittest.TestCase):
    def test_lock_matches_engine_source_and_runner(self):
        lock=json.loads((ROOT/"config/opencs.lock.json").read_text(encoding="utf-8"))
        self.assertEqual(lock["repository"],"rumata-ap/OpenCS")
        self.assertEqual(lock["branch"],"master")
        self.assertRegex(lock["commit"],r"^[0-9a-f]{40}$")
        runner=(ROOT/"src/CScorePileRunner/Program.cs").read_text(encoding="utf-8")
        py=(ROOT/"pile_rebar.py").read_text(encoding="utf-8")
        self.assertIn('engine_commit = "'+lock["commit"]+'"',runner)
        self.assertIn("'engine_commit': '"+lock["commit"]+"'",py)
        workflow=(ROOT/".github/workflows/build-windows.yml").read_text(encoding="utf-8")
        self.assertIn('Get-Content config/opencs.lock.json', workflow)
        self.assertIn('Copy-Item config/opencs.lock.json', workflow)

    def test_no_unreviewed_automatic_upstream_updates(self):
        watcher=(ROOT/".github/workflows/watch-opencs.yml").read_text(encoding="utf-8")
        self.assertIn("schedule:",watcher)
        self.assertIn("issues: write",watcher)
        self.assertNotIn("git push",watcher)
        self.assertNotIn("github.rest.pulls.merge",watcher)
        self.assertIn("repo: upstreamRepo",watcher)


if __name__=="__main__":
    unittest.main()
