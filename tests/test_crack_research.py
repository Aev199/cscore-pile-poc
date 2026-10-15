"""The research-only SLS probe must never silently 'pass' an incomplete crack check."""
import json
import pathlib
import sys
import unittest
from unittest.mock import patch

ROOT=pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from tools import run_crack_research as research

class CrackResearchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.reference=json.loads(research.REFERENCE.read_text(encoding="utf-8"))

    def test_both_sections_match_report_reference_cages(self):
        for section in ("P800","P1000"):
            req=research.prepare_request(section,self.reference)
            self.assertEqual(req["schema_version"],1)
            self.assertEqual(len(req["candidates"]),1)
            self.assertEqual(len(req["sls_probe"]["loads"]),8)
            self.assertEqual(
                req["sls_probe"]["loads"][0]["n_kn"],
                self.reference["groups"][section]["cases"][0]["n_kn"])
            self.assertEqual(
                req["sls_probe"]["short_materials"],
                req["sls_probe"]["long_materials"])
            self.assertEqual(len(req["loads"]),1)

    def fake_answer(self,section,*,cracked=True,converged=True,status="research_calculated"):
        req=research.prepare_request(section,self.reference)
        sls=[]
        for r in req["sls_probe"]["loads"]:
            sls.append({
                "id":r["id"],"status":status,"error":None if status=="research_calculated" else "internal",
                "crc_converged":converged,"verified_for_design":False,
                "cracked":cracked,"width_long_mm":.12,"width_short_mm":.16,
                "utilization_long":.4,"utilization_short":.4
            })
        return {"schema_version":1,"cases":[{
            "id":req["candidates"][0]["id"],"sls_probe":sls
        }]}

    def test_valid_diagnostic_never_signals_design_approval(self):
        rows=research.check_answer("P800",self.reference,self.fake_answer("P800"))
        self.assertEqual(len(rows),8)
        self.assertTrue(all(r["CScore_status"]=="computed_experimental" for r in rows))
        self.assertTrue(all(r["design_status"]=="NOT_VERIFIED" for r in rows))

    def test_cracking_report_with_zero_width_fallback_is_indeterminate(self):
        rows=research.check_answer("P800",self.reference,self.fake_answer("P800",cracked=False))
        byid={r["case"]:r for r in rows}
        self.assertEqual(byid["P800_T02"]["CScore_status"],"indeterminate_requires_arbat")
        self.assertIsNone(byid["P800_T02"]["CScore_util_long"])

    def test_nonconservative_sls_verdict_disagreement_is_flagged(self):
        # ARBAT P800 T07 reports long crack utilization 1.360 (>1).
        # An experimental CScore utilization of 0.4 MUST be visibly flagged.
        rows=research.check_answer("P800",self.reference,self.fake_answer("P800"))
        row=next(r for r in rows if r["case"]=="P800_T07")
        self.assertTrue(row["disagrees_on_long_term_pass_fail"])
        self.assertEqual(row["CScore_status"],"computed_experimental")
        self.assertEqual(row["design_status"],"NOT_VERIFIED")

    def test_precrack_compatibility_is_study_only_and_inferred_psi_is_not_assumed(self):
        result=self.fake_answer("P800")
        load=next(row for row in result["cases"][0]["sls_probe"] if row["id"]=="P800_T07")
        load["psi_s"]=0.4609227937888848
        load["sigma_rebar_kpa"]=269388.6114121997
        load["utilization_long"]=0.6953371677673083
        load["sigma_rebar_precrack_kpa"]=30000
        load["psi_s_precrack_hypothesis"]=0.91
        load["width_long_precrack_hypothesis_mm"]=0.411
        load["utilization_long_precrack_hypothesis"]=1.37
        load["compatibility_error"]=None
        rows=research.check_answer("P800",self.reference,result)
        row=next(item for item in rows if item["case"]=="P800_T07")
        self.assertEqual(row["design_status"],"NOT_VERIFIED")
        self.assertAlmostEqual(row["psi_implied_by_arbat_if_sigma_ls_equal"],0.902,places=2)
        self.assertAlmostEqual(row["sigma_s_crc_MPa_implied_if_other_terms_equal"],33.2,places=0)
        self.assertAlmostEqual(row["precrack_hypothesis_delta_pct"],(1.37/1.36-1)*100)
        self.assertTrue(row["disagrees_on_long_term_pass_fail"])

    def test_sp63_psi_one_screening_is_a_separate_nonapproving_result(self):
        result=self.fake_answer("P800")
        record=next(v for v in result["cases"][0]["sls_probe"] if v["id"]=="P800_T07")
        record["width_long_psi_one_mm"]=0.45
        record["utilization_long_psi_one"]=1.5
        rows=research.check_answer("P800",self.reference,result)
        row=next(v for v in rows if v["case"]=="P800_T07")
        self.assertEqual(row["CScore_status"],"computed_experimental")
        self.assertEqual(row["design_status"],"NOT_VERIFIED")
        self.assertAlmostEqual(row["util_long_psi_one"],1.5)
        self.assertFalse(row["psi_one_vs_arbat_verdict_diff"])
        self.assertTrue(row["disagrees_on_long_term_pass_fail"])

    def test_solver_nonconvergence_never_yields_result(self):
        rows=research.check_answer("P1000",self.reference,self.fake_answer("P1000",converged=False))
        self.assertTrue(all(r["CScore_status"]=="indeterminate_requires_arbat" for r in rows))
        self.assertTrue(all(r["CScore_util_short"] is None for r in rows))

    def test_missing_sls_result_is_not_a_pass(self):
        result=self.fake_answer("P800")
        result["cases"][0]["sls_probe"].pop()
        with self.assertRaisesRegex(ValueError,"Incomplete"):
            research.check_answer("P800",self.reference,result)

    def test_output_provenance_requires_false_verified(self):
        result=self.fake_answer("P800")
        result["cases"][0]["sls_probe"][0]["verified_for_design"]=True
        rows=research.check_answer("P800",self.reference,result)
        self.assertEqual(rows[0]["CScore_status"],"indeterminate_requires_arbat")

if __name__=="__main__":
    unittest.main()
