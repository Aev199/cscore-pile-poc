import copy
import json
import math
from pathlib import Path
import struct
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from tools import diagnose_arbat_inputs as diagnostic
from tools import arbat_sav_export as arbat


class DiagnosticGeneratorTests(unittest.TestCase):
    def test_sensitivity_scenarios_preserve_reference_forces_and_geometry(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = diagnostic.generate(Path(tmp))
            self.assertEqual(16, len(output))
            for record in output:
                source = json.loads((Path(tmp)/record["input"]).read_text(encoding="utf-8"))
                group = record["section"]
                d = int(group[1:])
                bar_phi = source["search"]["bar_diameters_mm"][0]
                cover = source["pile"]["cover_mm"]
                tie = source["pile"]["ties_diameter_mm"]
                r = d / 2 - cover - tie - bar_phi / 2
                self.assertAlmostEqual(r, record["axis_radius_mm"])
                if record["cover_hypothesis"] == "bar_surface":
                    self.assertAlmostEqual(r, d / 2 - 70 - bar_phi / 2)
                else:
                    self.assertAlmostEqual(r, d / 2 - 70)
                originals = json.loads(
                    (diagnostic.BASE / f"{group}_reference.json").read_text(encoding="utf-8")
                )["loads"]
                for a, b in zip(originals, source["loads"]):
                    self.assertEqual(a["id"], b["id"])
                    self.assertEqual(a["n_kn"], b["n_kn"])
                    self.assertEqual(a["my_knm"], b["my_knm"])
                    if not record["added_accidental_eccentricity"] or a["n_kn"] >= 0:
                        self.assertEqual(a["mx_knm"], b["mx_knm"])
                    else:
                        ea = record["ea_mm"] / 1000
                        expected = a["mx_knm"] + math.copysign(abs(a["n_kn"]) * ea, a["mx_knm"])
                        self.assertAlmostEqual(expected, b["mx_knm"])


class ArbatSavExportTests(unittest.TestCase):
    def template(self):
        raw = bytearray(arbat.SIZE)
        raw[:len(arbat.MAGIC)] = arbat.MAGIC
        struct.pack_into("<I", raw, arbat.COUNT_AT, arbat.LOADS)
        for i in range(arbat.LOADS):
            struct.pack_into("<ddd", raw, arbat.FIRST+i*arbat.STRIDE, -1000, 2000, 3000)
        return bytes(raw)

    def loads(self):
        return [
            {"case_id":f"S{i:02d}", "N_kN":-1000-i*10,
             "My_kNm":20*i, "Qz_kN":((-1)**i)*200}
            for i in range(arbat.LOADS)
        ]

    def test_patch_changes_only_confirmed_force_slots(self):
        original = self.template()
        modified = arbat.patch_eight(original, self.loads())
        allowed = {j for i in range(arbat.LOADS)
                   for j in range(arbat.FIRST+i*arbat.STRIDE,
                                  arbat.FIRST+i*arbat.STRIDE+24)}
        self.assertEqual(len(modified), len(original))
        self.assertTrue(all(i in allowed for i,(x,y) in enumerate(zip(original,modified)) if x!=y))
        self.assertEqual(arbat.patch_eight(modified,self.loads()),modified)
        for i,row in enumerate(self.loads()):
            actual = struct.unpack_from("<ddd",modified,arbat.FIRST+i*arbat.STRIDE)
            self.assertEqual(actual, (row["N_kN"]*1000,row["My_kNm"]*1000,row["Qz_kN"]*1000))

    def test_unknown_template_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "not one of the two verified"):
            arbat.validate_reference_template(self.template())

    def test_nonfinite_force_is_rejected(self):
        rows=self.loads()
        rows[0]["N_kN"]=math.nan
        with self.assertRaises(ValueError):
            arbat.patch_eight(self.template(),rows)

    def test_bad_number_of_forces_is_rejected(self):
        with self.assertRaises(ValueError):
            arbat.patch_eight(self.template(),self.loads()[:7])


if __name__ == "__main__":
    unittest.main()
