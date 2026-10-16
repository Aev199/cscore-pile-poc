"""Contract tests for new research sections and conservative SLS method selection.

No mock result constitutes an independent crack-width reference.
"""
import json
import math
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from tools import cross_section_sls_matrix as matrix


class ResearchMatrixTests(unittest.TestCase):
    def test_rectangles_and_circles_have_unique_labeled_geometry(self):
        sections = matrix.section_definitions()
        self.assertEqual(len(sections), 7)
        self.assertEqual(sum(x["kind"] == "rectangle" for x in sections), 4)
        self.assertEqual(sum(x["kind"] == "circle" for x in sections), 3)
        self.assertEqual(len({x["id"] for x in sections}), len(sections))
        for sec in sections:
            req = matrix.make_request(sec)
            self.assertEqual(req["schema_version"], 1)
            self.assertEqual(len(req["candidates"]), 1)
            self.assertEqual(req["candidates"][0]["id"], sec["id"])
            self.assertEqual(req["sls_probe"]["preliminary_method"], "arbat_conservative")
            self.assertEqual(req["sls_probe"]["review_band"], 0.03)
            self.assertEqual(len(req["sls_probe"]["loads"]), 10)
            if sec["kind"] == "rectangle":
                rect = req["research_rectangle"]
                self.assertEqual(len(rect["bars"]), len(sec["bars"]))
                for bar in rect["bars"]:
                    self.assertLess(abs(bar["x_mm"]) + bar["diameter_mm"]/2,
                                    rect["width_mm"]/2)
                    self.assertLess(abs(bar["y_mm"]) + bar["diameter_mm"]/2,
                                    rect["height_mm"]/2)
                self.assertEqual(req["pile"]["diameter_mm"], 0)
            else:
                self.assertNotIn("research_rectangle", req)
                self.assertGreater(req["candidates"][0]["bar_axis_radius_mm"], 0)

    def test_arbat_stated_geometry_does_not_mutate_original_test_matrix(self):
        original=matrix.section_definitions()
        aligned=matrix.section_definitions("arbat_stated")
        self.assertEqual([x["id"] for x in original],[x["id"] for x in aligned])
        self.assertEqual(len(aligned),7)
        by_id={x["id"]:x for x in aligned}
        # R400x600 ARBAT S1/S2 groups of 3 on opposite faces.
        r6=by_id["R400x600_6d20_cover50"]
        self.assertEqual({p["x_mm"] for p in r6["bars"]},{-140.0,0.0,140.0})
        self.assertEqual({p["y_mm"] for p in r6["bars"]},{-240.0,240.0})
        self.assertNotEqual(original[1]["bars"],r6["bars"])
        # R500x700 has four bars on each opposed face.
        r8=by_id["R500x700_8d25_cover60"]
        self.assertEqual(len({b["x_mm"] for b in r8["bars"]}),4)
        self.assertEqual(len({b["y_mm"] for b in r8["bars"]}),2)
        # Two rows: 16mm bar diameter + 20mm free spacing = 36mm pitch.
        layer=by_id["R400x400_8d16_two_layers_cover35"]
        self.assertEqual(sorted({b["y_mm"] for b in layer["bars"]}),
                         [-157.0,-121.0,121.0,157.0])
        self.assertEqual(len(layer["bars"]),8)
        # Circular reinforcement must stay exactly identical.
        for a,b in zip(original[4:],aligned[4:]):
            self.assertEqual(a["bar_count"],b["bar_count"])
            self.assertEqual(a["cover_to_longitudinal_bar_surface_mm"],
                             b["cover_to_longitudinal_bar_surface_mm"])
        with self.assertRaises(ValueError):
            matrix.section_definitions("bogus")

    def test_arbat_stated_manifest_records_uncertainty(self):
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)
            matrix.generate_cases(path,matrix.section_definitions("arbat_stated"),
                                  layout="arbat_stated")
            manifest=json.loads((path/"manifest.json").read_text(encoding="utf-8"))
            self.assertEqual(manifest["layout"],"arbat_stated")
            self.assertTrue(manifest["geometry_hypothesis_not_independently_verified"])
            self.assertEqual(len(manifest["sections"]),7)
            self.assertEqual(len(matrix.loads()),10)

    def test_full_and_sustained_bending_are_not_confused(self):
        rows = matrix.loads()
        self.assertEqual(len(rows), 10)
        partial = [x for x in rows if x["id"].startswith("T09") or x["id"].startswith("T10")]
        self.assertEqual(len(partial), 2)
        for row in partial:
            self.assertLess(math.hypot(row["mx_long_knm"], row["my_long_knm"]),
                            math.hypot(row["mx_total_knm"], row["my_total_knm"]))
        self.assertTrue(any(row["n_kn"] > 0 for row in rows))
        self.assertTrue(any(row["n_kn"] < 0 for row in rows))
        self.assertTrue(any(row["mx_total_knm"] < 0 for row in rows))
        self.assertTrue(any(row["my_total_knm"] != 0 and row["mx_total_knm"] != 0 for row in rows))

    def test_native_refined_choice_is_explicit(self):
        section = matrix.section_definitions()[0]
        native = matrix.make_request(section, "native_refined")
        self.assertEqual(native["sls_probe"]["preliminary_method"], "native_refined")
        with self.assertRaises(ValueError):
            matrix.make_request(section, "unknown")

    def test_reference_schedule_never_invents_arbats_results(self):
        with tempfile.TemporaryDirectory() as temp:
            p = Path(temp)
            manifest = matrix.generate_cases(p, matrix.section_definitions())
            self.assertEqual(len(manifest), 7)
            csv = (p/"arbat_reference_TO_FILL.csv").read_text(encoding="utf-8-sig")
            self.assertIn("NOT_CALCULATED", csv)
            self.assertNotIn("0.000", csv)
            self.assertEqual(csv.count("NOT_CALCULATED"), 70)
            for sec in manifest:
                req = json.loads((p/sec["input_file"]).read_text(encoding="utf-8"))
                self.assertEqual(req["candidates"][0]["id"], sec["id"])

    def fake_output(self, section, status="research_calculated", preliminary=0.99, native=0.95):
        req = matrix.make_request(section)
        records = []
        for l in req["sls_probe"]["loads"]:
            records.append({
                "id":l["id"], "status":status,
                "error":None if status=="research_calculated" else "crack_not_converged",
                "preliminary_is_final_approval":False,
                "verified_for_design":False,
                "preliminary_method":"arbat_conservative",
                "preliminary_basis":"precrack_compatibility_envelope_with_native",
                "preliminary_decision":"engineering_review" if status=="research_calculated" else "indeterminate",
                "preliminary_utilization_long":preliminary if status=="research_calculated" else None,
                "preliminary_utilization_short":0.40 if status=="research_calculated" else None,
                "preliminary_governing_utilization":max(preliminary,0.40) if status=="research_calculated" else None,
                "utilization_long":native if status=="research_calculated" else None,
                "utilization_short":0.35 if status=="research_calculated" else None,
            })
        return req, {"schema_version":1,"cases":[{"id":section["id"],"sls_probe":records}]}

    def test_near_unity_is_review_not_design_pass(self):
        sec = matrix.section_definitions()[0]
        req, answer = self.fake_output(sec)
        rows = matrix.validate_answer(sec, req, answer)
        self.assertEqual(len(rows), 10)
        self.assertTrue(all(x["preliminary_status"] == "engineering_review" for x in rows))
        self.assertTrue(all(x["final_design_status"] == "NOT_VERIFIED" for x in rows))
        self.assertTrue(all(x["arbat_report_status"] == "NOT_PROVIDED" for x in rows))

    def test_short_term_can_govern_the_conservative_research_screen(self):
        sec = matrix.section_definitions()[0]
        req, answer = self.fake_output(sec, preliminary=0.7, native=0.6)
        for row in answer["cases"][0]["sls_probe"]:
            row["preliminary_utilization_short"]=1.014
            row["utilization_short"]=0.95
            row["preliminary_governing_utilization"]=1.014
            row["preliminary_decision"]="engineering_review"
        rows=matrix.validate_answer(sec,req,answer)
        self.assertTrue(all(r["preliminary_status"]=="engineering_review" for r in rows))
        self.assertTrue(all(r["final_design_status"]=="NOT_VERIFIED" for r in rows))

    def test_default_must_not_underestimate_native(self):
        sec = matrix.section_definitions()[0]
        req, answer = self.fake_output(sec, preliminary=0.90, native=0.95)
        with self.assertRaisesRegex(ValueError,"less conservative"):
            matrix.validate_answer(sec, req, answer)

    def test_indeterminate_is_preserved(self):
        sec = matrix.section_definitions()[0]
        req, answer = self.fake_output(sec,status="indeterminate")
        rows = matrix.validate_answer(sec, req, answer)
        self.assertTrue(all(r["cscore_status"]=="indeterminate" for r in rows))
        self.assertTrue(all(r["preliminary_utilization"] is None for r in rows))

    def test_cannot_relabel_as_design_approved(self):
        sec = matrix.section_definitions()[0]
        req, answer = self.fake_output(sec)
        answer["cases"][0]["sls_probe"][0]["verified_for_design"] = True
        with self.assertRaisesRegex(ValueError,"never be approved"):
            matrix.validate_answer(sec, req, answer)

    def test_incomplete_results_cannot_silently_pass(self):
        sec = matrix.section_definitions()[0]
        req, answer = self.fake_output(sec)
        answer["cases"][0]["sls_probe"].pop()
        with self.assertRaisesRegex(ValueError,"Incomplete"):
            matrix.validate_answer(sec, req, answer)


if __name__=="__main__":
    unittest.main()
