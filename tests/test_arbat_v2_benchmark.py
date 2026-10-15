import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from tools import arbat_v2_benchmark as v2

class ArbatV2BenchmarkTests(unittest.TestCase):
    def test_reference_contains_all_eight_cases_for_two_sections(self):
        reference = json.loads(v2.REFERENCE.read_text(encoding="utf-8"))
        self.assertEqual(set(reference["groups"]), {"P800", "P1000"})
        for section, obj in reference["groups"].items():
            self.assertEqual(len(obj["cases"]), 8)
            self.assertEqual([r["id"] for r in obj["cases"]],
                             [f"{section}_T{i:02d}" for i in range(1, 9)])
            self.assertTrue(all(r["qz_kn"] == 0 and r["arbat_moment_util"] > 0
                                for r in obj["cases"]))

    def test_generated_accidental_eccentricity_changes_only_compressed_moments(self):
        with tempfile.TemporaryDirectory() as t:
            target = Path(t)
            v2.corrected_inputs(target)
            for d in (800, 1000):
                before = json.loads((v2.INPUTS / f"P{d}_NM8.json").read_text(encoding="utf-8"))
                after = json.loads((target / f"P{d}_NM8_ea.json").read_text(encoding="utf-8"))
                self.assertEqual(before["pile"], after["pile"])
                self.assertEqual(before["materials"], after["materials"])
                self.assertEqual(before["search"], after["search"])
                self.assertEqual([r["id"] for r in before["loads"]],
                                 [r["id"] for r in after["loads"]])
                ecc = max(3000/600, d/30, 10)/1000
                for original, transformed in zip(before["loads"], after["loads"]):
                    self.assertEqual(original["n_kn"], transformed["n_kn"])
                    self.assertEqual(original["my_knm"], transformed["my_knm"])
                    if original["n_kn"] < 0:
                        self.assertAlmostEqual(
                            transformed["mx_knm"],
                            original["mx_knm"] + abs(original["n_kn"]) * ecc
                        )
                    else:
                        self.assertEqual(original["mx_knm"], transformed["mx_knm"])

if __name__ == "__main__":
    unittest.main()
