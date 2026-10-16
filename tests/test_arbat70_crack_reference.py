"""ARBAT 70-case crack-width fixture provenance and nonapproval checks."""
import json
from pathlib import Path
import sys
import unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from tools import compare_arbat70_crackwidth as benchmark


class Arbat70ResearchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.reference=json.loads(benchmark.REF.read_text(encoding="utf-8"))

    def build_fake(self):
        rows=[]
        for section,ref in self.reference["sections"].items():
            for idx,case_id in enumerate(benchmark.case_names):
                long_value=ref["long"][idx]
                short_value=ref["short"][idx]
                rows.append({
                    "section_id":section,"case_id":case_id,
                    "cscore_status":"research_calculated",
                    "preliminary_utilization":long_value if long_value is not None else .1,
                    "preliminary_utilization_short":short_value if short_value is not None else .1,
                    "native_utilization":.1,
                    "native_utilization_short":.1,
                    "Mx_total_kNm":160 if idx==8 else 60,
                    "Mx_long_kNm":80 if idx==8 else 60,
                })
        return rows

    def test_independent_reference_keeps_all_70_cases_and_43_nulls(self):
        sections=self.reference["sections"]
        self.assertEqual(len(sections),7)
        self.assertEqual(sum(len(s["long"]) for s in sections.values()),70)
        self.assertEqual(sum(x is not None for s in sections.values() for x in s["long"]),27)
        self.assertEqual(sum(x is not None for s in sections.values() for x in s["short"]),27)
        self.assertEqual(sections["C800_16d25_cover55"]["long"],[None]*10)
        self.assertEqual(sections["C1000_20d28_cover70"]["long"],[None]*10)

    def test_no_phantom_zero_and_geometry_unverified_is_not_calibration(self):
        rows,stats=benchmark.compare(self.reference,self.build_fake())
        self.assertEqual(len(rows),70)
        self.assertEqual(stats["coverage"]["arbat_printed"],27)
        self.assertEqual(stats["coverage"]["preliminarily_comparable"],9)
        self.assertEqual(stats["comparable_long_mape_percent"],0)
        bad=next(x for x in rows if x["section_id"]=="R400x600_6d20_cover50"
                 and x["case_id"]=="T05_biax")
        self.assertEqual(bad["class"],"GEOMETRY_UNVERIFIED")
        no=next(x for x in rows if x["section_id"]=="C1000_20d28_cover70"
                and x["case_id"]=="T02_high_Mx")
        self.assertEqual(no["class"],"ARBAT_NOT_REPORTED")
        self.assertIsNone(no["arbat_long_width_mm"])
        self.assertTrue(all(r["preliminary_only_not_design_approved"] for r in rows))

    def test_different_long_axial_component_not_used_for_match(self):
        rows,_=benchmark.compare(self.reference,self.build_fake())
        last=next(x for x in rows if x["section_id"]=="C600_8d20_cover50"
                  and x["case_id"]=="T10_partial_biax")
        self.assertEqual(last["class"],"N_LONG_NOT_MATCHED_T10")

    def test_supplemental_short_only_is_excluded_from_agreement_mape(self):
        data=self.build_fake()
        target=next(r for r in data
                    if r["section_id"]=="R500x700_8d25_cover60"
                    and r["case_id"]=="T09_partial_sustained")
        target["crack_solver_path"]="supplemental_full_load_Acrc2"
        target["preliminary_utilization"]=0.0
        target["preliminary_utilization_short"]=0.55
        rows,stats=benchmark.compare(self.reference,data)
        found=next(r for r in rows
                    if r["section_id"]=="R500x700_8d25_cover60"
                    and r["case_id"]=="T09_partial_sustained")
        self.assertEqual(found["class"],"SHORT_ONLY_RECONSTRUCTION_UNVERIFIED")
        self.assertEqual(stats["coverage"]["preliminarily_comparable"],9)
        self.assertTrue(stats["not_design_approved"])

    def test_bad_t09_duration_is_rejected(self):
        data=self.build_fake()
        row=next(x for x in data if x["section_id"]=="C600_8d20_cover50"
                 and x["case_id"]=="T09_partial_sustained")
        row["Mx_long_kNm"]=160
        with self.assertRaisesRegex(ValueError,"T09"):
            benchmark.compare(self.reference,data)

    def test_unconverged_results_are_not_fabricated(self):
        data=self.build_fake()
        row=next(x for x in data if x["section_id"]=="C600_8d20_cover50"
                 and x["case_id"]=="T02_high_Mx")
        row["cscore_status"]="indeterminate"
        row["preliminary_utilization"]=""
        row["preliminary_utilization_short"]=""
        results,metrics=benchmark.compare(self.reference,data)
        self.assertEqual(metrics["coverage"]["preliminarily_comparable"],8)
        result=next(x for x in results if x["section_id"]==row["section_id"]
                    and x["case_id"]==row["case_id"])
        self.assertEqual(result["class"],"CSCORE_INDETERMINATE")


if __name__=="__main__":
    unittest.main()
