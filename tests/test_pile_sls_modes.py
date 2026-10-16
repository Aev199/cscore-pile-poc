"""Optional experimental SLS in ordinary pile_rebar: never final design approval."""
import copy
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import pile_rebar as main


class PileSlsOptionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.example = json.loads(
            (ROOT/"examples/verification/P800_sls_preselection_example.json").read_text(encoding="utf-8")
        )

    def test_default_conservative_sls_is_explicit_and_loads_independent(self):
        data=copy.deepcopy(self.example)
        main.validate_input(data)
        candidates, _ = main.make_candidates(data)
        request = main.build_engine_request(data, candidates)
        self.assertEqual(request["sls_probe"]["preliminary_method"],"arbat_conservative")
        self.assertEqual(len(request["sls_probe"]["loads"]),4)
        self.assertEqual(len(request["loads"]),3)
        self.assertEqual(request["sls_probe"]["review_band"],.03)

    def test_optional_native_refined_is_available(self):
        data=copy.deepcopy(self.example)
        data["sls_probe"]["preliminary_method"]="native_refined"
        main.validate_input(data)
        self.assertEqual(
            main.build_engine_request(data,main.make_candidates(data)[0])["sls_probe"]["preliminary_method"],
            "native_refined"
        )

    def test_malformed_service_material_is_rejected(self):
        data=copy.deepcopy(self.example)
        data["sls_probe"]["short_materials"]["concrete_rbt_kpa"]=-5
        with self.assertRaises(main.InputError):
            main.validate_input(data)

    def test_duplicate_or_nonfinite_service_load_is_rejected(self):
        data=copy.deepcopy(self.example)
        data["sls_probe"]["loads"][1]["id"]=data["sls_probe"]["loads"][0]["id"]
        with self.assertRaises(main.InputError):
            main.validate_input(data)
        data=copy.deepcopy(self.example)
        data["sls_probe"]["loads"][0]["n_kn"]=float("nan")
        with self.assertRaises(main.InputError):
            main.validate_input(data)

    def fake_rows(self, utilization=.989, native=.95, mode="arbat_conservative"):
        return [{
            "id":l["id"],"status":"research_calculated","error":None,
            "verified_for_design":False,"preliminary_is_final_approval":False,
            "preliminary_method":mode,"preliminary_basis":"precrack_compatibility_envelope_with_native",
            "preliminary_decision":(
                "engineering_review" if abs(utilization-1)<=.03
                else "preliminary_exceeds_limit" if utilization>1
                else "preliminary_below_limit"
            ),
            "preliminary_utilization_long":utilization,
            "preliminary_utilization_short":0.5,
            "preliminary_governing_utilization":max(utilization, 0.5),
            "utilization_long":native,
            "utilization_short":0.4
        } for l in self.example["sls_probe"]["loads"]]

    def test_short_only_supplement_is_mandatory_engineering_review(self):
        from copy import deepcopy
        sample=deepcopy(self.example["sls_probe"])
        rows=self.fake_rows(utilization=0.0,native=0.0)
        target=rows[0]
        target.update({
            "preliminary_basis":"short_only_supplemental_full_load_solver",
            "preliminary_review_reason":"cracked_only_under_total_load_unvalidated_mcrc_threshold",
            "preliminary_utilization_long":0.0,
            "utilization_long":0.0,
            "preliminary_utilization_short":0.72,
            "utilization_short":0.63,
            "preliminary_governing_utilization":0.72,
            "preliminary_decision":"engineering_review",
        })
        checked=main.check_sls_results(rows,sample,"arbat_conservative",0.03)
        self.assertEqual(checked[0]["preliminary_decision"],"engineering_review")
        self.assertEqual(checked[0]["design_status"],"NOT_VERIFIED")
        target["preliminary_decision"]="preliminary_below_limit"
        with self.assertRaises(main.InputError):
            main.check_sls_results(rows,sample,"arbat_conservative",0.03)

    def test_short_crack_check_also_controls_candidate(self):
        rows=self.fake_rows(utilization=.80,native=.70)
        for r in rows:
            r["preliminary_utilization_short"]=1.014
            r["utilization_short"]=1.01
            r["preliminary_governing_utilization"]=1.014
            r["preliminary_decision"]="engineering_review"
        validated=main.check_sls_results(
            rows,self.example["sls_probe"],"arbat_conservative",.03
        )
        self.assertTrue(all(v["preliminary_decision"]=="engineering_review"
                            for v in validated))

    def test_near_boundary_989_and_1014_both_require_review(self):
        for utilization in (.989,1.014):
            rows=main.check_sls_results(
                self.fake_rows(utilization), self.example["sls_probe"],
                "arbat_conservative", .03
            )
            self.assertTrue(all(r["preliminary_decision"]=="engineering_review" for r in rows))
            self.assertTrue(all(r["design_status"]=="NOT_VERIFIED" for r in rows))

    def test_default_can_never_be_less_conservative_than_native(self):
        with self.assertRaisesRegex(main.InputError,"менее консервативен"):
            main.check_sls_results(
                self.fake_rows(utilization=.92,native=.99),
                self.example["sls_probe"],"arbat_conservative",.03
            )

    def test_native_user_selection_remains_non_approved(self):
        source=copy.deepcopy(self.example["sls_probe"])
        source["preliminary_method"]="native_refined"
        rows=main.check_sls_results(
            self.fake_rows(utilization=.90,native=.98,mode="native_refined"),
            source,"native_refined",.03
        )
        self.assertTrue(all(r["design_status"]=="NOT_VERIFIED" for r in rows))

    def test_missing_sls_results_are_rejected(self):
        with self.assertRaisesRegex(main.InputError,"неполный"):
            main.check_sls_results(
                self.fake_rows()[:2],self.example["sls_probe"],"arbat_conservative",.03
            )

    def test_failed_upstream_solver_is_indeterminate(self):
        rows=self.fake_rows()
        rows[0]={"id":rows[0]["id"],"status":"indeterminate","error":"solver_no_convergence",
                 "verified_for_design":False}
        result=main.check_sls_results(
            rows,self.example["sls_probe"],"arbat_conservative",.03
        )
        self.assertEqual(result[0]["design_status"],"NOT_VERIFIED")
        self.assertEqual(result[0]["status"],"indeterminate")

    def test_end_to_end_research_cage_requires_all_sls_below_review_band(self):
        import tempfile
        from unittest.mock import patch
        from types import SimpleNamespace
        for trial in (0.91, 0.989):
            with tempfile.TemporaryDirectory() as temp:
                folder=Path(temp)
                inp=folder/"input.json"
                inp.write_text(json.dumps(self.example),encoding="utf-8")
                def fake_run(argv, **kwargs):
                    req=json.loads(kwargs["input"])
                    sls = self.fake_rows(
                        utilization=trial,native=0.85,mode="arbat_conservative"
                    )
                    return SimpleNamespace(
                        returncode=0,stderr="",
                        stdout=json.dumps({
                            "schema_version":1,
                            "cases":[{
                                "id":c["id"],
                                "loads":[{
                                    "id":l["id"],"converged":True,"error":None,
                                    "factor":2.0,"utilization":0.5,
                                } for l in req["loads"]],
                                "sls_probe":sls,
                            } for c in req["candidates"]]
                        })
                    )
                with patch.object(main.subprocess,"run",side_effect=fake_run):
                    result=main.run(inp,folder/"fake.exe",folder/"out.json",folder/"out.csv")
                self.assertIsNotNone(result["selected_uls_candidate"])
                self.assertEqual(result["cases"][0]["sls_probe"][0]["design_status"],"NOT_VERIFIED")
                if trial==0.91:
                    self.assertIsNotNone(result["selected_preliminary_uls_sls_research_candidate"])
                else:
                    self.assertIsNone(result["selected_preliminary_uls_sls_research_candidate"])

    def test_upstream_cannot_claim_design_approval(self):
        rows=self.fake_rows()
        rows[0]["verified_for_design"]=True
        with self.assertRaisesRegex(main.InputError,"утверждённый"):
            main.check_sls_results(
                rows,self.example["sls_probe"],"arbat_conservative",.03
            )


if __name__=="__main__":
    unittest.main()
